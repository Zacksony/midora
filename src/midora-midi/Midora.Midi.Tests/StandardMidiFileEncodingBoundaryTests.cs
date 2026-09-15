using System.Buffers.Binary;

namespace Midora.Midi.Tests;

public sealed class StandardMidiFileEncodingBoundaryTests
{
    private const long M = StandardMidiFile.MaximumVariableLengthValue;
    private const long B = StandardMidiFile.MaximumTrackDataLength;

    [Theory]
    [InlineData(0, 0, "00")]
    [InlineData(M - 1, 0, "ffffff7e")]
    [InlineData(M, 0, "ffffff7f")]
    [InlineData(M + 1, 1, "01")]
    [InlineData(2 * M, 1, "ffffff7f")]
    [InlineData(2 * M + 1, 2, "01")]
    public void EmptyTrackTailPaddingIsMinimalAndRoundTrips(long endTick, long count, string tail)
    {
        using MemoryStream stream = new();
        var summary = StandardMidiFile.WriteType1(stream, 32767, [new(endTick, [])]);
        byte[] bytes = stream.ToArray();
        string expected = string.Concat(Enumerable.Repeat("ffffff7fff0100", (int)count)) + tail + "ff2f00";
        Assert.Equal(expected, Convert.ToHexString(bytes.AsSpan(22)).ToLowerInvariant());
        Assert.Equal(new StandardMidiFileWriteSummary(count, count == 0 ? 0 : 1), summary);
        ParsedStandardMidiFileTrack parsed = Assert.Single(StandardMidiFile.ParseType0Or1(bytes).Tracks);
        Assert.Equal(endTick, parsed.EndTick);
        Assert.Equal(count, parsed.Events.Count);
        Assert.All(parsed.Events, value =>
        {
            Assert.Equal(StandardMidiFileEventKind.Meta, value.Kind);
            Assert.Equal(StandardMidiFile.TextMetaType, value.Type);
            Assert.True(value.Data.IsEmpty);
        });
        stream.Position = 0;
        StandardMidiFile.ValidateType1(stream);
    }

    [Fact]
    public void LeadingMiddleAndTailPaddingPreserveOrderNotesAndOpaquePackets()
    {
        long start = M + 1, change = start + 2 * M + 1, end = change + M + 1;
        StandardMidiFileEvent[] original =
        [
            StandardMidiFileEvent.ChannelVoice(start, MidiMessage.ControlChange(9, 0, 127)),
            StandardMidiFileEvent.ChannelVoice(start, MidiMessage.ControlChange(9, 32, 1)),
            StandardMidiFileEvent.ChannelVoice(start, MidiMessage.ProgramChange(9, 127)),
            StandardMidiFileEvent.ChannelVoice(start, MidiMessage.NoteOn(9, 0, 127)),
            StandardMidiFileEvent.SystemExclusive(change, [0x41, 0x10]),
            StandardMidiFileEvent.SystemExclusive(change, [0x40, 0xf7], continuation: true),
            StandardMidiFileEvent.Meta(change, 0x7f, [0xff, 0x80]),
            // Existing empty Text must survive, not be classified/deleted as "our padding".
            StandardMidiFileEvent.Meta(change, StandardMidiFile.TextMetaType, []),
            StandardMidiFileEvent.ChannelVoice(change, MidiMessage.NoteOff(9, 0, 127))
        ];
        using MemoryStream stream = new();
        var summary = StandardMidiFile.WriteType1(stream, 1, [new(end, original)]);
        var parsed = Assert.Single(StandardMidiFile.ParseType0Or1(stream.ToArray()).Tracks);
        Assert.Equal(new StandardMidiFileWriteSummary(4, 1), summary);
        Assert.Equal(end, parsed.EndTick);
        var retained = parsed.Events.Where(value => value.Tick == start || value.Tick == change).ToArray();
        Assert.Equal(original.Length, retained.Length);
        for (int index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].Tick, retained[index].Tick);
            Assert.Equal(original[index].Kind, retained[index].Kind);
            Assert.Equal(original[index].Message, retained[index].Message);
            Assert.Equal(original[index].Type, retained[index].Type);
            Assert.Equal(original[index].Data.ToArray(), retained[index].Data.ToArray());
        }
        Assert.Equal(5, parsed.Events.Count(value => value.Kind == StandardMidiFileEventKind.Meta
            && value.Type == StandardMidiFile.TextMetaType));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void WholeWriterEnforcesExactTrackDataLimitBeforePadding(int relativeSize)
    {
        // About 4 GiB of padding is counted, not allocated or written to disk.
        const long pads = 613_566_754;
        long endTick = pads * M + 1;
        StandardMidiFileTrackSource track = new(endTick,
            [StandardMidiFileEvent.Meta(0, 1, new byte[9 + relativeSize])], "Boundary");
        using CountingStream output = new();
        if (relativeSize <= 0)
        {
            var summary = StandardMidiFile.WriteType1(output, 192, [track]);
            Assert.Equal(B + relativeSize + 22, output.Length);
            Assert.Equal((uint)(B + relativeSize), output.PatchedLengths[18]);
            Assert.Equal(pads, summary.PaddingEventCount);
            Assert.InRange(output.MaximumWriteLength, 1, 7 * StandardMidiFile.PaddingEventsPerBlock);
        }
        else
        {
            var error = Assert.Throws<StandardMidiFileEncodingException>(() =>
                StandardMidiFile.WriteType1(output, 192, [track]));
            Assert.Equal(StandardMidiFileEncodingBoundary.TrackDataLength, error.Boundary);
            Assert.Equal(B + 1, error.Actual);
            Assert.Equal(B, error.Limit);
            Assert.Equal(endTick, error.Tick);
            Assert.Equal(0, error.TrackIndex);
            Assert.Equal("Boundary", error.TrackName);
            // Header + original record only; no part of the enormous padding was emitted.
            Assert.Equal(36, output.Length);
        }
    }

    [Fact]
    public void LimitIsPerTrackNotPerFileAndDoesNotIncludeHeaders()
    {
        long end = 613_566_754L * M + 1;
        StandardMidiFileTrackSource track = new(end, [StandardMidiFileEvent.Meta(0, 1, new byte[9])]);
        using CountingStream output = new();
        StandardMidiFileWriteSummary summary = StandardMidiFile.WriteType1(output, 192, [track, track]);
        Assert.Equal(14 + 2 * (8 + B), output.Length);
        Assert.Equal(uint.MaxValue, output.PatchedLengths[18]);
        Assert.Equal(uint.MaxValue, output.PatchedLengths[26 + B]);
        Assert.Equal(2, summary.PaddedTrackCount);
    }

    [Fact]
    public void ReservesOnlyMinimumEotUntilTheActualTailIsKnown()
    {
        long used = B - 7;
        _ = StandardMidiFile.ReserveTrackRecord(ref used, 0, 2, false, 123);
        Assert.Equal(B - 4, used);
        _ = StandardMidiFile.ReserveTrackRecord(ref used, 0, 3, true, 123);
        Assert.Equal(B, used);
        long full = B - 4;
        Assert.Throws<StandardMidiFileEncodingException>(() =>
            StandardMidiFile.ReserveTrackRecord(ref full, 0, 2, false, 123));
        Assert.Equal(B - 4, full); // Failed reservations don't change the budget.
    }

    [Fact]
    public void NeverChargesTheEntireFutureEmptyRangeBeforeReadingIntermediateEvents()
    {
        // A huge end tick would fail a premature "everything after this event is empty" estimate.
        // The next iterator call must still be reached (and can cancel) after a short first record.
        using CancellationTokenSource cancel = new();
        IEnumerable<StandardMidiFileEvent> Events()
        {
            yield return StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 1));
            cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
        }
        using CountingStream output = new();
        Assert.Throws<OperationCanceledException>(() => StandardMidiFile.WriteType1(output, 192,
            [new(long.MaxValue, Events())], cancel.Token));
        Assert.Equal(26, output.Length);
    }

    [Fact]
    public void ExtremeInt64GapFailsWithoutAllocatingOrWritingPadding()
    {
        using CountingStream output = new();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<StandardMidiFileEncodingException>(() =>
            StandardMidiFile.WriteType1(output, 192, [new(long.MaxValue, [], "Empty") ]));
        Assert.Equal(34_359_738_496, StandardMidiFile.PlanDelta(long.MaxValue).PaddingEvents);
        Assert.True(error.Actual > B);
        Assert.Equal(22, output.Length);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 128 * 1024);
    }

    [Fact]
    public void PaddingCanBeCancelledBetweenBoundedBlocks()
    {
        using CancellationTokenSource cancel = new();
        using CountingStream output = new(count => { if (count > 4096) cancel.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => StandardMidiFile.WriteType1(output, 192,
            [new(100_000L * M + 1, [])], cancel.Token));
        Assert.Equal(22 + 7 * StandardMidiFile.PaddingEventsPerBlock, output.Length);
    }

    [Fact]
    public void PayloadWritesAreBoundedAndCancellationDoesNotWaitForEntirePayload()
    {
        using CancellationTokenSource cancel = new();
        using CountingStream output = new(count => { if (count > 4096) cancel.Cancel(); });
        var value = StandardMidiFileEvent.Meta(0, 0x7f, new byte[256 * 1024]);
        Assert.Throws<OperationCanceledException>(() => StandardMidiFile.WriteType1(output, 192,
            [new(0, [value])], cancel.Token));
        Assert.Equal(64 * 1024, output.MaximumWriteLength);
        Assert.True(output.Length < 128 * 1024);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(32767, 65535)]
    public void HeaderUpperBoundsDoNotDependOnUnitLimit(int tpq, int tracks) =>
        StandardMidiFile.ValidateEncodingHeader(tpq, tracks);

    [Theory]
    [InlineData(0, 1, StandardMidiFileEncodingBoundary.TicksPerQuarterNote)]
    [InlineData(32768, 1, StandardMidiFileEncodingBoundary.TicksPerQuarterNote)]
    [InlineData(192, 0, StandardMidiFileEncodingBoundary.TrackCount)]
    [InlineData(192, 65536, StandardMidiFileEncodingBoundary.TrackCount)]
    public void InvalidHeadersFailBeforeWriting(int tpq, int tracks, StandardMidiFileEncodingBoundary boundary)
    {
        using CountingStream output = new();
        var error = Assert.Throws<StandardMidiFileEncodingException>(() => StandardMidiFile.WriteType1(
            output, tpq, new StandardMidiFileTrackSource[tracks]));
        Assert.Equal(boundary, error.Boundary);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void PayloadLengthKeepsItsOwnStrictVlqLimit()
    {
        StandardMidiFile.ValidatePayloadLength(M, 12);
        var error = Assert.Throws<StandardMidiFileEncodingException>(() =>
            StandardMidiFile.ValidatePayloadLength(M + 1, 12));
        Assert.Equal(StandardMidiFileEncodingBoundary.PayloadLength, error.Boundary);
        Assert.Equal(M + 1, error.Actual);
        Assert.Equal(12, error.Tick);
    }

    [Fact]
    public void OrdinaryChannelFastPathAlsoReservesEotAtTheSizeLimit()
    {
        long nearEnd = 613_566_754L * M + 1;
        // Meta size = padding + delta(1) + FF/type/length(3) + 9 bytes = B - 4.
        using CountingStream stream = new();
        var error = Assert.Throws<StandardMidiFileEncodingException>(() => StandardMidiFile.WriteType1(stream, 192,
            [new(nearEnd, [StandardMidiFileEvent.Meta(nearEnd, 1, new byte[9]),
                StandardMidiFileEvent.ChannelVoice(nearEnd, MidiMessage.NoteOn(0, 60, 1))])]));
        Assert.Equal(B + 4, error.Actual);
        Assert.Equal(B - 4 + 22, stream.Length);
    }

    [Fact]
    public void OriginalChannelMessagesAfterLongDeltasAlwaysRetainExplicitStatus()
    {
        using MemoryStream stream = new();
        StandardMidiFile.WriteType1(stream, 192, [new(3 * M,
            [StandardMidiFileEvent.ChannelVoice(M + 1, MidiMessage.NoteOn(0, 60, 100)),
             StandardMidiFileEvent.ChannelVoice(2 * M + 2, MidiMessage.NoteOn(0, 61, 100)),
             StandardMidiFileEvent.ChannelVoice(3 * M, MidiMessage.NoteOff(0, 60, 13))])]);
        StandardMidiFile.ValidateType1(stream.ToArray());
        var notes = StandardMidiFile.ParseType0Or1(stream.ToArray()).Tracks.Single().Events
            .Where(e => e.Kind == StandardMidiFileEventKind.ChannelVoice).ToArray();
        Assert.Equal([M + 1, 2 * M + 2, 3 * M], notes.Select(e => e.Tick));
        Assert.Equal(MidiMessage.NoteOff(0, 60, 13), notes[^1].Message);
    }

    private sealed class CountingStream(Action<int>? afterWrite = null) : Stream
    {
        private long _position, _length;
        public Dictionary<long, uint> PatchedLengths { get; } = [];
        public int MaximumWriteLength { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length == 4 && _position < _length)
                PatchedLengths[_position] = BinaryPrimitives.ReadUInt32BigEndian(buffer);
            MaximumWriteLength = Math.Max(MaximumWriteLength, buffer.Length);
            _position = checked(_position + buffer.Length);
            _length = Math.Max(_length, _position);
            afterWrite?.Invoke(buffer.Length);
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void WriteByte(byte value) => Write(new ReadOnlySpan<byte>(in value));
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
