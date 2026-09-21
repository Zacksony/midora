using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Domain;

namespace Midora.Persistence;

public enum ProjectPresentationAllTracksModeV3
{
    Raw,
    Compiled
}

public enum OnionSourceMode
{
    Custom,
    Previous,
    Next
}

public sealed record TrackOnionPresetV3(
    MidoraId TargetTrackId,
    bool Enabled,
    double Opacity,
    IReadOnlyList<MidoraId> SourceTrackIds,
    OnionSourceMode SourceMode = OnionSourceMode.Custom);

public sealed record SubVoiceOnionPresetV3(
    MidoraId EventInstrumentId,
    MidoraId TargetSubVoiceId,
    bool Enabled,
    double Opacity,
    IReadOnlyList<MidoraId> SourceSubVoiceIds,
    OnionSourceMode SourceMode = OnionSourceMode.Custom);

public sealed record ProjectPresentationStateV3(
    ProjectPresentationAllTracksModeV3 AllTracksMode,
    IReadOnlyList<TrackOnionPresetV3> TrackOnionPresets,
    IReadOnlyList<SubVoiceOnionPresetV3> SubVoiceOnionPresets,
    ProjectPresentationWorkspaceStateV4? WorkspaceState = null,
    ProjectPresentationNavigationStateV4? Navigation = null)
{
    public static ProjectPresentationStateV3 Empty { get; } = new(
        ProjectPresentationAllTracksModeV3.Raw,
        Array.Empty<TrackOnionPresetV3>(),
        Array.Empty<SubVoiceOnionPresetV3>(),
        ProjectPresentationWorkspaceStateV4.Empty,
        ProjectPresentationNavigationStateV4.Empty);
}

internal static class ProjectPresentationCodecV3
{
    public static ProjectPresentationStateV3 Parse(
        ReadOnlySpan<byte> utf8,
        MidoraProject project,
        int? declaredSchemaVersion = null)
        => ParseWithRecovery(utf8, project, declaredSchemaVersion).State;

    internal static ProjectPresentationParseResultV4 ParseWithRecovery(
        ReadOnlySpan<byte> utf8,
        MidoraProject project,
        int? declaredSchemaVersion = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        StrictJsonV1.ValidateInput(utf8);
        using JsonDocument header = JsonDocument.Parse(utf8.ToArray());
        if (header.RootElement.ValueKind != JsonValueKind.Object
            || !header.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schemaVersion)
            || declaredSchemaVersion is { } declared && schemaVersion != declared)
            throw new InvalidDataException("project-presentation.json schemaVersion is missing or differs from its manifest.");
        if (schemaVersion is 3 or 4)
            return ProjectPresentationCodecV4.Read(utf8, project, declaredSchemaVersion);
        if (schemaVersion == 2)
            return new(ProjectPresentationCodecV2.Read(utf8, project), false, []);
        if (schemaVersion != 1)
            throw new InvalidDataException("project-presentation.json schemaVersion is not supported.");
        ProjectPresentationJsonV3 dto = JsonSerializer.Deserialize(
            utf8,
            ProjectPresentationJsonContextV3.Default.ProjectPresentationJsonV3)
            ?? throw new InvalidDataException("project-presentation.json cannot be null.");
        return new(FromDto(dto, project), false, []);
    }

    public static byte[] Serialize(
        ProjectPresentationStateV3 state,
        MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(project);
        return ProjectPresentationCodecV4.Serialize(state, project);
    }

    public static ProjectPresentationStateV3 ValidateAndCanonicalize(
        ProjectPresentationStateV3 state,
        MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(project);
        HashSet<MidoraId> trackIds = project.ArrangementTracks
            .Select(value => value.TrackId)
            .ToHashSet();
        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments
            .ToDictionary(value => value.Id);

        HashSet<MidoraId> trackTargets = [];
        TrackOnionPresetV3[] tracks = (state.TrackOnionPresets
                ?? throw new InvalidDataException("Track onion presets cannot be null."))
            .Select(value => ValidateTrack(value, trackIds, trackTargets))
            .OrderBy(value => value.TargetTrackId)
            .ToArray();

        HashSet<MidoraId> subVoiceTargets = [];
        SubVoiceOnionPresetV3[] subVoices = (state.SubVoiceOnionPresets
                ?? throw new InvalidDataException("SubVoice onion presets cannot be null."))
            .Select(value => ValidateSubVoice(value, instruments, subVoiceTargets))
            .OrderBy(value => value.EventInstrumentId)
            .ThenBy(value => value.TargetSubVoiceId)
            .ToArray();

        if (!Enum.IsDefined(state.AllTracksMode))
        {
            throw new InvalidDataException("The all-tracks presentation mode is invalid.");
        }
        ProjectPresentationWorkspaceStateV4 workspace = ProjectPresentationCodecV4.ValidateWorkspace(
            state.WorkspaceState,
            project);
        ProjectPresentationNavigationStateV4 navigation = ProjectPresentationCodecV4.ValidateNavigation(
            state.Navigation,
            project);
        return new(state.AllTracksMode, tracks, subVoices, workspace, navigation);
    }

    private static ProjectPresentationStateV3 FromDto(
        ProjectPresentationJsonV3 dto,
        MidoraProject project)
    {
        if (dto.SchemaVersion != 1)
        {
            throw new InvalidDataException("project-presentation.json schemaVersion is invalid.");
        }
        ProjectPresentationAllTracksModeV3 mode = dto.AllTracksMode switch
        {
            "raw" => ProjectPresentationAllTracksModeV3.Raw,
            "compiled" => ProjectPresentationAllTracksModeV3.Compiled,
            _ => throw new InvalidDataException("project-presentation.json allTracksMode is invalid.")
        };
        if (dto.TrackOnionPresets is null || dto.SubVoiceOnionPresets is null)
        {
            throw new InvalidDataException("project-presentation.json preset arrays are required.");
        }
        ProjectPresentationStateV3 state = new(
            mode,
            dto.TrackOnionPresets.Select(value => value is null
                ? throw new InvalidDataException("A Track onion preset cannot be null.")
                : new TrackOnionPresetV3(
                    value.TargetTrackId.ToDomain(),
                    value.Enabled,
                    value.Opacity,
                    (value.SourceTrackIds
                        ?? throw new InvalidDataException("Track onion sources are required."))
                        .Select(id => id.ToDomain()).ToArray())).ToArray(),
            dto.SubVoiceOnionPresets.Select(value => value is null
                ? throw new InvalidDataException("A SubVoice onion preset cannot be null.")
                : new SubVoiceOnionPresetV3(
                    value.EventInstrumentId.ToDomain(),
                    value.TargetSubVoiceId.ToDomain(),
                    value.Enabled,
                    value.Opacity,
                    (value.SourceSubVoiceIds
                        ?? throw new InvalidDataException("SubVoice onion sources are required."))
                        .Select(id => id.ToDomain()).ToArray())).ToArray());
        return ValidateAndCanonicalize(state, project);
    }

    private static TrackOnionPresetV3 ValidateTrack(
        TrackOnionPresetV3 value,
        IReadOnlySet<MidoraId> trackIds,
        ISet<MidoraId> targets)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateOpacity(value.Opacity);
        if (!Enum.IsDefined(value.SourceMode)) throw new InvalidDataException("Invalid Track onion source mode.");
        if (!trackIds.Contains(value.TargetTrackId) || !targets.Add(value.TargetTrackId))
        {
            throw new InvalidDataException("A Track onion target is missing or duplicated.");
        }
        MidoraId[] sources = ValidateSources(
            value.SourceTrackIds,
            value.TargetTrackId,
            trackIds,
            "Track");
        return value with { SourceTrackIds = sources };
    }

    private static SubVoiceOnionPresetV3 ValidateSubVoice(
        SubVoiceOnionPresetV3 value,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        ISet<MidoraId> targets)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateOpacity(value.Opacity);
        if (!Enum.IsDefined(value.SourceMode)) throw new InvalidDataException("Invalid SubVoice onion source mode.");
        if (!instruments.TryGetValue(value.EventInstrumentId, out EventInstrument? instrument))
        {
            throw new InvalidDataException("A SubVoice onion preset references a missing Event Instrument.");
        }
        HashSet<MidoraId> subVoiceIds = instrument.SubVoices.Select(item => item.Id).ToHashSet();
        if (!subVoiceIds.Contains(value.TargetSubVoiceId) || !targets.Add(value.TargetSubVoiceId))
        {
            throw new InvalidDataException("A SubVoice onion target is missing or duplicated.");
        }
        MidoraId[] sources = ValidateSources(
            value.SourceSubVoiceIds,
            value.TargetSubVoiceId,
            subVoiceIds,
            "SubVoice");
        return value with { SourceSubVoiceIds = sources };
    }

    private static MidoraId[] ValidateSources(
        IReadOnlyList<MidoraId>? values,
        MidoraId target,
        IReadOnlySet<MidoraId> validIds,
        string kind)
    {
        if (values is null)
        {
            throw new InvalidDataException($"{kind} onion sources cannot be null.");
        }
        HashSet<MidoraId> unique = [];
        MidoraId[] result = values.ToArray();
        foreach (MidoraId source in result)
        {
            if (source == target || !validIds.Contains(source) || !unique.Add(source))
            {
                throw new InvalidDataException(
                    $"A {kind} onion source is missing, duplicated, or equals its target.");
            }
        }
        return result;
    }

    private static void ValidateOpacity(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new InvalidDataException("Onion opacity must be finite and in [0, 1].");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectPresentationJsonV3
{
    [JsonPropertyOrder(0)] public required int SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public required string AllTracksMode { get; init; }
    [JsonPropertyOrder(2)] public required TrackOnionPresetJsonV3[] TrackOnionPresets { get; init; }
    [JsonPropertyOrder(3)] public required SubVoiceOnionPresetJsonV3[] SubVoiceOnionPresets { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class TrackOnionPresetJsonV3
{
    [JsonPropertyOrder(0)] public required StableIdJsonV1 TargetTrackId { get; init; }
    [JsonPropertyOrder(1)] public required bool Enabled { get; init; }
    [JsonPropertyOrder(2)] public required double Opacity { get; init; }
    [JsonPropertyOrder(3)] public required StableIdJsonV1[] SourceTrackIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SubVoiceOnionPresetJsonV3
{
    [JsonPropertyOrder(0)] public required StableIdJsonV1 EventInstrumentId { get; init; }
    [JsonPropertyOrder(1)] public required StableIdJsonV1 TargetSubVoiceId { get; init; }
    [JsonPropertyOrder(2)] public required bool Enabled { get; init; }
    [JsonPropertyOrder(3)] public required double Opacity { get; init; }
    [JsonPropertyOrder(4)] public required StableIdJsonV1[] SourceSubVoiceIds { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectPresentationJsonV3))]
internal sealed partial class ProjectPresentationJsonContextV3 : JsonSerializerContext;
