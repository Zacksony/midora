using Midora.Domain;

namespace Midora.Persistence;

/// <summary>
/// The presentation-only owner identity used by the schema 3 workspace state.
/// It deliberately contains no Project object, ViewModel or runtime handle.
/// </summary>
public enum ProjectPresentationWorkspaceOwnerKindV4
{
    Track,
    Segment,
    SubVoice
}

public readonly record struct ProjectPresentationWorkspaceOwnerV4(
    ProjectPresentationWorkspaceOwnerKindV4 Kind,
    MidoraId Id,
    MidoraId? InstrumentId = null);

public readonly record struct ProjectPresentationEditorSubdivisionV4(
    int Numerator,
    int Denominator,
    string Label,
    bool IsBar = false);

public readonly record struct ProjectPresentationEditorSettingsV4(
    ProjectPresentationEditorSubdivisionV4 Operation,
    bool Snap,
    bool Grid,
    long Length,
    int Velocity);

public sealed record ProjectPresentationEditorProfileV4(
    ProjectPresentationWorkspaceOwnerV4 Owner,
    ProjectPresentationEditorSettingsV4 Piano,
    ProjectPresentationEditorSettingsV4 Event,
    long TickSpan,
    double KeyHeight,
    int Tool,
    int Shape,
    bool LanesVisible,
    double LanesHeight,
    bool ListVisible,
    double ListWidth);

public readonly record struct ProjectPresentationEditorLocalViewV4(
    ProjectPresentationWorkspaceOwnerV4 Owner,
    long StartTick,
    int FirstLane,
    int FirstRow);

public readonly record struct ProjectPresentationLaneKeyV4(
    int Type,
    MidoraId? Parameter,
    int Kind,
    int Number);

public readonly record struct ProjectPresentationLaneTargetV4(
    ProjectPresentationLaneKeyV4 Key,
    bool Hidden,
    double Minimum,
    double Maximum);

public sealed record ProjectPresentationLaneMemoryV4(
    ProjectPresentationWorkspaceOwnerV4 Owner,
    ProjectPresentationLaneKeyV4 Active,
    IReadOnlyList<ProjectPresentationLaneTargetV4> Targets,
    IReadOnlyList<ProjectPresentationLaneKeyV4> ExplicitMidiTargets);

public sealed record ProjectPresentationMonitoringV4(
    IReadOnlyList<MidoraId> MutedTrackIds,
    IReadOnlyList<MidoraId> SoloTrackIds,
    IReadOnlyList<MidoraId> MutedGroupIds,
    IReadOnlyList<MidoraId> SoloGroupIds)
{
    public static ProjectPresentationMonitoringV4 Empty { get; } = new([], [], [], []);
}

public sealed record ProjectPresentationWorkspaceStateV4(
    IReadOnlyList<ProjectPresentationEditorProfileV4> Profiles,
    IReadOnlyList<ProjectPresentationEditorLocalViewV4> Views,
    IReadOnlyList<ProjectPresentationLaneMemoryV4> Lanes,
    ProjectPresentationMonitoringV4 Monitoring)
{
    public static ProjectPresentationWorkspaceStateV4 Empty { get; } = new([], [], [], ProjectPresentationMonitoringV4.Empty);
}

public sealed record ProjectPresentationParseResultV4(
    ProjectPresentationStateV3 State,
    bool Recovered,
    IReadOnlyList<string> RecoveredSections);

internal sealed record ProjectPresentationSerializationResultV4(
    ProjectPresentationStateV3 State,
    byte[] Bytes,
    IReadOnlyList<string> OmittedSections);
