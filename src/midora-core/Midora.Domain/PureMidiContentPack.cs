using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Midora.Common;

namespace Midora.Domain;

public enum PureMidiContentRecordKind : byte
{
    Note = 1,
    ChannelEvent = 2,
    OpaqueEvent = 3,
    NoteOnEndpoint = 4,
    NoteOffEndpoint = 5,
    ChannelEventEndpoint = 6
}

public sealed class PureMidiContentPackWriter : IDisposable
{
    public const int MaximumPageRecordCount = 65_536;
    public const int MaximumEndpointPageRecordCount = 16_384;
    public const int MaximumDecodedPageByteCount = 4 * 1024 * 1024;

    private const int HeaderByteCount = 48;
    private const int DirectoryEntryByteCount = 140;
    private const int FooterByteCount = 48;
    private const int Version = 4;
    private const long DefaultEncodeBufferBudget = 64L * 1024 * 1024;
    private static ReadOnlySpan<byte> HeaderMagic => "MIDMPK3\0"u8;
    private static ReadOnlySpan<byte> FooterMagic => "MIDMPKF\0"u8;

    private readonly string _path;
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly List<PageDescriptor> _pages = [];
    private readonly Dictionary<(MidoraId SegmentId, PureMidiContentRecordKind Kind), PageBuilder> _builders = [];
    private readonly Dictionary<(MidoraId SegmentId, PureMidiContentRecordKind Kind), int> _nextOrdinals = [];
    private readonly CancellationToken _cancellationToken;
    private readonly ConcurrentExclusiveSchedulerPair _encoderScheduler;
    private readonly Queue<PendingEncodedPage> _pendingPages = [];
    private readonly LinkedList<PageBuilder> _residentBuilders = [];
    private readonly BuilderSpool _builderSpool = new();
    private readonly int _maximumPendingPageCount;
    private readonly long _encodeBufferBudget;
    private readonly Action? _encodePageTestHook;
    private readonly Action<bool>? _beforePageWaitTestHook;
    private readonly Action? _beforeBuilderSpillTestHook;
    private long _pendingReservedBytes;
    private long _activeBufferBytes;
    private long _peakReservedBytes;
    private long _pendingActualBufferBytes;
    private long _peakActualBufferBytes;
    private bool _encoderCompleted;
    private bool _builderBuffersReleased;
    private bool _completed;
    private bool _disposed;
    private bool _streamsDisposed;

    public PureMidiContentPackWriter(
        string path,
        CancellationToken cancellationToken = default)
        : this(
            path,
            cancellationToken,
            Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2)),
            DefaultEncodeBufferBudget)
    {
    }

    internal PureMidiContentPackWriter(
        string path,
        CancellationToken cancellationToken,
        int encoderConcurrency,
        long encodeBufferBudget,
        Action? encodePageTestHook = null,
        Action<bool>? beforePageWaitTestHook = null,
        Action? beforeBuilderSpillTestHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (encoderConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(encoderConcurrency));
        if (encodeBufferBudget < MaximumDecodedPageByteCount * 2L + 65_536)
            throw new ArgumentOutOfRangeException(nameof(encodeBufferBudget));
        _path = System.IO.Path.GetFullPath(path);
        _cancellationToken = cancellationToken;
        _encoderScheduler = new(
            TaskScheduler.Default,
            maxConcurrencyLevel: encoderConcurrency);
        _maximumPendingPageCount = checked(encoderConcurrency * 2);
        _encodeBufferBudget = encodeBufferBudget;
        _encodePageTestHook = encodePageTestHook;
        _beforePageWaitTestHook = beforePageWaitTestHook;
        _beforeBuilderSpillTestHook = beforeBuilderSpillTestHook;
        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _stream = new FileStream(
            _path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        _writer = new(_stream, System.Text.Encoding.UTF8, leaveOpen: true);
        _writer.Write(new byte[HeaderByteCount]);
    }

    public string Path => _path;

    internal long ActiveBuilderCapacityBytes => _activeBufferBytes;
    internal long PendingPageReservedBytes => _pendingReservedBytes;
    internal long PeakReservedBytes => _peakReservedBytes;
    internal long PendingActualBufferBytes => _pendingActualBufferBytes;
    internal long PeakActualBufferBytes => _peakActualBufferBytes;
    internal long BuilderSpillCount => _builderSpool.SpillCount;
    internal long BuilderSpoolLength => _builderSpool.Length;
    internal string? BuilderSpoolPath => _builderSpool.Path;

    public void AddNote(MidoraId segmentId, DirectMidiNoteValue value)
    {
        ValidateSegmentId(segmentId);
        GetBuilder(segmentId, PureMidiContentRecordKind.Note).AddNote(value);
        GetBuilder(segmentId, PureMidiContentRecordKind.NoteOnEndpoint).AddNote(value);
        GetBuilder(segmentId, PureMidiContentRecordKind.NoteOffEndpoint).AddNote(value);
    }

    public void AddChannelEvent(MidoraId segmentId, DirectMidiChannelEventValue value)
    {
        ValidateSegmentId(segmentId);
        GetBuilder(segmentId, PureMidiContentRecordKind.ChannelEvent).AddChannelEvent(value);
        GetBuilder(segmentId, PureMidiContentRecordKind.ChannelEventEndpoint)
            .AddChannelEvent(value);
    }

    public void AddOpaqueEvent(MidoraId segmentId, OpaqueMidiEventValue value)
    {
        ValidateSegmentId(segmentId);
        GetBuilder(segmentId, PureMidiContentRecordKind.OpaqueEvent).AddOpaqueEvent(value);
    }

    public PureMidiContentPack Complete(PureMidiContentPackDecodedCache? decodedCache = null)
    {
        ThrowIfUnavailable();
        foreach (PageBuilder builder in _builders.Values
            .OrderBy(value => value.SegmentId)
            .ThenBy(value => value.Kind))
        {
            Flush(builder);
        }
        ReleaseBuilderBuffers();
        while (_pendingPages.Count != 0) CommitOldestPage();
        CompleteEncoderScheduling();

        long directoryOffset = _stream.Position;
        using MemoryStream directoryBuffer = new(checked(_pages.Count * DirectoryEntryByteCount));
        using (BinaryWriter directoryWriter = new(
            directoryBuffer,
            System.Text.Encoding.UTF8,
            leaveOpen: true))
        {
            foreach (PageDescriptor page in _pages) WriteDirectoryEntry(directoryWriter, page);
        }
        byte[] directoryBytes = directoryBuffer.ToArray();
        byte[] directoryHash = SHA256.HashData(directoryBytes);
        _writer.Write(directoryBytes);
        long footerOffset = _stream.Position;
        _writer.Write(FooterMagic);
        _writer.Write(directoryHash);
        _writer.Write(checked(footerOffset + FooterByteCount));
        _writer.Flush();

        _stream.Position = 0;
        _writer.Write(HeaderMagic);
        _writer.Write(Version);
        _writer.Write(HeaderByteCount);
        _writer.Write(_pages.Count);
        _writer.Write(0);
        _writer.Write(directoryOffset);
        _writer.Write((long)directoryBytes.Length);
        _writer.Write(footerOffset);
        _writer.Flush();
        _stream.Flush(flushToDisk: true);
        DisposeStreams();
        PureMidiContentPack result = decodedCache is null
            ? PureMidiContentPack.Open(_path)
            : PureMidiContentPack.Open(_path, decodedCache);
        _completed = true;
        _pages.Clear();
        _pages.Capacity = 0;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? encoderFailure = StopEncoderPipeline();
        try { ReleaseBuilderBuffers(); }
        catch (Exception exception) { encoderFailure ??= exception; }
        try
        {
            DisposeStreams();
        }
        catch (Exception exception)
        {
            encoderFailure ??= exception;
        }
        if (!_completed)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
                // A failed transaction remains unpublished. Cleanup is best effort.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed transaction remains unpublished. Cleanup is best effort.
            }
        }
        if (encoderFailure is not null
            && encoderFailure is not OperationCanceledException
            && !_cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException(
                "The Pure MIDI content-pack page encoder failed.",
                encoderFailure);
        }
    }

    private PageBuilder GetBuilder(MidoraId segmentId, PureMidiContentRecordKind kind)
    {
        ThrowIfUnavailable();
        var key = (segmentId, kind);
        if (!_builders.TryGetValue(key, out PageBuilder? result))
        {
            result = new(segmentId, kind, this);
            _builders.Add(key, result);
        }
        TouchBuilder(result);
        return result;
    }

    private void TouchBuilder(PageBuilder builder)
    {
        if (builder.ResidentNode is not null)
        {
            _residentBuilders.Remove(builder.ResidentNode);
            _residentBuilders.AddLast(builder.ResidentNode);
        }
        else builder.ResidentNode = _residentBuilders.AddLast(builder);
    }

    private void EnsureBufferRoom(PageBuilder protectedBuilder, long additionalBytes)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        while (_pendingPages.Count != 0
            && _activeBufferBytes + _pendingReservedBytes + additionalBytes > _encodeBufferBudget)
            CommitOldestPage();
        while (_activeBufferBytes + _pendingReservedBytes + additionalBytes > _encodeBufferBudget)
        {
            LinkedListNode<PageBuilder>? candidate = _residentBuilders.First;
            while (candidate is not null
                && (ReferenceEquals(candidate.Value, protectedBuilder)
                    || candidate.Value.CapacityBytes == 0))
                candidate = candidate.Next;
            if (candidate is null) break;
            candidate.Value.Spill();
            _residentBuilders.Remove(candidate);
            candidate.Value.ResidentNode = null;
        }
        // The internal test budget can be smaller than one legal page's
        // encoding/growth peak. Never reject that page: after all other work is
        // drained, one protected page may borrow its measured working capacity.
        ObserveCapacityPeak(additionalBytes);
    }

    private void ObserveCapacityPeak(long additionalBytes = 0) =>
        _peakReservedBytes = Math.Max(_peakReservedBytes,
            checked(_activeBufferBytes + _pendingReservedBytes + additionalBytes));

    private void ChangeActiveCapacity(long delta)
    {
        _activeBufferBytes = checked(_activeBufferBytes + delta);
        ObserveCapacityPeak();
        ObserveActualCapacityPeak();
    }

    private void ChangePendingActualCapacity(long delta)
    {
        Interlocked.Add(ref _pendingActualBufferBytes, delta);
        ObserveActualCapacityPeak();
    }

    private void ObserveActualCapacityPeak(long additionalBytes = 0)
    {
        long value = checked(Volatile.Read(ref _activeBufferBytes)
            + Volatile.Read(ref _pendingActualBufferBytes) + additionalBytes);
        long previous = Volatile.Read(ref _peakActualBufferBytes);
        while (value > previous)
        {
            long observed = Interlocked.CompareExchange(ref _peakActualBufferBytes, value, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    private static int PooledByteCapacity(int length) =>
        checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(16, length)));

    private void Flush(PageBuilder builder)
    {
        if (builder.RecordCount == 0) return;
        _cancellationToken.ThrowIfCancellationRequested();
        builder.EnsureResident();
        int sourceBytes = builder.DecodedByteCount;
        bool endpoints = builder.IsEndpoint;
        int compressedCapacity = PooledByteCapacity(Math.Max(4096,
            BrotliEncoder.GetMaxCompressedLength(sourceBytes)));
        long reservation = checked(
            (endpoints ? builder.CapacityBytes : 0L)
            + PooledByteCapacity(sourceBytes)
            // Include both arrays temporarily alive while the compressed buffer
            // grows, and the final pool bucket (not compressed payload length).
            + compressedCapacity + compressedCapacity / 2L);
        EnsureBufferRoom(builder, reservation);
        while (_pendingPages.Count >= _maximumPendingPageCount) CommitOldestPage();
        ArraySegment<byte> decoded = builder.PreparePage();
        DirectMidiNoteValue[]? noteEndpoints = builder.TakeNoteEndpointBuffer();
        DirectMidiChannelEventValue[]? channelEndpoints = builder.TakeChannelEndpointBuffer();
        int decodedByteCount = noteEndpoints is not null
            ? checked(builder.LastPageRecordCount * 52)
            : channelEndpoints is not null
                ? checked(builder.LastPageRecordCount * 36)
                : decoded.Count;
        if (decodedByteCount > MaximumDecodedPageByteCount)
        {
            throw new InvalidDataException(
                $"A Pure MIDI content page decoded to {decodedByteCount} bytes, exceeding {MaximumDecodedPageByteCount} bytes.");
        }
        bool serializesEndpoints = noteEndpoints is not null || channelEndpoints is not null;
        byte[]? decodedBuffer = ArrayPool<byte>.Shared.Rent(decodedByteCount);
        long actualInputCapacity = decodedBuffer.LongLength
            + (noteEndpoints?.LongLength ?? 0) * Unsafe.SizeOf<DirectMidiNoteValue>()
            + (channelEndpoints?.LongLength ?? 0) * Unsafe.SizeOf<DirectMidiChannelEventValue>();
        // The array actually returned by the pool, not the requested size, owns
        // the pending bytes. Normal shared-pool buckets equal the estimate;
        // account larger returns as well before scheduling any encoder.
        reservation = checked(actualInputCapacity + compressedCapacity + compressedCapacity / 2L);
        ChangePendingActualCapacity(actualInputCapacity);
        if (!serializesEndpoints)
        {
            decoded.AsSpan().CopyTo(decodedBuffer);
        }
        var ordinalKey = (builder.SegmentId, builder.Kind);
        int firstOrdinal = _nextOrdinals.GetValueOrDefault(ordinalKey);
        _nextOrdinals[ordinalKey] = checked(firstOrdinal + builder.LastPageRecordCount);
        UnencodedPage page = new(
            builder.SegmentId,
            builder.Kind,
            firstOrdinal,
            builder.LastPageRecordCount,
            builder.LastPageMinimumTick,
            builder.LastPageMaximumTick,
            builder.LastPageMaximumActiveEndTick,
            builder.LastPageMinimumId,
            builder.LastPageMaximumId,
            builder.LastPageMinimumKey,
            builder.LastPageMaximumKey,
            builder.LastPageLaneMaskLow,
            builder.LastPageLaneMaskHigh,
            builder.LastPageMinimumRasterValue,
            builder.LastPageMaximumRasterValue,
            decodedBuffer,
            decodedByteCount,
            noteEndpoints,
            channelEndpoints,
            _cancellationToken,
            _encodePageTestHook,
            ChangePendingActualCapacity);
        Task<EncodedPage>? task = null;
        try
        {
            task = Task.Factory.StartNew(
                static state => EncodePage((UnencodedPage)state!),
                page,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                _encoderScheduler.ConcurrentScheduler);
            _pendingPages.Enqueue(new(task, reservation));
            _pendingReservedBytes = checked(_pendingReservedBytes + reservation);
            ObserveCapacityPeak();
        }
        catch
        {
            if (task is null) ReturnUnencodedBuffers(page, decodedBuffer);
            throw;
        }
        builder.ResetPage();
    }

    private static EncodedPage EncodePage(UnencodedPage page)
    {
        byte[]? decodedBuffer = page.DecodedBuffer;
        try
        {
            page.CancellationToken.ThrowIfCancellationRequested();
            page.EncodePageTestHook?.Invoke();
            if (decodedBuffer is null)
            {
                decodedBuffer = ArrayPool<byte>.Shared.Rent(page.DecodedByteCount);
                page.ChangeActualCapacity(decodedBuffer.LongLength);
            }
            if (page.NoteEndpoints is not null || page.ChannelEndpoints is not null)
                SerializeEndpoints(page, decodedBuffer);
            byte[] decodedSha256 = SHA256.HashData(
                decodedBuffer.AsSpan(0, page.DecodedByteCount));
            page.CancellationToken.ThrowIfCancellationRequested();
            int initialStoredCapacity = Math.Clamp(
                page.DecodedByteCount / 8,
                4 * 1024,
                256 * 1024);
            using PooledWriteStream compressed = new(initialStoredCapacity, page.ChangeActualCapacity);
            using (BrotliStream brotli = new(
                compressed,
                CompressionLevel.Fastest,
                leaveOpen: true))
            {
                brotli.Write(decodedBuffer.AsSpan(0, page.DecodedByteCount));
            }
            PooledBuffer stored = compressed.Detach();
            return new(page, stored, decodedSha256);
        }
        finally
        {
            ReturnUnencodedBuffers(page, decodedBuffer);
        }
    }

    private static void SerializeEndpoints(UnencodedPage page, byte[] destination)
    {
        if (page.NoteEndpoints is not null)
        {
            Array.Sort(
                page.NoteEndpoints,
                0,
                page.RecordCount,
                page.Kind == PureMidiContentRecordKind.NoteOnEndpoint
                    ? PageBuilder.NoteOnEndpointComparer.Instance
                    : PageBuilder.NoteOffEndpointComparer.Instance);
        }
        else if (page.ChannelEndpoints is not null)
        {
            Array.Sort(
                page.ChannelEndpoints,
                0,
                page.RecordCount,
                PageBuilder.ChannelEndpointComparer.Instance);
        }
        else
        {
            throw new InvalidOperationException(
                "An endpoint page has no detached source records.");
        }
        Span<byte> bytes = destination.AsSpan(0, page.DecodedByteCount);
        if (page.NoteEndpoints is not null)
        {
            for (int index = 0; index < page.RecordCount; index++)
            {
                WriteNote(bytes.Slice(index * 52, 52), page.NoteEndpoints[index]);
            }
        }
        else
        {
            DirectMidiChannelEventValue[] values = page.ChannelEndpoints!;
            for (int index = 0; index < page.RecordCount; index++)
            {
                WriteChannelEvent(bytes.Slice(index * 36, 36), values[index]);
            }
        }
    }

    private static void WriteNote(Span<byte> destination, DirectMidiNoteValue value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Id.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], value.StartTick);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], value.LengthTicks);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], value.Key);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], value.NoteOnVelocity);
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..], value.NoteOffVelocity);
        BinaryPrimitives.WriteInt64LittleEndian(destination[36..], value.NoteOnOrder);
        BinaryPrimitives.WriteInt64LittleEndian(destination[44..], value.NoteOffOrder);
    }

    private static void WriteChannelEvent(
        Span<byte> destination,
        DirectMidiChannelEventValue value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, value.Id.Value);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], value.Tick);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], (int)value.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], value.Data1);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], value.Data2);
        BinaryPrimitives.WriteInt64LittleEndian(destination[28..], value.Order);
    }

    private static void ReturnUnencodedBuffers(
        UnencodedPage page,
        byte[]? decodedBuffer)
    {
        if (decodedBuffer is not null) ArrayPool<byte>.Shared.Return(decodedBuffer);
        if (page.NoteEndpoints is not null)
            ArrayPool<DirectMidiNoteValue>.Shared.Return(page.NoteEndpoints);
        if (page.ChannelEndpoints is not null)
            ArrayPool<DirectMidiChannelEventValue>.Shared.Return(page.ChannelEndpoints);
        page.ChangeActualCapacity(-((decodedBuffer?.LongLength ?? 0)
            + (page.NoteEndpoints?.LongLength ?? 0) * Unsafe.SizeOf<DirectMidiNoteValue>()
            + (page.ChannelEndpoints?.LongLength ?? 0) * Unsafe.SizeOf<DirectMidiChannelEventValue>()));
    }

    private void CommitOldestPage()
    {
        PendingEncodedPage pending = _pendingPages.Dequeue();
        try
        {
            using EncodedPage encoded = WaitForEncodedPage(pending);
            _cancellationToken.ThrowIfCancellationRequested();
            long offset = _stream.Position;
            _stream.Write(encoded.Stored.Buffer.AsSpan(0, encoded.Stored.Count));
            _pages.Add(new(
                encoded.Source.SegmentId,
                encoded.Source.Kind,
                encoded.Source.FirstOrdinal,
                encoded.Source.RecordCount,
                encoded.Source.MinimumTick,
                encoded.Source.MaximumTick,
                encoded.Source.MaximumActiveEndTick,
                encoded.Source.MinimumId,
                encoded.Source.MaximumId,
                encoded.Source.MinimumKey,
                encoded.Source.MaximumKey,
                encoded.Source.LaneMaskLow,
                encoded.Source.LaneMaskHigh,
                encoded.Source.MinimumRasterValue,
                encoded.Source.MaximumRasterValue,
                offset,
                encoded.Stored.Count,
                encoded.Source.DecodedByteCount,
                encoded.DecodedSha256));
        }
        finally
        {
            _pendingReservedBytes -= pending.ReservedBytes;
        }
    }

    private void CompleteEncoderScheduling()
    {
        if (_encoderCompleted) return;
        _encoderCompleted = true;
        _encoderScheduler.Complete();
    }

    private EncodedPage WaitForEncodedPage(PendingEncodedPage pending)
    {
        // A synchronous Task wait can attempt scheduler inlining even while the
        // task is concurrently finishing. Keep the scheduler alive until every
        // result has been observed: Complete can otherwise dispose its internal
        // ThreadLocal before that final inline attempt accesses it.
        _beforePageWaitTestHook?.Invoke(_encoderCompleted);
        return pending.Task.GetAwaiter().GetResult();
    }

    private Exception? StopEncoderPipeline()
    {
        Exception? failure = null;
        while (_pendingPages.Count != 0)
        {
            PendingEncodedPage pending = _pendingPages.Dequeue();
            try
            {
                using EncodedPage encoded = WaitForEncodedPage(pending);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                _pendingReservedBytes -= pending.ReservedBytes;
            }
        }
        CompleteEncoderScheduling();
        return failure;
    }

    private void ReleaseBuilderBuffers()
    {
        if (_builderBuffersReleased) return;
        _builderBuffersReleased = true;
        foreach (PageBuilder builder in _builders.Values) builder.ReleaseBuffers();
        _builders.Clear();
        _builders.TrimExcess();
        _nextOrdinals.Clear();
        _nextOrdinals.TrimExcess();
        _residentBuilders.Clear();
        _builderSpool.Dispose();
    }

    private readonly record struct PendingEncodedPage(
        Task<EncodedPage> Task,
        long ReservedBytes);

    private sealed record UnencodedPage(
        MidoraId SegmentId,
        PureMidiContentRecordKind Kind,
        int FirstOrdinal,
        int RecordCount,
        long MinimumTick,
        long MaximumTick,
        long MaximumActiveEndTick,
        MidoraId MinimumId,
        MidoraId MaximumId,
        int MinimumKey,
        int MaximumKey,
        ulong LaneMaskLow,
        ulong LaneMaskHigh,
        int MinimumRasterValue,
        int MaximumRasterValue,
        byte[]? DecodedBuffer,
        int DecodedByteCount,
        DirectMidiNoteValue[]? NoteEndpoints,
        DirectMidiChannelEventValue[]? ChannelEndpoints,
        CancellationToken CancellationToken,
        Action? EncodePageTestHook,
        Action<long> ChangeActualCapacity);

    private sealed class EncodedPage(
        UnencodedPage source,
        PooledBuffer stored,
        byte[] decodedSha256) : IDisposable
    {
        private bool _disposed;

        public UnencodedPage Source { get; } = source;
        public PooledBuffer Stored { get; } = stored;
        public byte[] DecodedSha256 { get; } = decodedSha256;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(Stored.Buffer);
            Source.ChangeActualCapacity(-Stored.Buffer.LongLength);
        }
    }

    private readonly record struct PooledBuffer(byte[] Buffer, int Count);

    private sealed class PooledWriteStream : Stream
    {
        private byte[]? _buffer;
        private int _length;
        private readonly Action<long> _capacityChanged;

        public PooledWriteStream(int initialCapacity, Action<long> capacityChanged)
        {
            if (initialCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
            _capacityChanged = capacityChanged;
            _capacityChanged(_buffer.LongLength);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            EnsureCapacity(checked(_length + buffer.Length));
            buffer.CopyTo(_buffer.AsSpan(_length));
            _length += buffer.Length;
        }

        public override void WriteByte(byte value)
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            EnsureCapacity(checked(_length + 1));
            _buffer[_length++] = value;
        }

        public PooledBuffer Detach()
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            byte[] result = _buffer;
            _buffer = null;
            return new(result, _length);
        }

        protected override void Dispose(bool disposing)
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                _capacityChanged(-buffer.LongLength);
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private void EnsureCapacity(int required)
        {
            byte[] current = _buffer
                ?? throw new ObjectDisposedException(nameof(PooledWriteStream));
            if (required <= current.Length) return;
            int nextLength = current.Length;
            while (nextLength < required)
            {
                nextLength = checked(nextLength * 2);
            }
            byte[] replacement = ArrayPool<byte>.Shared.Rent(nextLength);
            _capacityChanged(replacement.LongLength);
            current.AsSpan(0, _length).CopyTo(replacement);
            _buffer = replacement;
            ArrayPool<byte>.Shared.Return(current);
            _capacityChanged(-current.LongLength);
        }
    }

    private static void WriteDirectoryEntry(BinaryWriter writer, PageDescriptor page)
    {
        writer.Write((byte)page.Kind);
        writer.Write(new byte[3]);
        writer.Write(page.SegmentId.Value);
        writer.Write(page.FirstOrdinal);
        writer.Write(page.RecordCount);
        writer.Write(page.MinimumTick);
        writer.Write(page.MaximumTick);
        writer.Write(page.MaximumActiveEndTick);
        writer.Write(page.MinimumId.Value);
        writer.Write(page.MaximumId.Value);
        writer.Write(page.MinimumKey);
        writer.Write(page.MaximumKey);
        writer.Write(page.LaneMaskLow);
        writer.Write(page.LaneMaskHigh);
        writer.Write(page.MinimumRasterValue);
        writer.Write(page.MaximumRasterValue);
        writer.Write(page.Offset);
        writer.Write(page.StoredByteCount);
        writer.Write(page.DecodedByteCount);
        writer.Write(page.DecodedSha256);
    }

    private static void ValidateSegmentId(MidoraId value)
    {
        if (value == default) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("The content pack is already complete.");
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private void DisposeStreams()
    {
        if (_streamsDisposed) return;
        _streamsDisposed = true;
        _writer.Dispose();
        _stream.Dispose();
    }

    /// <summary>
    /// Stores incomplete builders without publishing pages or changing their
    /// frozen write order. Bucket extents are reused, so repeatedly revisiting
    /// a builder does not append another permanent copy of its tail.
    /// </summary>
    private sealed class BuilderSpool : IDisposable
    {
        private readonly Dictionary<int, Stack<long>> _free = [];
        private MidoraOwnedTemporaryDirectoryLease? _lease;
        private FileStream? _file;
        public long SpillCount { get; private set; }
        public long Length => _file?.Length ?? 0;
        public string? Path { get; private set; }

        public readonly record struct Extent(long Offset, int Length, int Capacity, byte[] Sha256);

        public Extent Write(ReadOnlySpan<byte> bytes)
        {
            EnsureFile();
            int capacity = PooledByteCapacity(bytes.Length);
            long offset = _free.TryGetValue(capacity, out Stack<long>? offsets) && offsets.TryPop(out long freeOffset)
                ? freeOffset : _file!.Length;
            if (offset == _file!.Length) _file.SetLength(checked(offset + capacity));
            _file.Position = offset;
            _file.Write(bytes);
            SpillCount++;
            return new(offset, bytes.Length, capacity, SHA256.HashData(bytes));
        }

        public void Read(Extent extent, Span<byte> destination)
        {
            if (destination.Length != extent.Length) throw new InvalidOperationException();
            _file!.Position = extent.Offset;
            _file.ReadExactly(destination);
            Span<byte> actualHash = stackalloc byte[32];
            SHA256.HashData(destination, actualHash);
            if (!actualHash.SequenceEqual(extent.Sha256))
                throw new InvalidDataException("An incomplete Pure MIDI page spool checksum is invalid.");
        }

        public void Release(Extent extent)
        {
            if (!_free.TryGetValue(extent.Capacity, out Stack<long>? offsets))
                _free.Add(extent.Capacity, offsets = []);
            offsets.Push(extent.Offset);
        }

        private void EnsureFile()
        {
            if (_file is not null) return;
            _lease = MidoraOwnedTemporaryDirectoryLease.Create(
                MidoraProgramData.Current.CompilerRunsDirectory, "content-build");
            Path = System.IO.Path.Combine(_lease.DirectoryPath, "incomplete-pages.bin");
            try
            {
                _file = new(Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            }
            catch
            {
                _lease.Dispose();
                _lease = null;
                throw;
            }
        }

        public void Dispose()
        {
            try { _file?.Dispose(); }
            finally
            {
                _file = null;
                try { _lease?.Dispose(); }
                finally
                {
                    _lease = null;
                    _free.Clear();
                }
            }
        }
    }

    private sealed class PageBuilder
    {
        private readonly PureMidiContentPackWriter _owner;
        private MemoryStream? _buffer;
        private BinaryWriter? _writer;
        private BuilderSpool.Extent? _spooled;
        private int _recordCount;
        private long _minimumTick;
        private long _maximumTick;
        private long _maximumActiveEndTick = -1;
        private MidoraId _minimumId;
        private MidoraId _maximumId;
        private int _minimumKey;
        private int _maximumKey;
        private ulong _laneMaskLow;
        private ulong _laneMaskHigh;
        private int _minimumRasterValue;
        private int _maximumRasterValue;
        private DirectMidiNoteValue[]? _noteEndpoints;
        private DirectMidiChannelEventValue[]? _channelEndpoints;

        public PageBuilder(
            MidoraId segmentId,
            PureMidiContentRecordKind kind,
            PureMidiContentPackWriter owner)
        {
            SegmentId = segmentId;
            Kind = kind;
            _owner = owner;
        }

        public MidoraId SegmentId { get; }
        public PureMidiContentRecordKind Kind { get; }
        public int RecordCount => _recordCount;
        public LinkedListNode<PageBuilder>? ResidentNode { get; set; }
        public bool IsEndpoint => Kind is PureMidiContentRecordKind.NoteOnEndpoint
            or PureMidiContentRecordKind.NoteOffEndpoint or PureMidiContentRecordKind.ChannelEventEndpoint;
        public long CapacityBytes => (_buffer?.Capacity ?? 0L)
            + (_noteEndpoints?.LongLength ?? 0) * Unsafe.SizeOf<DirectMidiNoteValue>()
            + (_channelEndpoints?.LongLength ?? 0) * Unsafe.SizeOf<DirectMidiChannelEventValue>();
        public int DecodedByteCount => IsEndpoint
            ? checked(_recordCount * (Kind == PureMidiContentRecordKind.ChannelEventEndpoint ? 36 : 52))
            : checked((int)(_buffer?.Length ?? _spooled?.Length ?? 0));
        public int LastPageRecordCount { get; private set; }
        public long LastPageMinimumTick { get; private set; }
        public long LastPageMaximumTick { get; private set; }
        public long LastPageMaximumActiveEndTick { get; private set; }
        public MidoraId LastPageMinimumId { get; private set; }
        public MidoraId LastPageMaximumId { get; private set; }
        public int LastPageMinimumKey { get; private set; }
        public int LastPageMaximumKey { get; private set; }
        public ulong LastPageLaneMaskLow { get; private set; }
        public ulong LastPageLaneMaskHigh { get; private set; }
        public int LastPageMinimumRasterValue { get; private set; }
        public int LastPageMaximumRasterValue { get; private set; }

        public void AddNote(DirectMidiNoteValue value)
        {
            if (Kind is not (PureMidiContentRecordKind.Note
                or PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint))
            {
                throw new InvalidOperationException();
            }
            const int recordBytes = 52;
            EnsureRoom(recordBytes);
            if (Kind == PureMidiContentRecordKind.Note)
            {
                WriteNote(_writer!, value);
                Record(
                    value.Id,
                    value.StartTick,
                    SaturatingAdd(value.StartTick, Math.Max(1, value.LengthTicks)),
                    value.Key,
                    rasterValue: value.NoteOnVelocity);
                return;
            }
            _noteEndpoints![_recordCount] = value;
            long noteEnd = SaturatingAdd(value.StartTick, Math.Max(1, value.LengthTicks));
            long endpointTick = Kind == PureMidiContentRecordKind.NoteOnEndpoint
                ? value.StartTick
                : noteEnd;
            Record(
                value.Id,
                endpointTick,
                endpointTick,
                value.Key,
                Kind == PureMidiContentRecordKind.NoteOnEndpoint ? noteEnd : -1,
                value.NoteOnVelocity);
        }

        public void AddChannelEvent(DirectMidiChannelEventValue value)
        {
            if (Kind is not (PureMidiContentRecordKind.ChannelEvent
                or PureMidiContentRecordKind.ChannelEventEndpoint))
            {
                throw new InvalidOperationException();
            }
            const int recordBytes = 36;
            EnsureRoom(recordBytes);
            if (Kind == PureMidiContentRecordKind.ChannelEvent)
            {
                WriteChannelEvent(_writer!, value);
            }
            else
            {
                _channelEndpoints![_recordCount] = value;
            }
            Record(value.Id, value.Tick, value.Tick, -1, rasterValue: value.Data2);
        }

        public void AddOpaqueEvent(OpaqueMidiEventValue value)
        {
            if (Kind != PureMidiContentRecordKind.OpaqueEvent) throw new InvalidOperationException();
            int recordBytes = checked(33 + value.Payload.Length);
            if (recordBytes > MaximumDecodedPageByteCount)
            {
                throw new InvalidDataException(
                    $"Opaque MIDI event {value.Id.Value} exceeds the decoded page byte limit.");
            }
            EnsureRoom(recordBytes);
            _writer!.Write(value.Id.Value);
            _writer.Write(value.Tick);
            _writer.Write((int)value.Kind);
            _writer.Write(value.MetaType);
            _writer.Write(value.Payload.Length);
            _writer.Write(value.Payload.Span);
            _writer.Write(value.Order);
            Record(value.Id, value.Tick, value.Tick, -1, rasterValue: 0);
        }

        public ArraySegment<byte> PreparePage()
        {
            _writer?.Flush();
            LastPageRecordCount = _recordCount;
            LastPageMinimumTick = _minimumTick;
            LastPageMaximumTick = _maximumTick;
            LastPageMaximumActiveEndTick = _maximumActiveEndTick;
            LastPageMinimumId = _minimumId;
            LastPageMaximumId = _maximumId;
            LastPageMinimumKey = _minimumKey;
            LastPageMaximumKey = _maximumKey;
            LastPageLaneMaskLow = _laneMaskLow;
            LastPageLaneMaskHigh = _laneMaskHigh;
            LastPageMinimumRasterValue = _minimumRasterValue;
            LastPageMaximumRasterValue = _maximumRasterValue;
            return _buffer is null ? default : new(_buffer.GetBuffer(), 0, checked((int)_buffer.Length));
        }

        public DirectMidiNoteValue[]? TakeNoteEndpointBuffer()
        {
            DirectMidiNoteValue[]? result = _noteEndpoints;
            _noteEndpoints = null;
            if (result is not null)
                _owner.ChangeActiveCapacity(-result.LongLength * Unsafe.SizeOf<DirectMidiNoteValue>());
            return result;
        }

        public DirectMidiChannelEventValue[]? TakeChannelEndpointBuffer()
        {
            DirectMidiChannelEventValue[]? result = _channelEndpoints;
            _channelEndpoints = null;
            if (result is not null)
                _owner.ChangeActiveCapacity(-result.LongLength * Unsafe.SizeOf<DirectMidiChannelEventValue>());
            return result;
        }

        public void ReleaseBuffers()
        {
            long capacity = CapacityBytes;
            if (_noteEndpoints is not null)
            {
                ArrayPool<DirectMidiNoteValue>.Shared.Return(_noteEndpoints);
                _noteEndpoints = null;
            }
            if (_channelEndpoints is not null)
            {
                ArrayPool<DirectMidiChannelEventValue>.Shared.Return(_channelEndpoints);
                _channelEndpoints = null;
            }
            _writer?.Dispose();
            _buffer?.Dispose();
            _writer = null;
            _buffer = null;
            _owner.ChangeActiveCapacity(-capacity);
        }

        public void ResetPage()
        {
            _buffer?.SetLength(0);
            if (_buffer is not null) _buffer.Position = 0;
            _recordCount = 0;
            _maximumActiveEndTick = -1;
            _laneMaskLow = 0;
            _laneMaskHigh = 0;
        }

        private void EnsureRoom(int nextRecordBytes)
        {
            int maximumRecords = Kind is PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint
                or PureMidiContentRecordKind.ChannelEventEndpoint
                    ? MaximumEndpointPageRecordCount
                    : MaximumPageRecordCount;
            if (_recordCount != 0
                && (_recordCount >= maximumRecords
                    || DecodedByteCount + nextRecordBytes > MaximumDecodedPageByteCount))
            {
                _owner.Flush(this);
            }
            EnsureResident();
            if (Kind is PureMidiContentRecordKind.NoteOnEndpoint or PureMidiContentRecordKind.NoteOffEndpoint)
                GrowEndpoint(ref _noteEndpoints, _recordCount + 1);
            else if (Kind == PureMidiContentRecordKind.ChannelEventEndpoint)
                GrowEndpoint(ref _channelEndpoints, _recordCount + 1);
            else GrowByteBuffer(checked(DecodedByteCount + nextRecordBytes));
        }

        private void GrowEndpoint<T>(ref T[]? buffer, int required) where T : struct
        {
            if (buffer is not null && buffer.Length >= required) return;
            int desired = Math.Max(16, buffer is null ? required : Math.Max(required, buffer.Length * 2));
            int capacity = checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)desired));
            long bytes = (long)capacity * Unsafe.SizeOf<T>();
            _owner.EnsureBufferRoom(this, bytes);
            T[] replacement = ArrayPool<T>.Shared.Rent(capacity);
            long actualBytes = replacement.LongLength * Unsafe.SizeOf<T>();
            _owner.ChangeActiveCapacity(actualBytes);
            if (buffer is not null)
            {
                buffer.AsSpan(0, _recordCount).CopyTo(replacement);
                _owner.ChangeActiveCapacity(-buffer.LongLength * Unsafe.SizeOf<T>());
                ArrayPool<T>.Shared.Return(buffer);
            }
            buffer = replacement;
        }

        private void GrowByteBuffer(int required)
        {
            int oldCapacity = _buffer?.Capacity ?? 0;
            if (oldCapacity >= required) return;
            int capacity = Math.Min(MaximumDecodedPageByteCount,
                Math.Max(256, Math.Max(required, checked(oldCapacity * 2))));
            _owner.EnsureBufferRoom(this, capacity);
            if (_buffer is null)
            {
                _buffer = new(capacity);
                _writer = new(_buffer, System.Text.Encoding.UTF8, leaveOpen: true);
            }
            else _buffer.Capacity = capacity;
            _owner.ObserveActualCapacityPeak(capacity);
            _owner.ChangeActiveCapacity(capacity - oldCapacity);
        }

        public void Spill()
        {
            if (CapacityBytes == 0) return;
            if (_recordCount != 0)
            {
                _owner._beforeBuilderSpillTestHook?.Invoke();
                ReadOnlySpan<byte> data = _noteEndpoints is not null
                    ? MemoryMarshal.AsBytes(_noteEndpoints.AsSpan(0, _recordCount))
                    : _channelEndpoints is not null
                        ? MemoryMarshal.AsBytes(_channelEndpoints.AsSpan(0, _recordCount))
                        : _buffer!.GetBuffer().AsSpan(0, checked((int)_buffer.Length));
                _spooled = _owner._builderSpool.Write(data);
            }
            ReleaseBuffers();
        }

        public void EnsureResident()
        {
            if (_spooled is not { } extent) return;
            if (Kind is PureMidiContentRecordKind.NoteOnEndpoint or PureMidiContentRecordKind.NoteOffEndpoint)
            {
                GrowEndpoint(ref _noteEndpoints, _recordCount);
                _owner._builderSpool.Read(extent, MemoryMarshal.AsBytes(_noteEndpoints.AsSpan(0, _recordCount)));
            }
            else if (Kind == PureMidiContentRecordKind.ChannelEventEndpoint)
            {
                GrowEndpoint(ref _channelEndpoints, _recordCount);
                _owner._builderSpool.Read(extent, MemoryMarshal.AsBytes(_channelEndpoints.AsSpan(0, _recordCount)));
            }
            else
            {
                GrowByteBuffer(extent.Length);
                // Expanding MemoryStream.Length clears the newly exposed bytes;
                // establish its extent before copying the staged data back.
                _buffer!.SetLength(extent.Length);
                _owner._builderSpool.Read(extent, _buffer.GetBuffer().AsSpan(0, extent.Length));
                _buffer.Position = extent.Length;
            }
            _owner._builderSpool.Release(extent);
            _spooled = null;
            _owner.TouchBuilder(this);
        }

        private void Record(
            MidoraId id,
            long minimumTick,
            long maximumTick,
            int key,
            long maximumActiveEndTick = -1,
            int rasterValue = 0)
        {
            if (_recordCount == 0)
            {
                _minimumTick = minimumTick;
                _maximumTick = maximumTick;
                _minimumId = id;
                _maximumId = id;
                _minimumKey = key;
                _maximumKey = key;
                _maximumActiveEndTick = maximumActiveEndTick;
                _minimumRasterValue = rasterValue;
                _maximumRasterValue = rasterValue;
            }
            else
            {
                _minimumTick = Math.Min(_minimumTick, minimumTick);
                _maximumTick = Math.Max(_maximumTick, maximumTick);
                if (id.CompareTo(_minimumId) < 0) _minimumId = id;
                if (id.CompareTo(_maximumId) > 0) _maximumId = id;
                if (key >= 0)
                {
                    _minimumKey = Math.Min(_minimumKey, key);
                    _maximumKey = Math.Max(_maximumKey, key);
                }
                _maximumActiveEndTick = Math.Max(
                    _maximumActiveEndTick,
                    maximumActiveEndTick);
                _minimumRasterValue = Math.Min(_minimumRasterValue, rasterValue);
                _maximumRasterValue = Math.Max(_maximumRasterValue, rasterValue);
            }
            if (key is >= 0 and < 64) _laneMaskLow |= 1UL << key;
            else if (key is >= 64 and < 128) _laneMaskHigh |= 1UL << (key - 64);
            _recordCount++;
        }

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        private static void WriteNote(BinaryWriter writer, DirectMidiNoteValue value)
        {
            writer.Write(value.Id.Value);
            writer.Write(value.StartTick);
            writer.Write(value.LengthTicks);
            writer.Write(value.Key);
            writer.Write(value.NoteOnVelocity);
            writer.Write(value.NoteOffVelocity);
            writer.Write(value.NoteOnOrder);
            writer.Write(value.NoteOffOrder);
        }

        private static void WriteChannelEvent(
            BinaryWriter writer,
            DirectMidiChannelEventValue value)
        {
            writer.Write(value.Id.Value);
            writer.Write(value.Tick);
            writer.Write((int)value.Kind);
            writer.Write(value.Data1);
            writer.Write(value.Data2);
            writer.Write(value.Order);
        }

        internal sealed class NoteOnEndpointComparer : IComparer<DirectMidiNoteValue>
        {
            public static NoteOnEndpointComparer Instance { get; } = new();

            public int Compare(DirectMidiNoteValue x, DirectMidiNoteValue y)
            {
                int result = x.StartTick.CompareTo(y.StartTick);
                if (result != 0) return result;
                result = x.NoteOnOrder.CompareTo(y.NoteOnOrder);
                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }

        internal sealed class NoteOffEndpointComparer : IComparer<DirectMidiNoteValue>
        {
            public static NoteOffEndpointComparer Instance { get; } = new();

            public int Compare(DirectMidiNoteValue x, DirectMidiNoteValue y)
            {
                int result = SaturatingAdd(x.StartTick, Math.Max(1, x.LengthTicks))
                    .CompareTo(SaturatingAdd(y.StartTick, Math.Max(1, y.LengthTicks)));
                if (result != 0) return result;
                result = x.NoteOffOrder.CompareTo(y.NoteOffOrder);
                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }

        internal sealed class ChannelEndpointComparer :
            IComparer<DirectMidiChannelEventValue>
        {
            public static ChannelEndpointComparer Instance { get; } = new();

            public int Compare(
                DirectMidiChannelEventValue x,
                DirectMidiChannelEventValue y)
            {
                int result = x.Tick.CompareTo(y.Tick);
                if (result != 0) return result;
                result = x.Order.CompareTo(y.Order);
                return result != 0 ? result : x.Id.CompareTo(y.Id);
            }
        }
    }

    private sealed record PageDescriptor(
        MidoraId SegmentId,
        PureMidiContentRecordKind Kind,
        int FirstOrdinal,
        int RecordCount,
        long MinimumTick,
        long MaximumTick,
        long MaximumActiveEndTick,
        MidoraId MinimumId,
        MidoraId MaximumId,
        int MinimumKey,
        int MaximumKey,
        ulong LaneMaskLow,
        ulong LaneMaskHigh,
        int MinimumRasterValue,
        int MaximumRasterValue,
        long Offset,
        int StoredByteCount,
        int DecodedByteCount,
        byte[] DecodedSha256);
}

public sealed class PureMidiContentPackDecodedCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> _entries = [];
    private readonly Dictionary<CacheKey, TaskCompletionSource<object>> _decodes = [];
    private readonly LinkedList<CacheKey> _lru = [];
    private long _byteCount;
    private bool _disposed;

    public PureMidiContentPackDecodedCache(
        long byteLimit = PureMidiContentPack.DefaultDecodedCacheByteLimit)
    {
        if (byteLimit < PureMidiContentPackWriter.MaximumDecodedPageByteCount)
            throw new ArgumentOutOfRangeException(nameof(byteLimit));
        ByteLimit = byteLimit;
    }

    public long ByteLimit { get; }

    public long ByteCount
    {
        get
        {
            lock (_gate) return _byteCount;
        }
    }

    internal object GetOrAdd(
        PureMidiContentPack owner,
        int pageIndex,
        int decodedByteCount,
        Func<object> factory,
        out bool cacheHit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(factory);
        CacheKey key = new(owner, pageIndex);
        TaskCompletionSource<object> decode;
        bool ownsDecode = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(key, out CacheEntry? cached))
            {
                cacheHit = true;
                _lru.Remove(cached.Node);
                _lru.AddFirst(cached.Node);
                return cached.Value;
            }
            if (!_decodes.TryGetValue(key, out decode!))
            {
                decode = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = decode.Task.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously
                        | TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                _decodes.Add(key, decode);
                ownsDecode = true;
            }
        }

        if (!ownsDecode)
        {
            cacheHit = true;
            return cancellationToken.CanBeCanceled
                ? decode.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult()
                : decode.Task.GetAwaiter().GetResult();
        }

        try
        {
            // Decompression and checksum validation can take milliseconds. Keeping it
            // outside the global cache lock prevents an audio miss in one Track from
            // blocking an unrelated UI range query in another Track. The producer is
            // deliberately not canceled by an individual waiter: another raster/audio
            // consumer may already share this single-flight decode.
            object value = factory();
            long retainedByteCount = value is OpaqueMidiEventValue[] opaque
                ? checked(24L + opaque.LongLength * Unsafe.SizeOf<OpaqueMidiEventValue>()
                    + OpaqueMidiPayloadMemory.GetRetainedAllocatedBytes(opaque))
                : decodedByteCount;
            object published;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entries.TryGetValue(key, out CacheEntry? concurrentlyAdded))
                {
                    published = concurrentlyAdded.Value;
                    _lru.Remove(concurrentlyAdded.Node);
                    _lru.AddFirst(concurrentlyAdded.Node);
                }
                else if (retainedByteCount <= ByteLimit)
                {
                    LinkedListNode<CacheKey> node = _lru.AddFirst(key);
                    _entries.Add(key, new(value, retainedByteCount, node));
                    _byteCount += retainedByteCount;
                    while (_byteCount > ByteLimit && _lru.Last is not null)
                    {
                        CacheKey evictedKey = _lru.Last.Value;
                        if (evictedKey == key && _entries.Count == 1) break;
                        CacheEntry evicted = _entries[evictedKey];
                        _lru.RemoveLast();
                        _entries.Remove(evictedKey);
                        _byteCount -= evicted.DecodedByteCount;
                    }
                    published = value;
                }
                else published = value;
                _decodes.Remove(key);
            }
            cacheHit = false;
            decode.TrySetResult(published);
            return published;
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _decodes.Remove(key);
            }
            decode.TrySetException(exception);
            throw;
        }
    }

    internal bool TryGetMany(
        PureMidiContentPack owner,
        ReadOnlySpan<int> pageIndexes,
        out object[] values)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            values = new object[pageIndexes.Length];
            for (int index = 0; index < pageIndexes.Length; index++)
            {
                CacheKey key = new(owner, pageIndexes[index]);
                if (!_entries.TryGetValue(key, out CacheEntry? cached))
                {
                    values = [];
                    return false;
                }
                values[index] = cached.Value;
            }
            // Hold every decoded array strongly in `values` before changing LRU
            // order. Eviction after this lock cannot invalidate the exact query.
            for (int index = 0; index < pageIndexes.Length; index++)
            {
                CacheEntry cached = _entries[new(owner, pageIndexes[index])];
                _lru.Remove(cached.Node);
                _lru.AddFirst(cached.Node);
            }
            return true;
        }
    }

    internal void RemoveOwner(PureMidiContentPack owner)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (CacheKey key in _entries.Keys.Where(value => ReferenceEquals(value.Owner, owner)).ToArray())
            {
                CacheEntry entry = _entries[key];
                _entries.Remove(key);
                _lru.Remove(entry.Node);
                _byteCount -= entry.DecodedByteCount;
            }
        }
    }

    public void Dispose()
    {
        TaskCompletionSource<object>[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
            _lru.Clear();
            _byteCount = 0;
            pending = _decodes.Values.ToArray();
            _decodes.Clear();
        }
        ObjectDisposedException exception = new(nameof(PureMidiContentPackDecodedCache));
        foreach (TaskCompletionSource<object> decode in pending)
            decode.TrySetException(exception);
    }

    private readonly record struct CacheKey(PureMidiContentPack Owner, int PageIndex);

    private sealed record CacheEntry(
        object Value,
        long DecodedByteCount,
        LinkedListNode<CacheKey> Node);
}

public sealed class PureMidiContentPack : IDisposable
{
    public const long DefaultDecodedCacheByteLimit = 64L * 1024 * 1024;

    private const int HeaderByteCount = 48;
    private const int DirectoryEntryByteCount = 140;
    private const int FooterByteCount = 48;
    private const int Version = 4;
    private static ReadOnlySpan<byte> HeaderMagic => "MIDMPK3\0"u8;
    private static ReadOnlySpan<byte> FooterMagic => "MIDMPKF\0"u8;

    private readonly SafeFileHandle _handle;
    private readonly PageDescriptor[] _pages;
    private readonly Dictionary<MidoraId, SegmentSource> _sources;
    private readonly PureMidiContentPackDecodedCache _decodedCache;
    private readonly bool _ownsDecodedCache;
    private long _pageCacheHitCount;
    private long _pageCacheMissCount;
    private bool _disposed;

    private PureMidiContentPack(
        string path,
        SafeFileHandle handle,
        PageDescriptor[] pages,
        string fingerprint,
        PureMidiContentPackDecodedCache decodedCache,
        bool ownsDecodedCache)
    {
        Path = path;
        _handle = handle;
        _pages = pages;
        ContentFingerprint = fingerprint;
        _decodedCache = decodedCache;
        _ownsDecodedCache = ownsDecodedCache;
        _sources = pages
            .Select(value => value.SegmentId)
            .Distinct()
            .ToDictionary(value => value, value => new SegmentSource(this, value));
    }

    public string Path { get; }
    public string ContentFingerprint { get; }
    public int PageCount => _pages.Length;
    public long DecodedCacheByteLimit => _decodedCache.ByteLimit;
    public long DecodedCacheByteCount => _decodedCache.ByteCount;
    public long PageCacheHitCount => Interlocked.Read(ref _pageCacheHitCount);
    public long PageCacheMissCount => Interlocked.Read(ref _pageCacheMissCount);

    public static PureMidiContentPack Open(
        string path,
        long decodedCacheByteLimit = DefaultDecodedCacheByteLimit)
    {
        PureMidiContentPackDecodedCache cache = new(decodedCacheByteLimit);
        try
        {
            return OpenCore(path, cache, ownsDecodedCache: true);
        }
        catch
        {
            cache.Dispose();
            throw;
        }
    }

    public static PureMidiContentPack Open(
        string path,
        PureMidiContentPackDecodedCache decodedCache)
    {
        ArgumentNullException.ThrowIfNull(decodedCache);
        return OpenCore(path, decodedCache, ownsDecodedCache: false);
    }

    private static PureMidiContentPack OpenCore(
        string path,
        PureMidiContentPackDecodedCache decodedCache,
        bool ownsDecodedCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = System.IO.Path.GetFullPath(path);
        SafeFileHandle handle = File.OpenHandle(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            FileOptions.RandomAccess);
        try
        {
            long fileLength = RandomAccess.GetLength(handle);
            if (fileLength < HeaderByteCount + FooterByteCount)
                throw new InvalidDataException("Pure MIDI content pack is truncated.");
            byte[] header = ReadExactly(handle, 0, HeaderByteCount);
            if (!header.AsSpan(0, 8).SequenceEqual(HeaderMagic))
                throw new InvalidDataException("Pure MIDI content pack magic is invalid.");
            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            int headerBytes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            int pageCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            int reserved = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
            long directoryOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
            long directoryLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32));
            long footerOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
            if (version != Version || headerBytes != HeaderByteCount || reserved != 0 || pageCount < 0)
                throw new InvalidDataException("Pure MIDI content pack header is unsupported or malformed.");
            long expectedDirectoryLength = checked((long)pageCount * DirectoryEntryByteCount);
            if (directoryLength != expectedDirectoryLength
                || directoryOffset < HeaderByteCount
                || footerOffset != checked(directoryOffset + directoryLength)
                || checked(footerOffset + FooterByteCount) != fileLength
                || directoryLength > int.MaxValue)
            {
                throw new InvalidDataException("Pure MIDI content pack offsets are inconsistent.");
            }
            byte[] directory = ReadExactly(handle, directoryOffset, checked((int)directoryLength));
            byte[] footer = ReadExactly(handle, footerOffset, FooterByteCount);
            if (!footer.AsSpan(0, 8).SequenceEqual(FooterMagic))
                throw new InvalidDataException("Pure MIDI content pack footer magic is invalid.");
            byte[] actualDirectoryHash = SHA256.HashData(directory);
            if (!footer.AsSpan(8, 32).SequenceEqual(actualDirectoryHash))
                throw new InvalidDataException("Pure MIDI content pack directory checksum is invalid.");
            if (BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(40)) != fileLength)
                throw new InvalidDataException("Pure MIDI content pack footer length is invalid.");

            PageDescriptor[] pages = ParseDirectory(directory, pageCount, directoryOffset);
            return new(
                fullPath,
                handle,
                pages,
                Convert.ToHexStringLower(actualDirectoryHash),
                decodedCache,
                ownsDecodedCache);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IPureMidiSegmentContentSource GetSegmentSource(MidoraId segmentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _sources.TryGetValue(segmentId, out SegmentSource? source)
            ? source
            : new SegmentSource(this, segmentId);
    }

    public IReadOnlyList<MidoraId> SegmentIds => _sources.Keys.Order().ToArray();

    public void CopyTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        using FileStream source = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        source.CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decodedCache.RemoveOwner(this);
        _handle.Dispose();
        if (_ownsDecodedCache) _decodedCache.Dispose();
    }

    private object GetDecodedPage(
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PageDescriptor descriptor = _pages[pageIndex];
        object result = _decodedCache.GetOrAdd(
            this,
            pageIndex,
            descriptor.DecodedByteCount,
            () => DecodeStoredPage(pageIndex, descriptor),
            out bool cacheHit,
            cancellationToken);
        if (cacheHit) Interlocked.Increment(ref _pageCacheHitCount);
        else Interlocked.Increment(ref _pageCacheMissCount);
        return result;
    }

    private bool TryGetDecodedPages(
        ReadOnlySpan<int> pageIndexes,
        out object[] values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _decodedCache.TryGetMany(this, pageIndexes, out values);
    }

    private object DecodeStoredPage(int pageIndex, PageDescriptor descriptor)
    {
        byte[] stored = ReadExactly(_handle, descriptor.Offset, descriptor.StoredByteCount);
        byte[] decoded = new byte[descriptor.DecodedByteCount];
        try
        {
            using MemoryStream compressed = new(stored, writable: false);
            using BrotliStream brotli = new(compressed, CompressionMode.Decompress);
            int position = 0;
            while (position < decoded.Length)
            {
                int read = brotli.Read(decoded, position, decoded.Length - position);
                if (read == 0) break;
                position += read;
            }
            if (position != decoded.Length || brotli.ReadByte() != -1)
                throw new InvalidDataException($"Pure MIDI content page {pageIndex} decoded length is invalid.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"Pure MIDI content page {pageIndex} compressed payload is invalid.",
                exception);
        }
        if (!SHA256.HashData(decoded).AsSpan().SequenceEqual(descriptor.DecodedSha256))
            throw new InvalidDataException($"Pure MIDI content page {pageIndex} checksum is invalid.");
        return DecodePage(descriptor, decoded);
    }

    private static object DecodePage(PageDescriptor descriptor, byte[] decoded)
    {
        using MemoryStream stream = new(decoded, writable: false);
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: false);
        object result;
        switch (descriptor.Kind)
        {
            case PureMidiContentRecordKind.Note:
            case PureMidiContentRecordKind.NoteOnEndpoint:
            case PureMidiContentRecordKind.NoteOffEndpoint:
                {
                    DirectMidiNoteValue[] values = new DirectMidiNoteValue[descriptor.RecordCount];
                    for (int index = 0; index < values.Length; index++)
                    {
                        values[index] = new(
                            MidoraId.FromSequence(reader.ReadInt64()),
                            reader.ReadInt64(),
                            reader.ReadInt64(),
                            reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt64(),
                            reader.ReadInt64());
                    }
                    result = values;
                    break;
                }
            case PureMidiContentRecordKind.ChannelEvent:
            case PureMidiContentRecordKind.ChannelEventEndpoint:
                {
                    DirectMidiChannelEventValue[] values = new DirectMidiChannelEventValue[descriptor.RecordCount];
                    for (int index = 0; index < values.Length; index++)
                    {
                        values[index] = new(
                            MidoraId.FromSequence(reader.ReadInt64()),
                            reader.ReadInt64(),
                            (DirectMidiChannelEventKind)reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt32(),
                            reader.ReadInt64());
                    }
                    result = values;
                    break;
                }
            case PureMidiContentRecordKind.OpaqueEvent:
                {
                    OpaqueMidiEventValue[] values = new OpaqueMidiEventValue[descriptor.RecordCount];
                    for (int index = 0; index < values.Length; index++)
                    {
                        MidoraId id = MidoraId.FromSequence(reader.ReadInt64());
                        long tick = reader.ReadInt64();
                        OpaqueMidiEventKind kind = (OpaqueMidiEventKind)reader.ReadInt32();
                        byte metaType = reader.ReadByte();
                        int payloadLength = reader.ReadInt32();
                        if (payloadLength < 0 || payloadLength > decoded.Length - stream.Position - 8)
                            throw new InvalidDataException("Pure MIDI opaque payload length is invalid.");
                        byte[] payload = reader.ReadBytes(payloadLength);
                        if (payload.Length != payloadLength)
                            throw new InvalidDataException("Pure MIDI opaque payload is truncated.");
                        values[index] = new(id, tick, kind, metaType, payload, reader.ReadInt64());
                    }
                    result = values;
                    break;
                }
            default:
                throw new InvalidDataException($"Unsupported Pure MIDI content page kind {descriptor.Kind}.");
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Pure MIDI content page has trailing bytes.");
        ValidateDecodedPage(descriptor, result);
        return result;
    }

    private static void ValidateDecodedPage(PageDescriptor descriptor, object decoded)
    {
        long minimumTick = long.MaxValue;
        long maximumTick = long.MinValue;
        long maximumActiveEndTick = -1;
        MidoraId minimumId = new(long.MaxValue);
        MidoraId maximumId = default;
        int minimumKey = int.MaxValue;
        int maximumKey = int.MinValue;
        ulong laneMaskLow = 0;
        ulong laneMaskHigh = 0;
        int minimumRasterValue = int.MaxValue;
        int maximumRasterValue = int.MinValue;
        HashSet<MidoraId> ids = new(descriptor.RecordCount);

        switch (decoded)
        {
            case DirectMidiNoteValue[] notes:
                foreach (DirectMidiNoteValue value in notes)
                {
                    if (value.StartTick < 0 || value.LengthTicks <= 0
                        || value.StartTick > long.MaxValue - value.LengthTicks
                        || value.Key is < 0 or > 127
                        || value.NoteOnVelocity is < 1 or > 127
                        || value.NoteOffVelocity is < 0 or > 127
                        || value.NoteOnOrder < 0 || value.NoteOffOrder < 0)
                    {
                        throw new InvalidDataException("Pure MIDI Note page contains an invalid record.");
                    }
                    long endTick = value.StartTick + value.LengthTicks;
                    if (descriptor.Kind == PureMidiContentRecordKind.NoteOnEndpoint)
                    {
                        Record(value.Id, value.StartTick, value.StartTick, value.Key, value.NoteOnVelocity);
                        maximumActiveEndTick = Math.Max(maximumActiveEndTick, endTick);
                    }
                    else if (descriptor.Kind == PureMidiContentRecordKind.NoteOffEndpoint)
                    {
                        Record(value.Id, endTick, endTick, value.Key, value.NoteOnVelocity);
                    }
                    else
                    {
                        Record(value.Id, value.StartTick, endTick, value.Key, value.NoteOnVelocity);
                    }
                }
                break;
            case DirectMidiChannelEventValue[] events:
                foreach (DirectMidiChannelEventValue value in events)
                {
                    bool oneByte = value.Kind is DirectMidiChannelEventKind.ProgramChange
                        or DirectMidiChannelEventKind.ChannelPressure;
                    if (value.Tick < 0 || !Enum.IsDefined(value.Kind)
                        || value.Data1 is < 0 or > 127
                        || value.Data2 < 0 || value.Data2 > (oneByte ? 0 : 127)
                        || value.Order < 0)
                    {
                        throw new InvalidDataException("Pure MIDI channel-event page contains an invalid record.");
                    }
                    Record(value.Id, value.Tick, value.Tick, -1, value.Data2);
                }
                break;
            case OpaqueMidiEventValue[] opaque:
                foreach (OpaqueMidiEventValue value in opaque)
                {
                    if (value.Tick < 0 || !Enum.IsDefined(value.Kind) || value.Order < 0)
                        throw new InvalidDataException("Pure MIDI opaque-event page contains an invalid record.");
                    Record(value.Id, value.Tick, value.Tick, -1, 0);
                }
                break;
            default:
                throw new InvalidDataException("Pure MIDI content page decoded to an unexpected record type.");
        }

        if (ids.Count != descriptor.RecordCount
            || minimumTick != descriptor.MinimumTick
            || maximumTick != descriptor.MaximumTick
            || maximumActiveEndTick != descriptor.MaximumActiveEndTick
            || minimumId != descriptor.MinimumId
            || maximumId != descriptor.MaximumId
            || laneMaskLow != descriptor.LaneMaskLow
            || laneMaskHigh != descriptor.LaneMaskHigh
            || minimumRasterValue != descriptor.MinimumRasterValue
            || maximumRasterValue != descriptor.MaximumRasterValue
            || IsNoteKind(descriptor.Kind)
                && (minimumKey != descriptor.MinimumKey || maximumKey != descriptor.MaximumKey)
            || !IsNoteKind(descriptor.Kind)
                && (descriptor.MinimumKey != -1 || descriptor.MaximumKey != -1))
        {
            throw new InvalidDataException("Pure MIDI content page metadata does not match its decoded records.");
        }

        void Record(MidoraId id, long startTick, long endTick, int key, int rasterValue)
        {
            if (!ids.Add(id))
                throw new InvalidDataException("Pure MIDI content page contains a duplicate stable ID.");
            minimumTick = Math.Min(minimumTick, startTick);
            maximumTick = Math.Max(maximumTick, endTick);
            if (id.CompareTo(minimumId) < 0) minimumId = id;
            if (id.CompareTo(maximumId) > 0) maximumId = id;
            if (key >= 0)
            {
                minimumKey = Math.Min(minimumKey, key);
                maximumKey = Math.Max(maximumKey, key);
                if (key < 64) laneMaskLow |= 1UL << key;
                else if (key < 128) laneMaskHigh |= 1UL << (key - 64);
            }
            minimumRasterValue = Math.Min(minimumRasterValue, rasterValue);
            maximumRasterValue = Math.Max(maximumRasterValue, rasterValue);
        }

        static bool IsNoteKind(PureMidiContentRecordKind kind) =>
            kind is PureMidiContentRecordKind.Note
                or PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint;
    }

    private static PageDescriptor[] ParseDirectory(byte[] bytes, int pageCount, long directoryOffset)
    {
        PageDescriptor[] pages = new PageDescriptor[pageCount];
        using MemoryStream stream = new(bytes, writable: false);
        using BinaryReader reader = new(stream);
        Dictionary<(MidoraId SegmentId, PureMidiContentRecordKind Kind), int> nextOrdinals = [];
        long previousPayloadEnd = HeaderByteCount;
        for (int index = 0; index < pages.Length; index++)
        {
            PureMidiContentRecordKind kind = (PureMidiContentRecordKind)reader.ReadByte();
            if (reader.ReadByte() != 0 || reader.ReadByte() != 0 || reader.ReadByte() != 0)
                throw new InvalidDataException("Pure MIDI content directory reserved bytes are non-zero.");
            MidoraId segmentId = MidoraId.FromSequence(reader.ReadInt64());
            int firstOrdinal = reader.ReadInt32();
            int recordCount = reader.ReadInt32();
            long minimumTick = reader.ReadInt64();
            long maximumTick = reader.ReadInt64();
            long maximumActiveEndTick = reader.ReadInt64();
            MidoraId minimumId = MidoraId.FromSequence(reader.ReadInt64());
            MidoraId maximumId = MidoraId.FromSequence(reader.ReadInt64());
            int minimumKey = reader.ReadInt32();
            int maximumKey = reader.ReadInt32();
            ulong laneMaskLow = reader.ReadUInt64();
            ulong laneMaskHigh = reader.ReadUInt64();
            int minimumRasterValue = reader.ReadInt32();
            int maximumRasterValue = reader.ReadInt32();
            long offset = reader.ReadInt64();
            int storedBytes = reader.ReadInt32();
            int decodedBytes = reader.ReadInt32();
            byte[] checksum = reader.ReadBytes(32);
            int maximumRecords = kind is PureMidiContentRecordKind.NoteOnEndpoint
                or PureMidiContentRecordKind.NoteOffEndpoint
                or PureMidiContentRecordKind.ChannelEventEndpoint
                    ? PureMidiContentPackWriter.MaximumEndpointPageRecordCount
                    : PureMidiContentPackWriter.MaximumPageRecordCount;
            if (!Enum.IsDefined(kind)
                || recordCount <= 0
                || recordCount > maximumRecords
                || firstOrdinal < 0
                || minimumTick > maximumTick
                || kind == PureMidiContentRecordKind.NoteOnEndpoint
                    && maximumActiveEndTick <= maximumTick
                || kind != PureMidiContentRecordKind.NoteOnEndpoint
                    && maximumActiveEndTick != -1
                || minimumId.CompareTo(maximumId) > 0
                || minimumRasterValue > maximumRasterValue
                || offset < HeaderByteCount
                || storedBytes <= 0
                || storedBytes > PureMidiContentPackWriter.MaximumDecodedPageByteCount + 65_536
                || decodedBytes is <= 0 or > PureMidiContentPackWriter.MaximumDecodedPageByteCount
                || offset > directoryOffset - storedBytes
                || checksum.Length != 32)
            {
                throw new InvalidDataException($"Pure MIDI content directory entry {index} is invalid.");
            }
            var key = (segmentId, kind);
            int expectedOrdinal = nextOrdinals.GetValueOrDefault(key);
            if (firstOrdinal != expectedOrdinal)
                throw new InvalidDataException($"Pure MIDI content page ordinal {index} is discontinuous.");
            nextOrdinals[key] = checked(firstOrdinal + recordCount);
            long payloadEnd = checked(offset + storedBytes);
            if (offset < previousPayloadEnd)
                throw new InvalidDataException($"Pure MIDI content page {index} overlaps or precedes another payload extent.");
            previousPayloadEnd = payloadEnd;
            pages[index] = new(
                index,
                segmentId,
                kind,
                firstOrdinal,
                recordCount,
                minimumTick,
                maximumTick,
                maximumActiveEndTick,
                minimumId,
                maximumId,
                minimumKey,
                maximumKey,
                laneMaskLow,
                laneMaskHigh,
                minimumRasterValue,
                maximumRasterValue,
                offset,
                storedBytes,
                decodedBytes,
                checksum);
        }
        return pages;
    }

    private static byte[] ReadExactly(SafeFileHandle handle, long offset, int byteCount)
    {
        byte[] result = new byte[byteCount];
        int position = 0;
        while (position < result.Length)
        {
            int read = RandomAccess.Read(handle, result.AsSpan(position), offset + position);
            if (read == 0) throw new EndOfStreamException("Pure MIDI content pack is truncated.");
            position += read;
        }
        return result;
    }

    private sealed class SegmentSource :
        IPureMidiSegmentContentSource,
        IPureMidiPlaybackEndpointSource,
        IPureMidiContentOverviewSource,
        IPureMidiContentBoundsSource,
        IPureMidiContentRangeFingerprintSource,
        IPureMidiCachedContentSource,
        IPureMidiNoteExclusionAwareSource,
        IPureMidiContentPackSegmentSource
    {
        private readonly PureMidiContentPack _owner;
        private readonly PageDescriptor[] _notePages;
        private readonly PageDescriptor[] _noteOnEndpointPages;
        private readonly PageDescriptor[] _noteOffEndpointPages;
        private readonly PageDescriptor[] _channelPages;
        private readonly PageDescriptor[] _channelEndpointPages;
        private readonly PageDescriptor[] _opaquePages;
        private readonly PageRangeIndex _noteRangeIndex;
        private readonly PageRangeIndex _noteOnRangeIndex;
        private readonly PageRangeIndex _noteOffRangeIndex;
        private readonly PageRangeIndex _activeNoteRangeIndex;
        private readonly PageRangeIndex _channelRangeIndex;
        private readonly PageRangeIndex _channelEndpointRangeIndex;
        private readonly PageRangeIndex _opaqueRangeIndex;
        private readonly long _maximumNoteEndTick;

        public SegmentSource(PureMidiContentPack owner, MidoraId segmentId)
        {
            _owner = owner;
            SegmentId = segmentId;
            _notePages = Pages(PureMidiContentRecordKind.Note);
            _noteOnEndpointPages = Pages(PureMidiContentRecordKind.NoteOnEndpoint);
            _noteOffEndpointPages = Pages(PureMidiContentRecordKind.NoteOffEndpoint);
            _channelPages = Pages(PureMidiContentRecordKind.ChannelEvent);
            _channelEndpointPages = Pages(PureMidiContentRecordKind.ChannelEventEndpoint);
            _opaquePages = Pages(PureMidiContentRecordKind.OpaqueEvent);
            _noteRangeIndex = new(_notePages, pointEvents: false);
            _noteOnRangeIndex = new(_noteOnEndpointPages, pointEvents: true);
            _noteOffRangeIndex = new(_noteOffEndpointPages, pointEvents: true);
            _activeNoteRangeIndex = new(
                _noteOnEndpointPages,
                pointEvents: false,
                static page => page.MaximumActiveEndTick);
            _channelRangeIndex = new(_channelPages, pointEvents: true);
            _channelEndpointRangeIndex = new(_channelEndpointPages, pointEvents: true);
            _opaqueRangeIndex = new(_opaquePages, pointEvents: true);
            _maximumNoteEndTick = _noteOffEndpointPages.Length == 0
                ? 0
                : _noteOffEndpointPages.Max(static page => page.MaximumTick);

            if (Count(_notePages) != Count(_noteOnEndpointPages)
                || Count(_notePages) != Count(_noteOffEndpointPages)
                || Count(_channelPages) != Count(_channelEndpointPages))
            {
                throw new InvalidDataException(
                    $"Pure MIDI content indexes for Segment {segmentId.Value} do not match their source record counts.");
            }

            PageDescriptor[] Pages(PureMidiContentRecordKind kind) => owner._pages
                .Where(value => value.SegmentId == segmentId && value.Kind == kind)
                .OrderBy(value => value.FirstOrdinal)
                .ToArray();
        }

        public MidoraId SegmentId { get; }
        PureMidiContentPack IPureMidiContentPackSegmentSource.Owner => _owner;
        public int NoteCount => Count(_notePages);
        public int ChannelEventCount => Count(_channelPages);
        public int OpaqueEventCount => Count(_opaquePages);
        public long MaximumNoteEndTick => _maximumNoteEndTick;
        public string ContentFingerprint => _owner.ContentFingerprint + ":" + SegmentId.Value;

        public ulong GetNoteRangeFingerprint(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127) => _noteRangeIndex.GetFingerprint(
                startTick,
                endTick,
                minimumKey,
                maximumKey,
                filterKeys: true);

        public ulong GetChannelEventRangeFingerprint(long startTick, long endTick) =>
            _channelRangeIndex.GetFingerprint(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false);

        public ulong GetOpaqueEventRangeFingerprint(long startTick, long endTick) =>
            _opaqueRangeIndex.GetFingerprint(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false);

        public bool TryQueryCachedNotes(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey,
            List<DirectMidiNoteValue> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            PageDescriptor[] pages = NoteRangePages(
                startTick,
                endTick,
                minimumKey,
                maximumKey);
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                foreach (DirectMidiNoteValue value in (DirectMidiNoteValue[])decoded[pageIndex])
                {
                    long noteEnd = value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
                        ? long.MaxValue
                        : value.StartTick + Math.Max(1, value.LengthTicks);
                    if (value.StartTick < endTick
                        && noteEnd > startTick
                        && value.Key >= minimumKey
                        && value.Key <= maximumKey)
                    {
                        destination.Add(value);
                    }
                }
            }
            return true;
        }

        public bool TryQueryCachedNotesExcluding(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey,
            IReadOnlySet<MidoraId> excludedIds,
            List<DirectMidiNoteValue> destination)
        {
            ArgumentNullException.ThrowIfNull(excludedIds);
            ArgumentNullException.ThrowIfNull(destination);
            int initialCount = destination.Count;
            PageDescriptor[] pages = NoteRangePages(
                    startTick,
                    endTick,
                    minimumKey,
                    maximumKey)
                .Where(page => !IsFullyExcluded(page, excludedIds))
                .ToArray();
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                PageDescriptor page = pages[pageIndex];
                DirectMidiNoteValue[] values = (DirectMidiNoteValue[])decoded[pageIndex];
                IPureMidiOrdinalRangeSet? ordinalExclusions =
                    excludedIds as IPureMidiOrdinalRangeSet;
                ArraySegment<int> pageOrdinals;
                if (ordinalExclusions is IPureMidiCachedOrdinalRangeSet cachedOrdinals)
                {
                    if (!cachedOrdinals.TryGetOrdinalsInRange(page.FirstOrdinal, page.RecordCount, out pageOrdinals))
                    {
                        destination.RemoveRange(initialCount, destination.Count - initialCount);
                        return false;
                    }
                }
                else pageOrdinals = ordinalExclusions?.GetOrdinalsInRange(page.FirstOrdinal, page.RecordCount) ?? default;
                int ordinalCursor = pageOrdinals.Offset;
                for (int localIndex = 0; localIndex < values.Length; localIndex++)
                {
                    DirectMidiNoteValue value = values[localIndex];
                    long noteEnd = value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
                        ? long.MaxValue
                        : value.StartTick + Math.Max(1, value.LengthTicks);
                    if (!IsExcludedSourceNote(
                            page,
                            localIndex,
                            value.Id,
                            excludedIds,
                            ordinalExclusions,
                            pageOrdinals,
                            ref ordinalCursor)
                        && value.StartTick < endTick
                        && noteEnd > startTick
                        && value.Key >= minimumKey
                        && value.Key <= maximumKey)
                    {
                        destination.Add(value);
                    }
                }
            }
            return true;
        }

        public bool TryQueryCachedChannelEvents(
            long startTick,
            long endTick,
            List<DirectMidiChannelEventValue> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            PageDescriptor[] pages = ChannelRangePages(startTick, endTick);
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                foreach (DirectMidiChannelEventValue value in
                    (DirectMidiChannelEventValue[])decoded[pageIndex])
                {
                    if (value.Tick >= startTick && value.Tick < endTick)
                        destination.Add(value);
                }
            }
            return true;
        }

        public bool TryQueryCachedOpaqueEvents(
            long startTick,
            long endTick,
            List<OpaqueMidiEventValue> destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            PageDescriptor[] pages = OpaqueRangePages(startTick, endTick);
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                foreach (OpaqueMidiEventValue value in (OpaqueMidiEventValue[])decoded[pageIndex])
                {
                    if (value.Tick >= startTick && value.Tick < endTick)
                        destination.Add(value);
                }
            }
            return true;
        }

        public bool TryQueryCachedNotesByIds(
            IReadOnlySet<MidoraId> ids,
            List<DirectMidiNoteSourceMatch> destination)
        {
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(destination);
            PageDescriptor[] pages = IdPages(_notePages, ids);
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                DirectMidiNoteValue[] values = (DirectMidiNoteValue[])decoded[pageIndex];
                for (int index = 0; index < values.Length; index++)
                {
                    if (ids.Contains(values[index].Id))
                        destination.Add(new(checked(pages[pageIndex].FirstOrdinal + index), values[index]));
                }
            }
            return true;
        }

        public bool TryQueryCachedChannelEventsByIds(
            IReadOnlySet<MidoraId> ids,
            List<DirectMidiChannelEventSourceMatch> destination)
        {
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(destination);
            PageDescriptor[] pages = IdPages(_channelPages, ids);
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])decoded[pageIndex];
                for (int index = 0; index < values.Length; index++)
                {
                    if (ids.Contains(values[index].Id))
                        destination.Add(new(checked(pages[pageIndex].FirstOrdinal + index), values[index]));
                }
            }
            return true;
        }

        public bool TryQueryCachedOpaqueEventsByIds(
            IReadOnlySet<MidoraId> ids,
            List<OpaqueMidiEventSourceMatch> destination)
        {
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(destination);
            PageDescriptor[] pages = IdPages(_opaquePages, ids);
            if (!TryAcquirePages(pages, out object[] decoded)) return false;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                OpaqueMidiEventValue[] values = (OpaqueMidiEventValue[])decoded[pageIndex];
                for (int index = 0; index < values.Length; index++)
                {
                    if (ids.Contains(values[index].Id))
                        destination.Add(new(checked(pages[pageIndex].FirstOrdinal + index), values[index]));
                }
            }
            return true;
        }

        public void PrefetchNotes(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey,
            CancellationToken cancellationToken) => PrefetchPages(
                NoteRangePages(startTick, endTick, minimumKey, maximumKey),
                cancellationToken);

        public void PrefetchNotesExcluding(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey,
            IReadOnlySet<MidoraId> excludedIds,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(excludedIds);
            PrefetchPages(
                NoteRangePages(startTick, endTick, minimumKey, maximumKey)
                    .Where(page => !IsFullyExcluded(page, excludedIds))
                    .ToArray(),
                cancellationToken);
        }

        public void PrefetchChannelEvents(
            long startTick,
            long endTick,
            CancellationToken cancellationToken) => PrefetchPages(
                ChannelRangePages(startTick, endTick),
                cancellationToken);

        public void PrefetchOpaqueEvents(
            long startTick,
            long endTick,
            CancellationToken cancellationToken) => PrefetchPages(
                OpaqueRangePages(startTick, endTick),
                cancellationToken);

        public void PrefetchNotesByIds(
            IReadOnlySet<MidoraId> ids,
            CancellationToken cancellationToken) => PrefetchPages(
                IdPages(_notePages, ids),
                cancellationToken);

        public void PrefetchChannelEventsByIds(
            IReadOnlySet<MidoraId> ids,
            CancellationToken cancellationToken) => PrefetchPages(
                IdPages(_channelPages, ids),
                cancellationToken);

        public void PrefetchOpaqueEventsByIds(
            IReadOnlySet<MidoraId> ids,
            CancellationToken cancellationToken) => PrefetchPages(
                IdPages(_opaquePages, ids),
                cancellationToken);

        public IEnumerable<PureMidiContentRangeSummary> GetNoteRangeSummaries() =>
            _noteOnEndpointPages.Select(static page => new PureMidiContentRangeSummary(
                page.MinimumTick,
                page.MaximumTick,
                page.RecordCount,
                page.MinimumKey,
                page.MaximumKey));

        public IEnumerable<PureMidiContentRangeSummary> GetChannelEventRangeSummaries() =>
            _channelEndpointPages.Select(static page => new PureMidiContentRangeSummary(
                page.MinimumTick,
                page.MaximumTick,
                page.RecordCount));

        public IEnumerable<PureMidiContentRangeSummary> GetOpaqueEventRangeSummaries() =>
            _opaquePages.Select(static page => new PureMidiContentRangeSummary(
                page.MinimumTick,
                page.MaximumTick,
                page.RecordCount));

        public bool TryAccumulateNoteStartColumns(
            long extent,
            Span<byte> destination,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            PureMidiOverviewProjection.Validate(extent, destination);
            if (destination.IsEmpty) return true;
            AccumulateNoteStartPages(
                _noteOnEndpointPages,
                extent,
                destination,
                excludedIds);
            return true;
        }

        public bool TryAccumulateNoteRasterColumns(
            TimelineRasterColumnProjection projection,
            int minimumKey,
            int maximumKey,
            Span<TimelineRasterColumnSummary> destination,
            IReadOnlySet<MidoraId>? excludedIds,
            out int sourceWorkCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maximumKey < minimumKey) throw new ArgumentOutOfRangeException(nameof(maximumKey));
            sourceWorkCount = 0;
            if (destination.IsEmpty) return true;
            foreach (PageDescriptor page in _noteOnEndpointPages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.MaximumActiveEndTick <= projection.StartTick
                    || page.MinimumTick >= projection.EndTick
                    || page.MaximumKey < minimumKey
                    || page.MinimumKey > maximumKey)
                {
                    continue;
                }
                if (IsFullyExcluded(page, excludedIds)) continue;
                bool singleColumn = projection.TryGetColumns(
                    page.MinimumTick,
                    page.MaximumActiveEndTick,
                    out int first,
                    out int lastExclusive)
                    && lastExclusive - first == 1;
                bool decode = !singleColumn || MayContainExcludedRecord(page, excludedIds, cancellationToken);
                if (!decode)
                {
                    (ulong low, ulong high) = FilterLaneMask(
                        page.LaneMaskLow,
                        page.LaneMaskHigh,
                        minimumKey,
                        maximumKey);
                    if ((low | high) != 0)
                    {
                        destination[first].Include(
                            low,
                            high,
                            page.MinimumRasterValue / 127d,
                            page.MaximumRasterValue / 127d,
                            page.RecordCount);
                        if (projection.StartTick <= page.MinimumTick
                            && projection.EndTick >= page.MaximumActiveEndTick)
                        {
                            destination[first].IncludeStartBoundary(
                                low,
                                high);
                            destination[lastExclusive - 1].IncludeEndBoundary(
                                low,
                                high);
                        }
                    }
                    sourceWorkCount++;
                    continue;
                }

                int scanned = 0;
                foreach (DirectMidiNoteValue value in
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))
                {
                    if ((scanned++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (excludedIds?.Contains(value.Id) == true
                        || value.Key < minimumKey
                        || value.Key > maximumKey
                        || value.StartTick >= projection.EndTick
                        || SafeNoteEnd(value) <= projection.StartTick)
                    {
                        continue;
                    }
                    ulong low = value.Key < 64 ? 1UL << value.Key : 0;
                    ulong high = value.Key >= 64 ? 1UL << (value.Key - 64) : 0;
                    PagedTimelineRasterProjection.IncludeExact(
                        destination,
                        projection,
                        value.StartTick,
                        SafeNoteEnd(value),
                        low,
                        high,
                        value.NoteOnVelocity / 127d,
                        value.NoteOnVelocity / 127d,
                        1);
                    sourceWorkCount++;
                }
            }
            return true;
        }

        private static long SafeNoteEnd(DirectMidiNoteValue value) =>
            value.LengthTicks <= 0 || value.StartTick > long.MaxValue - value.LengthTicks
                ? long.MaxValue
                : value.StartTick + value.LengthTicks;

        private static (ulong Low, ulong High) FilterLaneMask(
            ulong low,
            ulong high,
            int minimumKey,
            int maximumKey)
        {
            ulong allowedLow = RangeMask(Math.Clamp(minimumKey, 0, 64), Math.Clamp(maximumKey + 1, 0, 64));
            ulong allowedHigh = RangeMask(Math.Clamp(minimumKey - 64, 0, 64), Math.Clamp(maximumKey - 63, 0, 64));
            return (low & allowedLow, high & allowedHigh);
        }

        private static ulong RangeMask(int first, int lastExclusive)
        {
            if (lastExclusive <= first) return 0;
            ulong belowLast = lastExclusive == 64 ? ulong.MaxValue : (1UL << lastExclusive) - 1;
            ulong belowFirst = first == 0 ? 0 : (1UL << first) - 1;
            return belowLast & ~belowFirst;
        }

        public bool TryAccumulateChannelEventColumns(
            long extent,
            Span<byte> noteStartColumns,
            Span<byte> eventColumns,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            PureMidiOverviewProjection.Validate(
                extent,
                noteStartColumns,
                eventColumns);
            if (noteStartColumns.IsEmpty) return true;
            foreach (PageDescriptor page in _channelEndpointPages)
            {
                foreach (DirectMidiChannelEventValue value in
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index))
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
            return true;
        }

        public bool TryAccumulateOpaqueEventColumns(
            long extent,
            Span<byte> destination,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            PureMidiOverviewProjection.Validate(extent, destination);
            if (destination.IsEmpty) return true;
            foreach (PageDescriptor page in _opaquePages)
            {
                int firstColumn = PureMidiOverviewProjection.Column(
                    page.MinimumTick,
                    extent,
                    destination.Length);
                int lastColumn = PureMidiOverviewProjection.Column(
                    page.MaximumTick,
                    extent,
                    destination.Length);
                if (firstColumn == lastColumn
                    && !MayContainExcludedId(page, excludedIds))
                {
                    destination[firstColumn] = 1;
                    continue;
                }

                foreach (OpaqueMidiEventValue value in
                    (OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        destination,
                        value.Tick,
                        extent);
                }
            }
            return true;
        }

        public DirectMidiNoteValue GetNote(int index)
        {
            PageDescriptor page = FindPage(_notePages, index);
            return ((DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))[index - page.FirstOrdinal];
        }

        public DirectMidiChannelEventValue GetChannelEvent(int index)
        {
            PageDescriptor page = FindPage(_channelPages, index);
            return ((DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index))[index - page.FirstOrdinal];
        }

        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            PageDescriptor page = FindPage(_opaquePages, index);
            return ((OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index))[index - page.FirstOrdinal];
        }

        public int FindNoteIndex(MidoraId id) => FindById(_notePages, id, static (page, owner) =>
            ((DirectMidiNoteValue[])owner.GetDecodedPage(page.Index)).Select(value => value.Id));

        public int FindChannelEventIndex(MidoraId id) => FindById(_channelPages, id, static (page, owner) =>
            ((DirectMidiChannelEventValue[])owner.GetDecodedPage(page.Index)).Select(value => value.Id));

        public int FindOpaqueEventIndex(MidoraId id) => FindById(_opaquePages, id, static (page, owner) =>
            ((OpaqueMidiEventValue[])owner.GetDecodedPage(page.Index)).Select(value => value.Id));

        public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) yield break;
            MidoraId[] sortedIds = ids.Order().ToArray();
            foreach (PageDescriptor page in _notePages)
            {
                if (!MayContainRequestedId(page, sortedIds)) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    DirectMidiNoteValue value = values[index];
                    if (ids.Contains(value.Id))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesAtStarts(
            IReadOnlySet<DirectMidiNoteStartKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            if (keys.Count == 0) yield break;
            Dictionary<long, HashSet<int>> keysByTick = keys
                .GroupBy(static key => key.Tick)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Select(static key => key.Key).ToHashSet());
            long[] sortedTicks = keysByTick.Keys.Order().ToArray();
            foreach (PageDescriptor page in _noteOnEndpointPages)
            {
                if (!MayContainRequestedTick(page, sortedTicks)) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                int tickIndex = Array.BinarySearch(sortedTicks, page.MinimumTick);
                if (tickIndex < 0) tickIndex = ~tickIndex;
                while (tickIndex < sortedTicks.Length
                    && sortedTicks[tickIndex] <= page.MaximumTick)
                {
                    long tick = sortedTicks[tickIndex++];
                    HashSet<int> requestedKeys = keysByTick[tick];
                    int index = LowerBoundNote(values, tick, noteOn: true);
                    while (index < values.Length && values[index].StartTick == tick)
                    {
                        DirectMidiNoteValue value = values[index++];
                        if (requestedKeys.Contains(value.Key))
                        {
                            // Endpoint pages are ordered by tick rather than source
                            // ordinal. Note collision resolution never removes an
                            // untouched source incumbent, so the source index is not
                            // needed for this bounded lookup.
                            yield return new(-1, value);
                        }
                    }
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsByIds(
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) yield break;
            MidoraId[] sortedIds = ids.Order().ToArray();
            foreach (PageDescriptor page in _channelPages)
            {
                if (!MayContainRequestedId(page, sortedIds)) continue;
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    DirectMidiChannelEventValue value = values[index];
                    if (ids.Contains(value.Id))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventSourceMatch> QueryChannelEventsAtStarts(
            IReadOnlySet<DirectMidiEventStartKey> keys)
        {
            ArgumentNullException.ThrowIfNull(keys);
            if (keys.Count == 0) yield break;
            long[] sortedTicks = keys.Select(static key => key.Tick).Distinct().Order().ToArray();
            foreach (PageDescriptor page in _channelPages)
            {
                if (!MayContainRequestedTick(page, sortedTicks)) continue;
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    DirectMidiChannelEventValue value = values[index];
                    int selector = value.Kind is DirectMidiChannelEventKind.ControlChange
                        or DirectMidiChannelEventKind.PolyphonicKeyPressure
                        or DirectMidiChannelEventKind.NoteOn
                        or DirectMidiChannelEventKind.NoteOff
                            ? value.Data1
                            : 0;
                    if (keys.Contains(new(value.Tick, value.Kind, selector)))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) yield break;
            MidoraId[] sortedIds = ids.Order().ToArray();
            foreach (PageDescriptor page in _opaquePages)
            {
                if (!MayContainRequestedId(page, sortedIds)) continue;
                OpaqueMidiEventValue[] values =
                    (OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index);
                for (int index = 0; index < values.Length; index++)
                {
                    OpaqueMidiEventValue value = values[index];
                    if (ids.Contains(value.Id))
                        yield return new(checked(page.FirstOrdinal + index), value);
                }
            }
        }

        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127)
        {
            foreach (PageDescriptor page in _noteRangeIndex.Query(
                startTick,
                endTick,
                minimumKey,
                maximumKey,
                filterKeys: true))
            {
                foreach (DirectMidiNoteValue value in (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))
                {
                    long noteEnd = value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
                        ? long.MaxValue
                        : value.StartTick + Math.Max(1, value.LengthTicks);
                    if (value.StartTick < endTick && noteEnd > startTick
                        && value.Key >= minimumKey && value.Key <= maximumKey)
                        yield return value;
                }
            }
        }

        public IEnumerable<DirectMidiNoteValue> QueryNotesExcluding(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey,
            IReadOnlySet<MidoraId> excludedIds)
        {
            ArgumentNullException.ThrowIfNull(excludedIds);
            foreach (PageDescriptor page in _noteRangeIndex.Query(
                startTick,
                endTick,
                minimumKey,
                maximumKey,
                filterKeys: true))
            {
                if (IsFullyExcluded(page, excludedIds)) continue;
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                IPureMidiOrdinalRangeSet? ordinalExclusions =
                    excludedIds as IPureMidiOrdinalRangeSet;
                ArraySegment<int> pageOrdinals = ordinalExclusions?.GetOrdinalsInRange(
                    page.FirstOrdinal,
                    page.RecordCount) ?? default;
                int ordinalCursor = pageOrdinals.Offset;
                for (int localIndex = 0; localIndex < values.Length; localIndex++)
                {
                    DirectMidiNoteValue value = values[localIndex];
                    long noteEnd = value.StartTick > long.MaxValue - Math.Max(1, value.LengthTicks)
                        ? long.MaxValue
                        : value.StartTick + Math.Max(1, value.LengthTicks);
                    if (!IsExcludedSourceNote(
                            page,
                            localIndex,
                            value.Id,
                            excludedIds,
                            ordinalExclusions,
                            pageOrdinals,
                            ref ordinalCursor)
                        && value.StartTick < endTick
                        && noteEnd > startTick
                        && value.Key >= minimumKey
                        && value.Key <= maximumKey)
                    {
                        yield return value;
                    }
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick)
        {
            foreach (PageDescriptor page in _channelRangeIndex.Query(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false))
            {
                foreach (DirectMidiChannelEventValue value in
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index))
                    if (value.Tick >= startTick && value.Tick < endTick) yield return value;
            }
        }

        public IEnumerable<DirectMidiNoteValue> QueryNoteStarts(
            long startTick,
            long endTick) =>
            QueryNoteEndpoints(
                _noteOnRangeIndex,
                startTick,
                endTick,
                noteOn: true);

        public long CountNoteStarts(long startTick, long endTick, CancellationToken cancellationToken = default)
        {
            long count = 0;
            if (endTick <= startTick) return count;
            foreach (PageDescriptor page in _noteOnRangeIndex.Query(startTick, endTick,
                int.MinValue, int.MaxValue, filterKeys: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.MinimumTick >= startTick && page.MaximumTick < endTick)
                    count += page.RecordCount;
                else
                {
                    var values = (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                    count += LowerBoundNote(values, endTick, noteOn: true)
                        - LowerBoundNote(values, startTick, noteOn: true);
                }
            }
            return count;
        }

        public IEnumerable<DirectMidiNoteValue> QueryNoteEnds(
            long startTick,
            long endTick) =>
            QueryNoteEndpoints(
                _noteOffRangeIndex,
                startTick,
                endTick,
                noteOn: false);

        public IEnumerable<DirectMidiNoteValue> QueryActiveNotes(long tick)
        {
            if (tick <= 0) yield break;
            foreach (PageDescriptor page in _activeNoteRangeIndex.QueryActiveAt(tick))
            {
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                int exclusiveEnd = LowerBoundNote(values, tick, noteOn: true);
                for (int index = 0; index < exclusiveEnd; index++)
                {
                    DirectMidiNoteValue value = values[index];
                    if (SaturatingAdd(value.StartTick, value.LengthTicks) > tick)
                    {
                        yield return value;
                    }
                }
            }
        }

        public IEnumerable<DirectMidiChannelEventValue> QueryOrderedChannelEvents(
            long startTick,
            long endTick)
        {
            if (endTick <= startTick) yield break;
            PriorityQueue<ChannelEndpointCursor, ChannelEndpointPriority> queue = new();
            foreach (PageDescriptor page in _channelEndpointRangeIndex.Query(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false))
            {
                DirectMidiChannelEventValue[] values =
                    (DirectMidiChannelEventValue[])_owner.GetDecodedPage(page.Index);
                int index = LowerBoundChannel(values, startTick);
                if (index < values.Length && values[index].Tick < endTick)
                {
                    ChannelEndpointCursor cursor = new(_owner, page.Index, values, index);
                    queue.Enqueue(cursor, ChannelPriority(cursor.Current));
                }
            }
            while (queue.TryDequeue(
                out ChannelEndpointCursor? cursor,
                out _))
            {
                DirectMidiChannelEventValue value = cursor.Current;
                yield return value;
                if (cursor.MoveNext() && cursor.Current.Tick < endTick)
                {
                    queue.Enqueue(cursor, ChannelPriority(cursor.Current));
                }
            }
        }

        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick)
        {
            foreach (PageDescriptor page in _opaqueRangeIndex.Query(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false))
            {
                foreach (OpaqueMidiEventValue value in
                    (OpaqueMidiEventValue[])_owner.GetDecodedPage(page.Index))
                    if (value.Tick >= startTick && value.Tick < endTick) yield return value;
            }
        }

        private IEnumerable<DirectMidiNoteValue> QueryNoteEndpoints(
            PageRangeIndex pageIndex,
            long startTick,
            long endTick,
            bool noteOn)
        {
            if (endTick <= startTick) yield break;
            PriorityQueue<NoteEndpointCursor, NoteEndpointPriority> queue = new();
            foreach (PageDescriptor page in pageIndex.Query(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false))
            {
                DirectMidiNoteValue[] values =
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index);
                int index = LowerBoundNote(values, startTick, noteOn);
                if (index < values.Length && EndpointTick(values[index], noteOn) < endTick)
                {
                    NoteEndpointCursor cursor = new(_owner, page.Index, values, index);
                    queue.Enqueue(cursor, NotePriority(cursor.Current, noteOn));
                }
            }
            while (queue.TryDequeue(out NoteEndpointCursor? cursor, out _))
            {
                DirectMidiNoteValue value = cursor.Current;
                yield return value;
                if (cursor.MoveNext()
                    && EndpointTick(cursor.Current, noteOn) < endTick)
                {
                    queue.Enqueue(cursor, NotePriority(cursor.Current, noteOn));
                }
            }
        }

        private void AccumulateNoteStartPages(
            IEnumerable<PageDescriptor> pages,
            long extent,
            Span<byte> destination,
            IReadOnlySet<MidoraId>? excludedIds)
        {
            foreach (PageDescriptor page in pages)
            {
                if (IsFullyExcluded(page, excludedIds)) continue;
                int firstColumn = PureMidiOverviewProjection.Column(
                    page.MinimumTick,
                    extent,
                    destination.Length);
                int lastColumn = PureMidiOverviewProjection.Column(
                    page.MaximumTick,
                    extent,
                    destination.Length);
                if (firstColumn == lastColumn
                    && !MayContainExcludedRecord(page, excludedIds))
                {
                    destination[firstColumn] = 1;
                    continue;
                }

                foreach (DirectMidiNoteValue value in
                    (DirectMidiNoteValue[])_owner.GetDecodedPage(page.Index))
                {
                    if (excludedIds?.Contains(value.Id) == true) continue;
                    PureMidiOverviewProjection.Mark(
                        destination,
                        value.StartTick,
                        extent);
                }
            }
        }

        private static bool MayContainExcludedId(
            PageDescriptor page,
            IReadOnlySet<MidoraId>? excludedIds,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (excludedIds is null || excludedIds.Count == 0) return false;
            if (excludedIds is IPureMidiIdRangeSet ranged)
                return ranged.MayContain(page.MinimumId, page.MaximumId);
            int scanned = 0;
            foreach (MidoraId id in excludedIds)
            {
                if ((scanned++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (id.CompareTo(page.MinimumId) >= 0
                    && id.CompareTo(page.MaximumId) <= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MayContainExcludedRecord(
            PageDescriptor page,
            IReadOnlySet<MidoraId>? excludedIds,
            CancellationToken cancellationToken = default) =>
            excludedIds is IPureMidiOrdinalRangeSet ordinal
                ? ordinal.MayContainOrdinalRange(page.FirstOrdinal, page.RecordCount)
                : MayContainExcludedId(page, excludedIds, cancellationToken);

        private static bool IsFullyExcluded(
            PageDescriptor page,
            IReadOnlySet<MidoraId>? excludedIds) =>
            excludedIds is IPureMidiOrdinalRangeSet ordinal
            && ordinal.ContainsAllOrdinals(page.FirstOrdinal, page.RecordCount);

        private static bool IsExcludedSourceNote(
            PageDescriptor page,
            int localIndex,
            MidoraId id,
            IReadOnlySet<MidoraId> excludedIds,
            IPureMidiOrdinalRangeSet? ordinalExclusions,
            ArraySegment<int> pageOrdinals,
            ref int ordinalCursor)
        {
            if (ordinalExclusions is null) return excludedIds.Contains(id);

            int sourceOrdinal = checked(page.FirstOrdinal + localIndex);
            int ordinalEnd = checked(pageOrdinals.Offset + pageOrdinals.Count);
            int[]? values = pageOrdinals.Array;
            while (values is not null
                && ordinalCursor < ordinalEnd
                && values[ordinalCursor] < sourceOrdinal)
            {
                ordinalCursor++;
            }
            if (values is not null
                && ordinalCursor < ordinalEnd
                && values[ordinalCursor] == sourceOrdinal)
            {
                return true;
            }

            // IDs without a resolved source ordinal are uncommon, but they must
            // still be checked exactly rather than being dropped from the view.
            return ordinalExclusions.ContainsUnknownOrdinalId(id);
        }

        private static bool MayContainRequestedId(
            PageDescriptor page,
            MidoraId[] sortedIds)
        {
            int low = 0;
            int high = sortedIds.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (sortedIds[middle].CompareTo(page.MinimumId) < 0) low = middle + 1;
                else high = middle;
            }
            return low < sortedIds.Length
                && sortedIds[low].CompareTo(page.MaximumId) <= 0;
        }

        private static bool MayContainRequestedTick(
            PageDescriptor page,
            long[] sortedTicks)
        {
            int low = Array.BinarySearch(sortedTicks, page.MinimumTick);
            if (low < 0) low = ~low;
            return low < sortedTicks.Length && sortedTicks[low] <= page.MaximumTick;
        }

        private static int LowerBoundNote(
            DirectMidiNoteValue[] values,
            long tick,
            bool noteOn)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (EndpointTick(values[middle], noteOn) < tick) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static int LowerBoundChannel(
            DirectMidiChannelEventValue[] values,
            long tick)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (values[middle].Tick < tick) low = middle + 1;
                else high = middle;
            }
            return low;
        }

        private static long EndpointTick(DirectMidiNoteValue value, bool noteOn) =>
            noteOn ? value.StartTick : SaturatingAdd(value.StartTick, value.LengthTicks);

        private static long SaturatingAdd(long left, long right) =>
            right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;

        private static NoteEndpointPriority NotePriority(
            DirectMidiNoteValue value,
            bool noteOn) => new(
                EndpointTick(value, noteOn),
                noteOn ? value.NoteOnOrder : value.NoteOffOrder,
                value.Id.Value);

        private static ChannelEndpointPriority ChannelPriority(
            DirectMidiChannelEventValue value) =>
            new(value.Tick, value.Order, value.Id.Value);

        private int FindById(
            IReadOnlyList<PageDescriptor> pages,
            MidoraId id,
            Func<PageDescriptor, PureMidiContentPack, IEnumerable<MidoraId>> getIds)
        {
            foreach (PageDescriptor page in pages)
            {
                if (id.CompareTo(page.MinimumId) < 0 || id.CompareTo(page.MaximumId) > 0) continue;
                int localIndex = 0;
                foreach (MidoraId value in getIds(page, _owner))
                {
                    if (value == id) return checked(page.FirstOrdinal + localIndex);
                    localIndex++;
                }
            }
            return -1;
        }

        private static int Count(IReadOnlyList<PageDescriptor> pages) => pages.Count == 0
            ? 0
            : checked(pages[^1].FirstOrdinal + pages[^1].RecordCount);

        private static PageDescriptor FindPage(IReadOnlyList<PageDescriptor> pages, int index)
        {
            if (index < 0 || pages.Count == 0 || index >= Count(pages))
                throw new ArgumentOutOfRangeException(nameof(index));
            int low = 0;
            int high = pages.Count - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                PageDescriptor page = pages[middle];
                if (index < page.FirstOrdinal) high = middle - 1;
                else if (index >= page.FirstOrdinal + page.RecordCount) low = middle + 1;
                else return page;
            }
            throw new InvalidDataException("Pure MIDI content ordinal is missing from the page catalog.");
        }

        private PageDescriptor[] NoteRangePages(
            long startTick,
            long endTick,
            int minimumKey,
            int maximumKey) => _noteRangeIndex.Query(
                startTick,
                endTick,
                minimumKey,
                maximumKey,
                filterKeys: true).ToArray();

        private PageDescriptor[] ChannelRangePages(long startTick, long endTick) =>
            _channelRangeIndex.Query(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false).ToArray();

        private PageDescriptor[] OpaqueRangePages(long startTick, long endTick) =>
            _opaqueRangeIndex.Query(
                startTick,
                endTick,
                int.MinValue,
                int.MaxValue,
                filterKeys: false).ToArray();

        private static PageDescriptor[] IdPages(
            IEnumerable<PageDescriptor> pages,
            IReadOnlySet<MidoraId> ids)
        {
            ArgumentNullException.ThrowIfNull(ids);
            if (ids.Count == 0) return [];
            MidoraId[] sorted = ids.Order().ToArray();
            return pages.Where(page => MayContainRequestedId(page, sorted)).ToArray();
        }

        private bool TryAcquirePages(
            PageDescriptor[] pages,
            out object[] decoded)
        {
            int[] indexes = new int[pages.Length];
            for (int index = 0; index < pages.Length; index++)
                indexes[index] = pages[index].Index;
            return _owner.TryGetDecodedPages(indexes, out decoded);
        }

        private void PrefetchPages(
            PageDescriptor[] pages,
            CancellationToken cancellationToken)
        {
            foreach (PageDescriptor page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = _owner.GetDecodedPage(page.Index, cancellationToken);
            }
        }

        /// <summary>
        /// Metadata-only interval index over immutable page descriptors.  Page
        /// arrays are ordinal ordered for persistence and are not guaranteed to
        /// be tick ordered after arbitrary editing followed by Save.  A range
        /// query therefore cannot binary-search the ordinal array directly.
        /// Sorting only descriptors by their minimum tick and retaining a
        /// prefix maximum gives allocation-free O(log P + candidates) lookups
        /// without decoding a page.  This path is used by UI tile fingerprints
        /// as well as background range enumeration.
        /// </summary>
        private sealed class PageRangeIndex
        {
            private readonly PageDescriptor[] _pages;
            private readonly long[] _maximumPrefix;
            private readonly bool _pointEvents;

            public PageRangeIndex(
                IEnumerable<PageDescriptor> pages,
                bool pointEvents,
                Func<PageDescriptor, long>? getMaximumTick = null)
            {
                ArgumentNullException.ThrowIfNull(pages);
                _pointEvents = pointEvents;
                getMaximumTick ??= static page => page.MaximumTick;
                _pages = pages
                    .OrderBy(static page => page.MinimumTick)
                    .ThenBy(static page => page.FirstOrdinal)
                    .ToArray();
                _maximumPrefix = new long[_pages.Length];
                long maximum = long.MinValue;
                for (int index = 0; index < _pages.Length; index++)
                {
                    maximum = Math.Max(maximum, getMaximumTick(_pages[index]));
                    _maximumPrefix[index] = maximum;
                }
                GetMaximumTick = getMaximumTick;
            }

            private Func<PageDescriptor, long> GetMaximumTick { get; }

            public IEnumerable<PageDescriptor> Query(
                long startTick,
                long endTick,
                int minimumKey,
                int maximumKey,
                bool filterKeys)
            {
                Validate(startTick, endTick, minimumKey, maximumKey);
                int first = FirstPotential(startTick);
                int lastExclusive = FirstMinimumAtOrAfter(endTick);
                for (int index = first; index < lastExclusive; index++)
                {
                    PageDescriptor page = _pages[index];
                    long maximumTick = GetMaximumTick(page);
                    if ((_pointEvents ? maximumTick < startTick : maximumTick <= startTick)
                        || filterKeys
                            && (page.MaximumKey < minimumKey || page.MinimumKey > maximumKey))
                    {
                        continue;
                    }
                    yield return page;
                }
            }

            public IEnumerable<PageDescriptor> QueryActiveAt(long tick)
            {
                if (tick <= 0) yield break;
                int first = FirstPotential(tick);
                int lastExclusive = FirstMinimumAtOrAfter(tick);
                for (int index = first; index < lastExclusive; index++)
                {
                    PageDescriptor page = _pages[index];
                    if (page.MinimumTick < tick && GetMaximumTick(page) > tick)
                        yield return page;
                }
            }

            public ulong GetFingerprint(
                long startTick,
                long endTick,
                int minimumKey,
                int maximumKey,
                bool filterKeys)
            {
                Validate(startTick, endTick, minimumKey, maximumKey);
                // Page order is stable for a given catalog.  This is a cache
                // identity, not a Project semantic ordering key.
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong fingerprint = offset;
                foreach (PageDescriptor page in Query(
                    startTick,
                    endTick,
                    minimumKey,
                    maximumKey,
                    filterKeys))
                {
                    ulong checksum = BinaryPrimitives.ReadUInt64LittleEndian(page.DecodedSha256);
                    fingerprint ^= checksum;
                    fingerprint *= prime;
                    fingerprint ^= unchecked((ulong)page.FirstOrdinal);
                    fingerprint *= prime;
                    fingerprint ^= unchecked((ulong)page.RecordCount);
                    fingerprint *= prime;
                }
                return fingerprint;
            }

            private int FirstPotential(long startTick)
            {
                int low = 0;
                int high = _maximumPrefix.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    bool before = _pointEvents
                        ? _maximumPrefix[middle] < startTick
                        : _maximumPrefix[middle] <= startTick;
                    if (before) low = middle + 1;
                    else high = middle;
                }
                return low;
            }

            private int FirstMinimumAtOrAfter(long endTick)
            {
                int low = 0;
                int high = _pages.Length;
                while (low < high)
                {
                    int middle = low + ((high - low) >> 1);
                    if (_pages[middle].MinimumTick < endTick) low = middle + 1;
                    else high = middle;
                }
                return low;
            }

            private static void Validate(
                long startTick,
                long endTick,
                int minimumKey,
                int maximumKey)
            {
                if (startTick < 0) throw new ArgumentOutOfRangeException(nameof(startTick));
                if (endTick <= startTick) throw new ArgumentOutOfRangeException(nameof(endTick));
                if (maximumKey < minimumKey) throw new ArgumentOutOfRangeException(nameof(maximumKey));
            }
        }

        private sealed class NoteEndpointCursor(
            PureMidiContentPack owner,
            int pageIndex,
            DirectMidiNoteValue[] values,
            int index)
        {
            private int _index = index;
            private readonly int _count = values.Length;
            private readonly WeakReference<DirectMidiNoteValue[]> _page = new(values);
            public DirectMidiNoteValue Current { get; private set; } = values[index];
            public bool MoveNext()
            {
                _index++;
                if (_index >= _count) return false;
                if (!_page.TryGetTarget(out var page))
                {
                    page = (DirectMidiNoteValue[])owner.GetDecodedPage(pageIndex);
                    _page.SetTarget(page);
                }
                Current = page[_index];
                return true;
            }
        }

        private sealed class ChannelEndpointCursor(
            PureMidiContentPack owner,
            int pageIndex,
            DirectMidiChannelEventValue[] values,
            int index)
        {
            private int _index = index;
            private readonly int _count = values.Length;
            private readonly WeakReference<DirectMidiChannelEventValue[]> _page = new(values);
            public DirectMidiChannelEventValue Current { get; private set; } = values[index];
            public bool MoveNext()
            {
                _index++;
                if (_index >= _count) return false;
                if (!_page.TryGetTarget(out var page))
                {
                    page = (DirectMidiChannelEventValue[])owner.GetDecodedPage(pageIndex);
                    _page.SetTarget(page);
                }
                Current = page[_index];
                return true;
            }
        }

        private readonly record struct NoteEndpointPriority(
            long Tick,
            long Order,
            long StableId) : IComparable<NoteEndpointPriority>
        {
            public int CompareTo(NoteEndpointPriority other)
            {
                int result = Tick.CompareTo(other.Tick);
                if (result != 0) return result;
                result = Order.CompareTo(other.Order);
                return result != 0 ? result : StableId.CompareTo(other.StableId);
            }
        }

        private readonly record struct ChannelEndpointPriority(
            long Tick,
            long Order,
            long StableId) : IComparable<ChannelEndpointPriority>
        {
            public int CompareTo(ChannelEndpointPriority other)
            {
                int result = Tick.CompareTo(other.Tick);
                if (result != 0) return result;
                result = Order.CompareTo(other.Order);
                return result != 0 ? result : StableId.CompareTo(other.StableId);
            }
        }
    }

    private sealed record PageDescriptor(
        int Index,
        MidoraId SegmentId,
        PureMidiContentRecordKind Kind,
        int FirstOrdinal,
        int RecordCount,
        long MinimumTick,
        long MaximumTick,
        long MaximumActiveEndTick,
        MidoraId MinimumId,
        MidoraId MaximumId,
        int MinimumKey,
        int MaximumKey,
        ulong LaneMaskLow,
        ulong LaneMaskHigh,
        int MinimumRasterValue,
        int MaximumRasterValue,
        long Offset,
        int StoredByteCount,
        int DecodedByteCount,
        byte[] DecodedSha256);

}
