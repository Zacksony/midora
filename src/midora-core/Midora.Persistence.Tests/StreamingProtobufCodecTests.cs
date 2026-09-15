using System.Reflection;
using Google.Protobuf;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;
using Midora.Persistence.Wire.Proto.V2;

namespace Midora.Persistence.Tests;

public sealed class StreamingProtobufCodecTests
{
    [Fact]
    public void PointPagesAndRepeatedMetadataMatchTheFrozenGeneratedOracle()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        SubVoice voice = instrument.SubVoices[0];
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Enum", Type = LogicalParameterType.Enum, UsesExplicitEnumValues = true
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        ValueCurve curve = new(project) { Target = MidiValueTarget.ControlChange(11) };
        track.Segments.Add(segment); segment.ParameterLanes.Add(lane); voice.Curves.Add(curve);
        for (int i = 0; i < 4097; i++)
        {
            lane.Points.Add(new(project, i * 2L, i == 0 ? -0.0 : i / 4097.0, CurveInterpolation.Step));
            curve.Points.Add(new(project, (4097 - i) * 2L, i / 4097.0, CurveInterpolation.Linear));
        }
        for (int i = 127; i >= 0; i--)
        {
            parameter.EnumItems.Add(new(project) { Name = "Entry " + i, Value = i });
            voice.InitialState.Controllers[i] = i;
            instrument.InitialState.RegisteredParameters[i] = 127 - i;
            voice.EventMappings[0].Steps.Add(new(project) { Constant = i / 128.0 });
        }
        CSharpMappingFunction function = new(project) { Name = "Function", Body = "0" };
        function.DeclaredContextFields.Add("triggerNote"); function.DeclaredContextFields.Add("gateLength");
        instrument.MappingFunctions.Add(function);
        byte[] expectedInstrument = FrozenGeneratedBytes(instrument, typeof(EventInstrumentProtobufCodecV1));
        byte[] expectedTrack = FrozenGeneratedBytes(track, typeof(LogicalTrackProtobufCodecV1));
        Assert.Equal(expectedInstrument, EventInstrumentProtobufCodecV1.Serialize(instrument));
        Assert.Equal(expectedTrack, LogicalTrackProtobufCodecV1.Serialize(track));
        using MidoraProject restoredProject = new(480);
        EventInstrument restoredInstrument = EventInstrumentProtobufCodecV1.Restore(restoredProject, expectedInstrument);
        LogicalTrack restoredTrack = LogicalTrackProtobufCodecV1.Restore(restoredProject, expectedTrack);
        Assert.Equal(expectedInstrument, EventInstrumentProtobufCodecV1.Serialize(restoredInstrument));
        Assert.Equal(expectedTrack, LogicalTrackProtobufCodecV1.Serialize(restoredTrack));
        Assert.Equal(4097, restoredInstrument.SubVoices[0].Curves[0].Points.Count);
        Assert.Equal(4097, restoredTrack.Segments[0].ParameterLanes[0].Points.Count);
        Assert.Equal(128, restoredInstrument.SubVoices[0].EventMappings[0].Steps.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4097)]
    public void StreamOutputMatchesFrozenGeneratedWriterAndRestoresValuePages(int count)
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Streaming\U0001F3B5");
        instrument.Description = "Line 1\r\nLine 2\t\u00E9";
        instrument.TemplateLengthTicks = Math.Max(480, count * 2L + 480);
        instrument.PreRollTicks = 20;
        SubVoice voice = instrument.SubVoices[0];
        LogicalTrack track = new(project) { Name = "Logical" };
        Segment segment = new(project) { LengthTicks = instrument.TemplateLengthTicks };
        track.Segments.Add(segment);
        for (int i = 0; i < count; i++)
        {
            // Deliberately non-tick order: persistence preserves formal insertion order.
            long tick = (count - i) * 2L;
            segment.Notes.Add(new LogicalNote(project) { StartTick = tick, LengthTicks = 1, Note = i % 128, Velocity = 100 });
            voice.Events.Add(TemplateEvent.Note(project, tick, 1, i % 128, 100));
        }
        byte[] expectedTrack = FrozenGeneratedBytes(track, typeof(LogicalTrackProtobufCodecV1));
        byte[] expectedDefinition = FrozenGeneratedBytes(instrument, typeof(EventInstrumentProtobufCodecV1));
        byte[] expectedV2 = StrictProtobufWireV1.SerializeDeterministic(new EventInstrumentV2
        {
            SchemaVersion = 2, ObjectType = "event-instrument", PreRollTicks = instrument.PreRollTicks,
            Definition = EventInstrumentV1.Parser.ParseFrom(expectedDefinition)
        });
        using MemoryStream logicalBytes = new();
        using MemoryStream instrumentBytes = new();
        using MemoryStream v2Bytes = new();
        LogicalTrackProtobufCodecV1.Serialize(track, logicalBytes);
        EventInstrumentProtobufCodecV1.Serialize(instrument, instrumentBytes);
        EventInstrumentProtobufCodecV2.Serialize(instrument, v2Bytes);
        Assert.Equal(expectedTrack, logicalBytes.ToArray());
        Assert.Equal(expectedDefinition, instrumentBytes.ToArray());
        Assert.Equal(expectedV2, v2Bytes.ToArray());
        using MidoraProject restoredProject = new(480);
        using ShortReadStream logicalInput = new(expectedTrack, 7);
        using ShortReadStream instrumentInput = new(expectedV2, 3);
        LogicalTrack restoredTrack = LogicalTrackProtobufCodecV1.Restore(restoredProject, logicalInput);
        EventInstrument restored = EventInstrumentProtobufCodecV2.Restore(restoredProject, instrumentInput);
        Assert.Equal(count, restoredTrack.Segments[0].Notes.Count);
        Assert.Equal(count, restored.SubVoices[0].Events.Count);
        Assert.Equal(instrument.PreRollTicks, restored.PreRollTicks);
        Assert.Equal(segment.Notes.CreateQuerySnapshot().EnumerateAll(),
            restoredTrack.Segments[0].Notes.CreateQuerySnapshot().EnumerateAll());
        Assert.Equal(voice.Events.CreateQuerySnapshot().EnumerateAll(),
            restored.SubVoices[0].Events.CreateQuerySnapshot().EnumerateAll());
        Assert.Equal(expectedTrack, LogicalTrackProtobufCodecV1.Serialize(restoredTrack));
        Assert.Equal(expectedV2, EventInstrumentProtobufCodecV2.Serialize(restored));
        Assert.Empty(restoredProject.Tracks);
        Assert.Empty(restoredProject.EventInstruments);
        Assert.True(logicalInput.CanRead);
        Assert.True(instrumentInput.CanRead);
    }

    [Fact]
    public void LogicalScalarPresenceUnknownDuplicateWrongWireAndTruncationRemainStrict()
    {
        LogicalTrackV1 wire = TrackWire();
        byte[] bytes = wire.ToByteArray();
        foreach (byte[] corrupt in new byte[][]
        {
            [.. bytes, 0x08, 0x01], // duplicate schema
            [.. bytes, 0x48, 0x00], // unknown field 9
            [.. bytes, 0x09, 0, 0, 0, 0, 0, 0, 0, 0], // wrong wire type
            bytes[..^1],
            [.. bytes, 0x80],
            [.. bytes, 0x00]
        })
        {
            using MidoraProject project = new(480);
            using ShortReadStream input = new(corrupt, 1);
            Assert.Throws<InvalidDataException>(() => LogicalTrackProtobufCodecV1.Restore(project, input));
            Assert.Empty(project.Tracks);
        }
        wire.Segments[0].Notes[0].ClearVelocity();
        using MidoraProject missingProject = new(480);
        Assert.Throws<InvalidDataException>(() => LogicalTrackProtobufCodecV1.Restore(missingProject, wire.ToByteArray()));
    }

    [Fact]
    public void LateHeaderMismatchOutranksNestedSemanticFailureButNotMalformedWire()
    {
        LogicalTrackV1 wire = TrackWire();
        wire.Segments[0].Notes[0].Id = 0;
        wire.ClearSchemaVersion(); wire.ClearObjectType();
        byte[] nestedFirst = wire.ToByteArray();
        byte[] headerLast = new LogicalTrackV1 { SchemaVersion = 1, ObjectType = "wrong-type" }.ToByteArray();
        using MidoraProject project = new(480);
        Assert.Throws<ProtobufObjectHeaderExceptionV1>(() => LogicalTrackProtobufCodecV1.Restore(project, [.. nestedFirst, .. headerLast]));
        Assert.Throws<InvalidDataException>(() => LogicalTrackProtobufCodecV1.Restore(project, [.. nestedFirst, .. headerLast, 0x80]));
    }

    [Fact]
    public void V2LateWrapperMismatchOutranksNestedV1HeaderAndSemanticErrors()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        EventInstrumentV2 wire = EventInstrumentV2.Parser.ParseFrom(EventInstrumentProtobufCodecV2.Serialize(instrument));
        wire.Definition.ObjectType = "bad-inner";
        wire.Definition.SubVoices[0].Id = 0;
        wire.ClearObjectType(); wire.ClearSchemaVersion();
        byte[] lateHeader = new EventInstrumentV2 { SchemaVersion = 2, ObjectType = "bad-outer" }.ToByteArray();
        ProtobufObjectHeaderExceptionV1 error = Assert.Throws<ProtobufObjectHeaderExceptionV1>(() =>
            EventInstrumentProtobufCodecV2.Restore(project, [.. wire.ToByteArray(), .. lateHeader]));
        Assert.Contains("v2", error.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(481)]
    public void V2WrapperRangeValidationStillPrecedesNestedHeaderErrors(long preRoll)
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        EventInstrumentV2 wire = EventInstrumentV2.Parser.ParseFrom(EventInstrumentProtobufCodecV2.Serialize(instrument));
        wire.Definition.TemplateLengthTicks = 480;
        wire.Definition.ObjectType = "bad-inner";
        wire.PreRollTicks = preRoll;
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(project, wire.ToByteArray()));
    }

    [Fact]
    public void OversizedNestedTextIsDrainedWithoutHidingLateWrapperHeaderConflict()
    {
        using MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        EventInstrumentV2 wire = EventInstrumentV2.Parser.ParseFrom(EventInstrumentProtobufCodecV2.Serialize(instrument));
        wire.Definition.Description = new string('a', 300_000);
        wire.ClearObjectType();
        byte[] ending = new EventInstrumentV2 { ObjectType = "bad-outer" }.ToByteArray();
        Assert.Throws<ProtobufObjectHeaderExceptionV1>(() =>
            EventInstrumentProtobufCodecV2.Restore(project, [.. wire.ToByteArray(), .. ending]));
        wire.ObjectType = "event-instrument";
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(project, wire.ToByteArray()));
    }

    [Fact]
    public void ReaderPreservesLegalNoncanonicalFieldOrderAndRejectsNestedDuplicates()
    {
        LogicalTrackV1 wire = TrackWire();
        byte[] head = new LogicalTrackV1 { SchemaVersion = 1, ObjectType = "logical-track" }.ToByteArray();
        wire.ClearSchemaVersion(); wire.ClearObjectType();
        using MidoraProject project = new(480);
        LogicalTrack restored = LogicalTrackProtobufCodecV1.Restore(project, [.. wire.ToByteArray(), .. head]);
        Assert.Equal(TrackWire().ToByteArray(), LogicalTrackProtobufCodecV1.Serialize(restored));
        using MemoryStream corrupt = new();
        using (CodedOutputStream output = new(corrupt, leaveOpen: true))
        {
            wire.Segments.Clear(); wire.WriteTo(output);
            output.WriteTag(8, WireFormat.WireType.LengthDelimited);
            byte[] segment = TrackWire().Segments[0].ToByteArray();
            output.WriteLength(segment.Length + 2);
            foreach (byte value in segment) output.WriteRawTag(value);
            output.WriteRawTag(8, 2); // duplicate segment ID
        }
        Assert.Throws<InvalidDataException>(() => LogicalTrackProtobufCodecV1.Restore(project, corrupt.ToArray()));
    }

    [Fact]
    public void InvalidUtf8UnknownEnumOverflowAndNonFiniteValuesAreRejected()
    {
        using MidoraProject project = new(480);
        LogicalTrackV1 wire = TrackWire();
        wire.ClearName();
        Assert.Throws<InvalidDataException>(() => LogicalTrackProtobufCodecV1.Restore(project,
            [.. wire.ToByteArray(), 0x22, 0x02, 0xc3, 0x28]));
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        EventInstrumentV1 definition = EventInstrumentV1.Parser.ParseFrom(EventInstrumentProtobufCodecV1.Serialize(instrument));
        definition.OverlapPolicy = (EventInstrumentOverlapPolicyV1)999;
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV1.Restore(project, definition.ToByteArray()));
        using MemoryStream point = new(new CurvePointV1
        {
            Id = 1, Tick = 0, Value = double.NaN, Interpolation = (CurveInterpolationV1)0
        }.ToByteArray());
        StreamingProtobufReader pointReader = new(point, default);
        Assert.Throws<InvalidDataException>(() => pointReader.Point(pointReader.Root(CurvePointV1.Descriptor)));
        using MemoryStream overflow = new([0x20, 0x80, 0x80, 0x80, 0x80, 0x08]);
        StreamingProtobufReader overflowReader = new(overflow, default);
        Assert.Throws<InvalidDataException>(() => overflowReader.Note(overflowReader.Root(LogicalNoteV1.Descriptor)));
    }

    [Fact]
    public void CancellationLeavesCallerStreamsOpenAndProjectUnpublished()
    {
        using MidoraProject project = new(480);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MemoryStream destination = new();
        Assert.Throws<OperationCanceledException>(() => LogicalTrackProtobufCodecV1.Serialize(new LogicalTrack(project) { Name = "Track" }, destination, cancellation.Token));
        Assert.Equal(0, destination.Length);
        using ShortReadStream input = new(TrackWire().ToByteArray(), 1);
        Assert.Throws<OperationCanceledException>(() => LogicalTrackProtobufCodecV1.Restore(project, input, cancellation.Token));
        Assert.True(input.CanRead);
        Assert.True(destination.CanWrite);
        Assert.Empty(project.Tracks);
    }

    private static LogicalTrackV1 TrackWire()
    {
        LogicalTrackV1 track = new() { SchemaVersion = 1, ObjectType = "logical-track", Id = 1, Name = "Track" };
        SegmentV1 segment = new() { Id = 2, ProjectStartTick = 0, LengthTicks = 480, ContentOffsetTick = 0 };
        segment.Notes.Add(new LogicalNoteV1 { Id = 3, StartTick = 0, LengthTicks = 120, Note = 60, Velocity = 100 });
        track.Segments.Add(segment);
        return track;
    }

    private static byte[] FrozenGeneratedBytes(object value, Type codec)
    {
        MethodInfo method = codec.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name == "ToWire" && candidate.GetParameters()[0].ParameterType == value.GetType());
        IMessage wire = (IMessage)method.Invoke(null, [value, true])!;
        return StrictProtobufWireV1.SerializeDeterministic(wire);
    }

    private sealed class ShortReadStream(byte[] bytes, int block) : Stream
    {
        private readonly MemoryStream _source = new(bytes, writable: false);
        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, Math.Min(count, block));
        public override int Read(Span<byte> buffer) => _source.Read(buffer[..Math.Min(buffer.Length, block)]);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _source.Dispose(); base.Dispose(disposing); }
    }
}
