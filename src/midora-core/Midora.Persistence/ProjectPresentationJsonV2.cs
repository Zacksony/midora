using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Domain;

namespace Midora.Persistence;

// Independent presentation schema 2, still inside a Project Format 3 package.
// The schema 1 DTOs remain frozen in ProjectPresentationV3.cs.
internal static class ProjectPresentationCodecV2
{
    public static string FormatMode(OnionSourceMode mode) => mode switch
    {
        OnionSourceMode.Custom => "custom",
        OnionSourceMode.Previous => "previous",
        OnionSourceMode.Next => "next",
        _ => throw new InvalidDataException("Invalid onion source mode.")
    };

    private static OnionSourceMode ParseMode(string mode) => mode switch
    {
        "custom" => OnionSourceMode.Custom,
        "previous" => OnionSourceMode.Previous,
        "next" => OnionSourceMode.Next,
        _ => throw new InvalidDataException("Invalid onion source mode.")
    };

    public static ProjectPresentationStateV3 Read(ReadOnlySpan<byte> utf8, MidoraProject project)
    {
        ProjectPresentationJsonV2 dto = JsonSerializer.Deserialize(utf8,
            ProjectPresentationJsonContextV2.Default.ProjectPresentationJsonV2)
            ?? throw new InvalidDataException("project-presentation.json cannot be null.");
        if (dto.SchemaVersion != 2 || dto.TrackOnionPresets is null || dto.SubVoiceOnionPresets is null)
            throw new InvalidDataException("Invalid presentation schema version or missing preset arrays.");
        ProjectPresentationStateV3 state = new(dto.AllTracksMode switch
        {
            "raw" => ProjectPresentationAllTracksModeV3.Raw,
            "compiled" => ProjectPresentationAllTracksModeV3.Compiled,
            _ => throw new InvalidDataException("Invalid all-tracks presentation mode.")
        }, dto.TrackOnionPresets.Select(value => value is null
            ? throw new InvalidDataException("A Track onion preset cannot be null.")
            : new TrackOnionPresetV3(value.TargetTrackId.ToDomain(), value.Enabled, value.Opacity,
                (value.SourceTrackIds ?? throw new InvalidDataException("Track onion sources are required."))
                    .Select(id => id.ToDomain()).ToArray(), ParseMode(value.SourceMode))).ToArray(),
            dto.SubVoiceOnionPresets.Select(value => value is null
                ? throw new InvalidDataException("A SubVoice onion preset cannot be null.")
                : new SubVoiceOnionPresetV3(value.EventInstrumentId.ToDomain(), value.TargetSubVoiceId.ToDomain(),
                    value.Enabled, value.Opacity,
                    (value.SourceSubVoiceIds ?? throw new InvalidDataException("SubVoice onion sources are required."))
                        .Select(id => id.ToDomain()).ToArray(), ParseMode(value.SourceMode))).ToArray());
        return ProjectPresentationCodecV3.ValidateAndCanonicalize(state, project);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectPresentationJsonV2
{
    [JsonPropertyOrder(0)] public required int SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public required string AllTracksMode { get; init; }
    [JsonPropertyOrder(2)] public required TrackOnionPresetJsonV2[] TrackOnionPresets { get; init; }
    [JsonPropertyOrder(3)] public required SubVoiceOnionPresetJsonV2[] SubVoiceOnionPresets { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TrackOnionPresetJsonV2
{
    [JsonPropertyOrder(0)] public required StableIdJsonV1 TargetTrackId { get; init; }
    [JsonPropertyOrder(1)] public required bool Enabled { get; init; }
    [JsonPropertyOrder(2)] public required double Opacity { get; init; }
    [JsonPropertyOrder(3)] public required StableIdJsonV1[] SourceTrackIds { get; init; }
    [JsonPropertyOrder(4)] public required string SourceMode { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SubVoiceOnionPresetJsonV2
{
    [JsonPropertyOrder(0)] public required StableIdJsonV1 EventInstrumentId { get; init; }
    [JsonPropertyOrder(1)] public required StableIdJsonV1 TargetSubVoiceId { get; init; }
    [JsonPropertyOrder(2)] public required bool Enabled { get; init; }
    [JsonPropertyOrder(3)] public required double Opacity { get; init; }
    [JsonPropertyOrder(4)] public required StableIdJsonV1[] SourceSubVoiceIds { get; init; }
    [JsonPropertyOrder(5)] public required string SourceMode { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectPresentationJsonV2))]
internal sealed partial class ProjectPresentationJsonContextV2 : JsonSerializerContext;
