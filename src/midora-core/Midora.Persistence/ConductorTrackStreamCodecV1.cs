using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Midora.Domain;

namespace Midora.Persistence;

internal sealed class ConductorCodecMetricsV1
{
    public long Records { get; internal set; }
    public int PeakInputBufferBytes { get; internal set; }
    public int PeakRecordBufferBytes { get; internal set; }
    public long PeakIdValidationBytes { get; internal set; }
}

internal static partial class ConductorTrackCodecV1
{
    public static void Serialize(MidoraProject project, Stream destination,
        CancellationToken cancellationToken = default) => SerializeCore(project, destination, cancellationToken, null);

    internal static void Serialize(MidoraProject project, Stream destination, ConductorCodecMetricsV1 metrics,
        CancellationToken cancellationToken = default) => SerializeCore(project, destination, cancellationToken, metrics);

    private static void SerializeCore(MidoraProject project, Stream destination,
        CancellationToken cancellationToken, ConductorCodecMetricsV1? metrics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The JSON stream must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        ConductorTrack frozen = project.Conductor.CloneFrozen();
        ValidateFrozen(project.TicksPerQuarterNote, frozen, cancellationToken, metrics);
        using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions { Indented = true, NewLine = "\n" });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion"u8, PersistenceContractV1.SchemaVersion);
        // Frozen V1 schema, not reflection over the Domain model. The original
        // source-generated DTO writer remains the byte-for-byte test oracle.
        // Serializing each separate DTO also flushes the writer once per record.
        WriteArray("tempos", frozen.Tempos, static (output, item) =>
        {
            output.WriteStartObject();
            output.WriteNumber("id"u8, item.Id.Value);
            output.WriteNumber("tick"u8, item.Tick);
            output.WriteNumber("beatsPerMinute"u8, item.BeatsPerMinute);
            output.WriteEndObject();
        });
        WriteArray("timeSignatures", frozen.TimeSignatures, static (output, item) =>
        {
            output.WriteStartObject();
            output.WriteNumber("id"u8, item.Id.Value);
            output.WriteNumber("tick"u8, item.Tick);
            output.WriteNumber("numerator"u8, item.Numerator);
            output.WriteNumber("denominator"u8, item.Denominator);
            output.WriteEndObject();
        });
        WriteArray("keySignatures", frozen.KeySignatures, static (output, item) =>
        {
            output.WriteStartObject();
            output.WriteNumber("id"u8, item.Id.Value);
            output.WriteNumber("tick"u8, item.Tick);
            output.WriteNumber("sharpsFlats"u8, item.SharpsFlats);
            output.WriteBoolean("isMinor"u8, item.IsMinor);
            output.WriteEndObject();
        });
        WriteArray("markers", frozen.Markers, static (output, item) =>
        {
            output.WriteStartObject();
            output.WriteNumber("id"u8, item.Id.Value);
            output.WriteNumber("tick"u8, item.Tick);
            output.WriteString("name"u8, item.Name);
            output.WriteEndObject();
        });
        if (frozen.EndMarker is { } marker)
        {
            writer.WriteStartObject("endMarker"u8);
            writer.WriteNumber("id"u8, marker.Id.Value);
            writer.WriteNumber("tick"u8, marker.Tick);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
        cancellationToken.ThrowIfCancellationRequested();
        writer.Flush();
        destination.WriteByte((byte)'\n');

        void WriteArray<T>(string name, IEnumerable<T> items, Action<Utf8JsonWriter, T> write)
        {
            writer.WriteStartArray(name);
            int count = 0;
            foreach (T item in items)
            {
                if ((count++ & 255) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (writer.BytesPending >= 65_536) writer.Flush();
                }
                write(writer, item);
            }
            writer.WriteEndArray();
        }
    }

    public static void Restore(MidoraProject project, Stream source,
        CancellationToken cancellationToken = default) => RestoreCore(project, source, cancellationToken, null);

    internal static void Restore(MidoraProject project, Stream source, ConductorCodecMetricsV1 metrics,
        CancellationToken cancellationToken = default) => RestoreCore(project, source, cancellationToken, metrics);

    private static void RestoreCore(MidoraProject project, Stream source,
        CancellationToken cancellationToken, ConductorCodecMetricsV1? metrics)
    {
        ArgumentNullException.ThrowIfNull(project);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new ConductorJsonStreamReaderV1(source, cancellationToken);
        var context = MidoraJsonSerializerContextV1.Default;
        var candidate = new ConductorTrack(project, createInitialState: false);
        RequireToken(reader.ReadToken(), JsonTokenType.StartObject);
        int seen = 0;
        while (true)
        {
            JsonTokenType token = reader.ReadToken(out string? name, out _);
            if (token == JsonTokenType.EndObject) break;
            RequireToken(token, JsonTokenType.PropertyName);
            int field = name switch { "schemaVersion" => 0, "tempos" => 1, "timeSignatures" => 2, "keySignatures" => 3, "markers" => 4, "endMarker" => 5, _ => -1 };
            if (field < 0) throw new JsonException($"Unknown Conductor property '{name}'.");
            int bit = 1 << field;
            if ((seen & bit) != 0) throw new InvalidDataException($"Duplicate JSON property '{name}' at $.");
            seen |= bit;
            switch (field)
            {
                case 0:
                    RequireToken(reader.ReadToken(out _, out int version), JsonTokenType.Number);
                    if (version != PersistenceContractV1.SchemaVersion)
                        throw new InvalidDataException("conductor-track.json version or arrays are invalid.");
                    break;
                case 1:
                    candidate.Tempos.AdoptSource(ReadArray(context.TempoChangeJsonV1,
                        static item =>
                        {
                            RequireItem(item, item.Tick, "tempo");
                            if (item.BeatsPerMinute <= 0) throw new InvalidDataException("conductor-track.json contains an invalid Tempo.");
                            return new TempoChange(ParseId(item.Id, "tempos.id"), item.Tick, item.BeatsPerMinute);
                        }), cancellationToken);
                    break;
                case 2:
                    candidate.TimeSignatures.AdoptSource(ReadArray(context.TimeSignatureChangeJsonV1,
                        static item =>
                        {
                            RequireItem(item, item.Tick, "time signature");
                            if (item.Numerator is < 1 or > 99 || !TimeSignatureDenominators.Contains(item.Denominator))
                                throw new InvalidDataException("conductor-track.json contains an invalid Time Signature.");
                            return new TimeSignatureChange(ParseId(item.Id, "timeSignatures.id"), item.Tick, item.Numerator, item.Denominator);
                        }), cancellationToken);
                    break;
                case 3:
                    candidate.KeySignatures.AdoptSource(ReadArray(context.KeySignatureChangeJsonV1,
                        static item =>
                        {
                            RequireItem(item, item.Tick, "key signature");
                            if (item.SharpsFlats is < -7 or > 7) throw new InvalidDataException("conductor-track.json contains an invalid Key Signature.");
                            return new KeySignatureChange(ParseId(item.Id, "keySignatures.id"), item.Tick, item.SharpsFlats, item.IsMinor);
                        }), cancellationToken);
                    break;
                case 4:
                    candidate.Markers.AdoptSource(ReadArray(context.ProjectMarkerJsonV1,
                        static item =>
                        {
                            RequireItem(item, item.Tick, "marker");
                            if (item.Name is null) throw new InvalidDataException("conductor-track.json markers.name cannot be null.");
                            PersistenceValueValidationV1.ValidateShortText(item.Name, "markers.name");
                            return new ProjectMarker(ParseId(item.Id, "markers.id"), item.Tick, item.Name);
                        }), cancellationToken);
                    break;
                case 5:
                    ProjectEndMarkerJsonV1? marker = reader.ReadRecord(context.ProjectEndMarkerJsonV1, allowNull: true, out bool endArray);
                    if (endArray) throw new JsonException("Unexpected end of array in Conductor.");
                    if (marker is not null) candidate.EndMarker = new ProjectEndMarker(ParseId(marker.Id, "endMarker.id"), marker.Tick);
                    break;
            }
        }
        if ((seen & 31) != 31) throw new JsonException("Conductor JSON is missing one or more required properties.");
        RequireToken(reader.ReadToken(), JsonTokenType.None);
        ValidateFrozen(project.TicksPerQuarterNote, candidate, cancellationToken, metrics);
        cancellationToken.ThrowIfCancellationRequested();
        if (metrics is not null)
        {
            metrics.PeakInputBufferBytes = reader.PeakBufferBytes;
            metrics.PeakRecordBufferBytes = reader.PeakRecordBufferBytes;
        }
        // No caller-visible mutation before the entire file and all records passed validation.
        project.Conductor = candidate;

        ConductorReadPagesV1<T> ReadArray<TDto, T>(JsonTypeInfo<TDto> info, Func<TDto, T> convert)
            where TDto : class where T : class
        {
            RequireToken(reader.ReadToken(), JsonTokenType.StartArray);
            var values = new ConductorReadPagesV1<T>();
            while (true)
            {
                TDto? item = reader.ReadRecord(info, allowNull: false, out bool endArray);
                if (endArray) break;
                values.Add(convert(item!));
            }
            return values.Finish(cancellationToken);
        }
    }

    private static void RequireToken(JsonTokenType actual, JsonTokenType expected)
    {
        if (actual != expected) throw new JsonException($"Expected {expected} in conductor-track.json, but found {actual}.");
    }

    private static void ValidateFrozen(int ticksPerQuarterNote, ConductorTrack value,
        CancellationToken cancellationToken, ConductorCodecMetricsV1? metrics)
    {
        using var ids = new StableIdValidatorV1(cancellationToken);
        bool maximumIdSeen = false;
        long count = 0;
        bool initialTempo = false, initialSignature = false;
        long previousTick = -1;
        foreach (TempoChange item in value.Tempos)
        {
            Check(item.Id, item.Tick);
            if (item.BeatsPerMinute <= 0 || item.Tick == previousTick)
                throw new InvalidDataException("conductor-track.json contains an invalid or duplicate Tempo.");
            initialTempo |= item.Tick == 0;
            previousTick = item.Tick;
        }
        previousTick = -1;
        foreach (TimeSignatureChange item in value.TimeSignatures)
        {
            Check(item.Id, item.Tick);
            if (item.Numerator is < 1 or > 99 || !TimeSignatureDenominators.Contains(item.Denominator) || item.Tick == previousTick)
                throw new InvalidDataException("conductor-track.json contains an invalid or duplicate Time Signature.");
            if (!ProjectTimeSignatureRules.IsCompatible(ticksPerQuarterNote, item.Denominator))
                throw new InvalidDataException($"Time Signature {item.Id} at tick {item.Tick} uses denominator {item.Denominator}, which is incompatible with TPQ {ticksPerQuarterNote}.");
            initialSignature |= item.Tick == 0;
            previousTick = item.Tick;
        }
        previousTick = -1;
        foreach (KeySignatureChange item in value.KeySignatures)
        {
            Check(item.Id, item.Tick);
            if (item.SharpsFlats is < -7 or > 7 || item.Tick == previousTick)
                throw new InvalidDataException("conductor-track.json contains an invalid or duplicate Key Signature.");
            previousTick = item.Tick;
        }
        foreach (ProjectMarker item in value.Markers)
        {
            Check(item.Id, item.Tick);
            PersistenceValueValidationV1.ValidateShortText(item.Name, "markers.name");
        }
        if (value.EndMarker is { } endMarker) Check(endMarker.Id, endMarker.Tick);
        if (!initialTempo || !initialSignature)
            throw new InvalidDataException("conductor-track.json must contain Tempo and Time Signature at tick 0.");
        ids.Complete();
        if (metrics is not null)
        {
            metrics.Records = count;
            metrics.PeakIdValidationBytes = ids.PeakResidentBytes;
        }

        void Check(MidoraId id, long tick)
        {
            if ((count++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (tick < 0) throw new InvalidDataException("conductor-track.json contains an invalid event item.");
            // Codec validation does not know project.nextStableId; package validation owns that check.
            if (id.Value == long.MaxValue)
            {
                if (maximumIdSeen) throw new InvalidDataException("conductor-track.json contains a duplicate event ID.");
                maximumIdSeen = true;
            }
            else ids.Add(id, long.MaxValue, "Conductor event");
        }
    }
}
