using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Midora.Midi.Tests;

public sealed class StandardMidiFileTests
{
    [Fact]
    public void EncodesType1GoldenBytesWithoutRunningStatus()
    {
        StandardMidiFileTrack conductor = new(
            128,
            [StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "C")]);
        StandardMidiFileTrack events = new(
            128,
            [
                StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "音"),
                StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [0]),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(128, MidiMessage.NoteOff(0, 60, 0))
            ]);

        byte[] actual = StandardMidiFile.EncodeType1(192, [conductor, events]);

        Assert.Equal(
            Hex("4d546864000000060001000200c0"
                + "4d54726b0000000a00ff0301438100ff2f00"
                + "4d54726b0000001900ff0303e99fb300ff21010000903c648100803c0000ff2f00"),
            actual);
        StandardMidiFile.ValidateType1(actual);
    }

    [Theory]
    [InlineData(0, "00")]
    [InlineData(127, "7f")]
    [InlineData(128, "8100")]
    [InlineData(16_383, "ff7f")]
    [InlineData(16_384, "818000")]
    [InlineData(StandardMidiFile.MaximumVariableLengthValue, "ffffff7f")]
    public void EncodesCanonicalVariableLengthDeltas(int endTick, string expectedDelta)
    {
        StandardMidiFileTrack track = new(endTick, []);

        byte[] actual = StandardMidiFile.EncodeType1(192, [track]);

        Assert.Equal(expectedDelta + "ff2f00", Convert.ToHexString(actual.AsSpan(22)).ToLowerInvariant());
    }

    [Fact]
    public void PadsDeltaOutsideSmfVariableLengthRange()
    {
        StandardMidiFileTrack track = new((long)StandardMidiFile.MaximumVariableLengthValue + 1, []);

        byte[] bytes = StandardMidiFile.EncodeType1(192, [track]);
        Assert.Equal("ffffff7fff010001ff2f00", Convert.ToHexString(bytes.AsSpan(22)).ToLowerInvariant());
    }

    [Fact]
    public void RejectsNonTpqnDivisionAndAllowsPerTrackEnds()
    {
        Assert.Throws<StandardMidiFileEncodingException>(() => StandardMidiFile.EncodeType1(0, [new(0, [])]));
        Assert.Throws<StandardMidiFileEncodingException>(() => StandardMidiFile.EncodeType1(32_768, [new(0, [])]));
        byte[] result = StandardMidiFile.EncodeType1(
            192,
            [new StandardMidiFileTrack(0, []), new StandardMidiFileTrack(1, [])]);
        StandardMidiFile.ValidateType1(result);
    }

    [Fact]
    public void ValidationRejectsRunningStatusAndMissingEndOfTrack()
    {
        byte[] runningStatus = StandardMidiFile.EncodeType1(
            192,
            [new(0, [StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100))])]);
        int channelStatusIndex = runningStatus.AsSpan().IndexOf(new byte[] { 0x90, 0x3c, 0x64 });
        Assert.True(channelStatusIndex >= 0);
        runningStatus[channelStatusIndex] = 0x3c;

        Assert.Throws<MidoraMidiException>(() => StandardMidiFile.ValidateType1(runningStatus));

        byte[] missingEnd = StandardMidiFile.EncodeType1(192, [new(0, [])]);
        BinaryPrimitives.WriteUInt32BigEndian(missingEnd.AsSpan(18, 4), 0);
        Array.Resize(ref missingEnd, 22);
        Assert.Throws<MidoraMidiException>(() => StandardMidiFile.ValidateType1(missingEnd));
    }

    [Fact]
    public void RejectsExplicitEndOfTrackAndInvalidEventOrder()
    {
        Assert.Throws<ArgumentException>(() =>
            StandardMidiFileEvent.Meta(0, StandardMidiFile.EndOfTrackMetaType, []));

        StandardMidiFileTrack reversed = new(
            10,
            [
                StandardMidiFileEvent.ChannelVoice(10, MidiMessage.NoteOn(0, 60, 100)),
                StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOff(0, 60))
            ]);
        Assert.Throws<MidoraMidiException>(() => StandardMidiFile.EncodeType1(192, [reversed]));
    }

    [Fact]
    public void RejectsInvalidUnicodeInsteadOfWritingReplacementText()
    {
        Assert.Throws<EncoderFallbackException>(() =>
            StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "\ud800"));
    }

    [Fact]
    public void ParsesFormatZeroRunningStatusAndPreservesSourceOrder()
    {
        byte[] source = Hex(
            "4d54686400000006000000010060"
            + "4d54726b0000000e"
            + "00903c64103e6e103c0000ff2f00");

        ParsedStandardMidiFile result = StandardMidiFile.ParseType0Or1(source);

        Assert.Equal((ushort)0, result.Format);
        Assert.Equal(96, result.TicksPerQuarterNote);
        ParsedStandardMidiFileTrack track = Assert.Single(result.Tracks);
        Assert.Equal(32, track.EndTick);
        Assert.Equal(3, track.Events.Count);
        Assert.Equal([0L, 16L, 32L], track.Events.Select(value => value.Tick).ToArray());
        Assert.Equal([0L, 1L, 2L], track.Events.Select(value => value.Order).ToArray());
        Assert.Equal((byte)62, track.Events[1].Message.Byte1);
        Assert.Equal(MidiMessageType.NoteOn, track.Events[2].Message.MessageType);
        Assert.Equal((byte)0, track.Events[2].Message.Byte2);
    }

    [Fact]
    public void MetaEventClearsRunningStatus()
    {
        byte[] source = Hex(
            "4d54686400000006000000010060"
            + "4d54726b00000010"
            + "00903c6400ff030141003e6e00ff2f00");

        MidoraMidiException error = Assert.Throws<MidoraMidiException>(
            () => StandardMidiFile.ParseType0Or1(source));

        Assert.Contains("Running Status", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingScanMatchesParsedTicksAndRunningStatusWithoutWholeFileGraph()
    {
        byte[] source = Hex(
            "4d54686400000006000000010060"
            + "4d54726b00000016"
            + "00ff03045465737400903c64103e6e103c0000ff2f00");
        CollectingStreamVisitor visitor = new(readPayloads: true);

        StandardMidiFileStreamResult result = StandardMidiFile.ScanType0Or1(
            new MemoryStream(source, writable: false),
            visitor);

        Assert.Equal((ushort)0, result.Header.Format);
        Assert.Equal(96, result.Header.TicksPerQuarterNote);
        Assert.Equal(4, result.EventCount);
        Assert.Equal([0L, 0L, 16L, 32L], visitor.Events.Select(value => value.Tick));
        Assert.Equal("Test", Encoding.UTF8.GetString(visitor.Events[0].Data.Span));
        Assert.Equal((byte)62, visitor.Events[2].Message.Byte1);
        Assert.Equal(32, Assert.Single(result.Tracks).EndTick);
    }

    [Fact]
    public void StreamingScanCanSkipOpaquePayloadWithoutAllocatingIt()
    {
        byte[] source = StandardMidiFile.EncodeType1(
            192,
            [new StandardMidiFileTrack(0, [StandardMidiFileEvent.Meta(0, 0x7f, new byte[4096])])]);
        CollectingStreamVisitor visitor = new(readPayloads: false);

        StandardMidiFileStreamResult result = StandardMidiFile.ScanType0Or1(
            new MemoryStream(source, writable: false),
            visitor);

        StreamedStandardMidiFileEvent value = Assert.Single(visitor.Events);
        Assert.False(value.PayloadWasRead);
        Assert.True(value.Data.IsEmpty);
        Assert.Equal(4096, value.DataLength);
        Assert.Equal(4096, result.PayloadByteCount);
    }

    private sealed class CollectingStreamVisitor(bool readPayloads) : IStandardMidiFileStreamVisitor
    {
        public List<StreamedStandardMidiFileEvent> Events { get; } = [];

        public void OnHeader(StandardMidiFileStreamHeader header)
        {
        }

        public void OnTrackStart(int sourceTrackIndex, long chunkByteCount)
        {
        }

        public bool ShouldReadPayload(
            int sourceTrackIndex,
            StandardMidiFileEventKind kind,
            byte type,
            int payloadByteCount) => readPayloads;

        public void OnEvent(in StreamedStandardMidiFileEvent value) => Events.Add(value);

        public void OnTrackEnd(StandardMidiFileStreamTrackResult result)
        {
        }
    }

    private static byte[] Hex(string value) =>
        Convert.FromHexString(value.ToUpper(CultureInfo.InvariantCulture));
}
