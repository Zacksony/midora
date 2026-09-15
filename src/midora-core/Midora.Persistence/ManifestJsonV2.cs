using System.Text.Json.Serialization;

namespace Midora.Persistence;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ManifestJsonV2
{
    [JsonPropertyOrder(0)]
    public required string Magic { get; init; }

    [JsonPropertyOrder(1)]
    public required int FileFormatVersion { get; init; }

    [JsonPropertyOrder(2)]
    public required int MinimumReadableVersion { get; init; }

    [JsonPropertyOrder(3)]
    public required int ManifestSchemaVersion { get; init; }

    [JsonPropertyOrder(4)]
    public required string CreatedWithSoftwareVersion { get; init; }

    [JsonPropertyOrder(5)]
    public required string LastSavedWithSoftwareVersion { get; init; }

    [JsonPropertyOrder(6)]
    public required ManifestFileEntryJsonV1[] Files { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ManifestJsonV2))]
internal sealed partial class MidoraJsonSerializerContextV2 : JsonSerializerContext;
