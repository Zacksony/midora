using System.Text.Json;
using Midora.Domain;

namespace Midora.Persistence;

internal static partial class ConductorTrackCodecV1
{
    private static readonly HashSet<int> TimeSignatureDenominators = [1, 2, 4, 8, 16, 32, 64];

    public static ConductorTrackJsonV1 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ConductorTrackJsonV1 value = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV1.Default.ConductorTrackJsonV1)
            ?? throw new InvalidDataException("conductor-track.json cannot be null.");
        Validate(value);
        return value;
    }

    public static byte[] Serialize(MidoraProject project)
    {
        using var stream = new MemoryStream();
        Serialize(project, stream);
        return stream.ToArray();
    }

    public static void Restore(MidoraProject project, ConductorTrackJsonV1 value)
    {
        ArgumentNullException.ThrowIfNull(project);
        Validate(value);
        ValidateCompatibility(project.TicksPerQuarterNote, value.TimeSignatures);
        ConductorTrack conductor = project.Conductor;
        conductor.Tempos.Clear();
        conductor.TimeSignatures.Clear();
        conductor.KeySignatures.Clear();
        conductor.Markers.Clear();
        conductor.EndMarker = null;

        foreach (TempoChangeJsonV1 item in value.Tempos)
        {
            conductor.Tempos.Add(new TempoChange(
                ParseId(item.Id, "tempos.id"),
                item.Tick,
                item.BeatsPerMinute));
        }
        foreach (TimeSignatureChangeJsonV1 item in value.TimeSignatures)
        {
            conductor.TimeSignatures.Add(new TimeSignatureChange(
                ParseId(item.Id, "timeSignatures.id"),
                item.Tick,
                item.Numerator,
                item.Denominator));
        }
        foreach (KeySignatureChangeJsonV1 item in value.KeySignatures)
        {
            conductor.KeySignatures.Add(new KeySignatureChange(
                ParseId(item.Id, "keySignatures.id"),
                item.Tick,
                item.SharpsFlats,
                item.IsMinor));
        }
        foreach (ProjectMarkerJsonV1 item in value.Markers)
        {
            conductor.Markers.Add(new ProjectMarker(
                ParseId(item.Id, "markers.id"),
                item.Tick,
                item.Name));
        }
        if (value.EndMarker is not null)
        {
            conductor.EndMarker = new ProjectEndMarker(
                ParseId(value.EndMarker.Id, "endMarker.id"),
                value.EndMarker.Tick);
        }
    }

    public static IEnumerable<MidoraId> EnumerateIds(ConductorTrackJsonV1 value)
    {
        foreach (TempoChangeJsonV1 item in value.Tempos) yield return ParseId(item.Id, "tempos.id");
        foreach (TimeSignatureChangeJsonV1 item in value.TimeSignatures)
            yield return ParseId(item.Id, "timeSignatures.id");
        foreach (KeySignatureChangeJsonV1 item in value.KeySignatures)
            yield return ParseId(item.Id, "keySignatures.id");
        foreach (ProjectMarkerJsonV1 item in value.Markers) yield return ParseId(item.Id, "markers.id");
        if (value.EndMarker is not null) yield return ParseId(value.EndMarker.Id, "endMarker.id");
    }

    private static void Validate(ConductorTrackJsonV1 value)
    {
        if (value.SchemaVersion != PersistenceContractV1.SchemaVersion
            || value.Tempos is null
            || value.TimeSignatures is null
            || value.KeySignatures is null
            || value.Markers is null)
        {
            throw new InvalidDataException("conductor-track.json version or arrays are invalid.");
        }
        HashSet<MidoraId> ids = [];
        HashSet<long> tempoTicks = [];
        foreach (TempoChangeJsonV1 item in value.Tempos)
        {
            RequireItem(item, item?.Tick, "tempo");
            if (item!.BeatsPerMinute <= 0
                || !tempoTicks.Add(item.Tick)
                || !ids.Add(ParseId(item.Id, "tempos.id")))
            {
                throw new InvalidDataException("conductor-track.json contains an invalid or duplicate Tempo.");
            }
        }
        HashSet<long> timeSignatureTicks = [];
        foreach (TimeSignatureChangeJsonV1 item in value.TimeSignatures)
        {
            RequireItem(item, item?.Tick, "time signature");
            if (item!.Numerator is < 1 or > 99
                || !TimeSignatureDenominators.Contains(item.Denominator)
                || !timeSignatureTicks.Add(item.Tick)
                || !ids.Add(ParseId(item.Id, "timeSignatures.id")))
            {
                throw new InvalidDataException("conductor-track.json contains an invalid or duplicate Time Signature.");
            }
        }
        HashSet<long> keySignatureTicks = [];
        foreach (KeySignatureChangeJsonV1 item in value.KeySignatures)
        {
            RequireItem(item, item?.Tick, "key signature");
            if (item!.SharpsFlats is < -7 or > 7
                || !keySignatureTicks.Add(item.Tick)
                || !ids.Add(ParseId(item.Id, "keySignatures.id")))
            {
                throw new InvalidDataException("conductor-track.json contains an invalid or duplicate Key Signature.");
            }
        }
        foreach (ProjectMarkerJsonV1 item in value.Markers)
        {
            RequireItem(item, item?.Tick, "marker");
            PersistenceValueValidationV1.ValidateShortText(item!.Name, "markers.name");
            if (!ids.Add(ParseId(item.Id, "markers.id")))
            {
                throw new InvalidDataException("conductor-track.json contains a duplicate Marker ID.");
            }
        }
        if (value.EndMarker is not null)
        {
            RequireItem(value.EndMarker, value.EndMarker.Tick, "end marker");
            if (!ids.Add(ParseId(value.EndMarker.Id, "endMarker.id")))
            {
                throw new InvalidDataException("conductor-track.json contains a duplicate End Marker ID.");
            }
        }
        if (!tempoTicks.Contains(0) || !timeSignatureTicks.Contains(0))
        {
            throw new InvalidDataException("conductor-track.json must contain Tempo and Time Signature at tick 0.");
        }
    }

    private static void RequireItem(object? value, long? tick, string name)
    {
        if (value is null || tick is null or < 0)
        {
            throw new InvalidDataException($"conductor-track.json contains an invalid {name} item.");
        }
    }

    private static void ValidateCompatibility(
        int ticksPerQuarterNote,
        IEnumerable<TimeSignatureChange> values)
    {
        foreach (TimeSignatureChange value in values)
        {
            if (!ProjectTimeSignatureRules.IsCompatible(ticksPerQuarterNote, value.Denominator))
            {
                throw new InvalidDataException(
                    $"Time Signature {value.Id} at tick {value.Tick} uses denominator "
                    + $"{value.Denominator}, which is incompatible with TPQ {ticksPerQuarterNote}.");
            }
        }
    }

    private static void ValidateCompatibility(
        int ticksPerQuarterNote,
        IEnumerable<TimeSignatureChangeJsonV1> values)
    {
        foreach (TimeSignatureChangeJsonV1 value in values)
        {
            if (!ProjectTimeSignatureRules.IsCompatible(ticksPerQuarterNote, value.Denominator))
            {
                throw new InvalidDataException(
                    $"Time Signature {value.Id.Value} at tick {value.Tick} uses denominator "
                    + $"{value.Denominator}, which is incompatible with TPQ {ticksPerQuarterNote}.");
            }
        }
    }

    private static MidoraId ParseId(StableIdJsonV1? value, string fieldName)
    {
        if (value is not StableIdJsonV1 id || id.Value <= 0)
        {
            throw new InvalidDataException($"conductor-track.json {fieldName} is not canonical.");
        }
        return id.ToDomain();
    }
}
