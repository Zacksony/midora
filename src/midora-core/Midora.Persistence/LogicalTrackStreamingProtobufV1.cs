using Google.Protobuf.Reflection;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence;

internal static partial class LogicalTrackProtobufCodecV1
{
    public static void Serialize(LogicalTrack value, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        new StreamingProtobufWriteContext(cancellationToken).Serialize(destination, writer => Write(writer, value));
    }

    private static void Write(StreamingProtobufWriter writer, LogicalTrack value)
    {
        LogicalTrackV1 header = ToWire(value, includeChildren: false);
        Validate(header);
        writer.Wire(header);
        foreach (Segment segment in value.Segments)
            writer.Message(8, segment, nested => Write(nested, segment));
    }

    private static void Write(StreamingProtobufWriter writer, Segment value)
    {
        SegmentV1 header = ToWire(value, includeChildren: false);
        Validate(header);
        writer.Wire(header);
        LogicalNoteQuerySnapshot notes = writer.Snapshot(value.Notes, value.Notes.CreateQuerySnapshot);
        int ordinal = 0;
        foreach (LogicalNoteSnapshotValue note in notes.EnumerateAll())
        {
            if ((ordinal++ & 255) == 0) writer.Checkpoint();
            writer.Note(5, note);
        }
        foreach (LogicalParameterLane lane in value.ParameterLanes)
            writer.Message(6, lane, nested => Write(nested, lane));
    }

    private static void Write(StreamingProtobufWriter writer, LogicalParameterLane value)
    {
        writer.Wire(ToWire(value, includeChildren: false));
        CurvePointQuerySnapshot points = writer.Snapshot(value.Points, value.Points.CreateQuerySnapshot);
        int ordinal = 0;
        foreach (CurvePointSnapshotValue point in points.EnumerateAll())
        {
            if ((ordinal++ & 255) == 0) writer.Checkpoint();
            writer.Point(3, point);
        }
    }

    public static LogicalTrack Restore(MidoraProject project, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        StreamingProtobufReader reader = new(source, cancellationToken);
        StreamingProtobufReader.Frame frame = reader.Root(LogicalTrackV1.Descriptor);
        LogicalTrackV1 header = new();
        List<Segment> segments = [];
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 8) segments.Add(ReadSegment(project, reader, reader.Child(frame, field)));
            else reader.Scalar(header, frame, field);
        }
        Validate(header);
        reader.ThrowSemanticFailure();
        LogicalTrack result = FromWire(project, header);
        foreach (Segment segment in segments) result.Segments.Add(segment);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static Segment ReadSegment(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        SegmentV1 header = new();
        ProtobufValuePages<LogicalNoteSnapshotValue> notes = new();
        List<LogicalParameterLane> lanes = [];
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            switch (field.FieldNumber)
            {
                case 5: notes.Add(reader.Note(reader.Child(frame, field))); break;
                case 6: lanes.Add(ReadLane(project, reader, reader.Child(frame, field))); break;
                default: reader.Scalar(header, frame, field); break;
            }
        }
        return reader.Materialize(() =>
        {
            Validate(header);
            Segment result = FromWire(project, header);
            result.Notes.AdoptSource(project, notes.Seal(), reader.Token);
            foreach (LogicalParameterLane lane in lanes) result.ParameterLanes.Add(lane);
            return result;
        });
    }

    private static LogicalParameterLane ReadLane(MidoraProject project, StreamingProtobufReader reader, StreamingProtobufReader.Frame frame)
    {
        LogicalParameterLaneV1 header = new();
        ProtobufValuePages<CurvePointSnapshotValue> points = new();
        FieldDescriptor? field;
        while ((field = reader.Field(ref frame)) is not null)
        {
            if (field.FieldNumber == 3) points.Add(reader.Point(reader.Child(frame, field)));
            else reader.Scalar(header, frame, field);
        }
        return reader.Materialize(() =>
        {
            LogicalParameterLane result = FromWire(project, header);
            result.Points.AdoptSource(project, points.Seal(), reader.Token);
            return result;
        });
    }
}
