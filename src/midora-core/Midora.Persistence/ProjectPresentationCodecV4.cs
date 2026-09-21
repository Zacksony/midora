using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Midora.Domain;

namespace Midora.Persistence;

/// <summary>
/// Presentation schema 4. The reader is deliberately section-oriented: a bad
/// workspace section is isolated from valid Onion/All-Tracks data and from the
/// Project source package.
/// </summary>
internal static class ProjectPresentationCodecV4
{
    internal const int SchemaVersion = 4;
    private const int MaximumSectionEntries = 131_072;
    private const int MaximumLaneTargets = 65_536;
    private const int MaximumLabelLength = 64;
    internal const int MaximumNavigationTabs = 4_096;
    internal const int MaximumNavigationViews = 4_096;
    internal const int MaximumNavigationTextLength = 256;
    // This is a presentation-file budget, not a Project/package budget.  Save
    // admits complete sections only; it never truncates an array or a string.
    internal const int MaximumSerializedBytes = 64 * 1024 * 1024;

    private static bool IsRecoverablePresentationValidationFailure(Exception exception) =>
        exception is InvalidDataException or ArgumentException or OverflowException;

    internal static byte[] Serialize(ProjectPresentationStateV3 state, MidoraProject project)
    {
        ProjectPresentationStateV3 canonical = ProjectPresentationCodecV3.ValidateAndCanonicalize(
            state,
            project);
        ProjectPresentationWorkspaceStateV4 workspace = canonical.WorkspaceState
            ?? ProjectPresentationWorkspaceStateV4.Empty;
        ProjectPresentationNavigationStateV4 navigation = canonical.Navigation
            ?? ProjectPresentationNavigationStateV4.Empty;
        ProjectPresentationJsonV4 dto = new()
        {
            SchemaVersion = SchemaVersion,
            AllTracksMode = canonical.AllTracksMode switch
            {
                ProjectPresentationAllTracksModeV3.Raw => "raw",
                ProjectPresentationAllTracksModeV3.Compiled => "compiled",
                _ => throw new InvalidDataException("Unknown all-tracks presentation mode.")
            },
            TrackOnionPresets = canonical.TrackOnionPresets.Select(value =>
                new TrackOnionPresetJsonV2
                {
                    TargetTrackId = new(value.TargetTrackId.Value),
                    Enabled = value.Enabled,
                    Opacity = value.Opacity,
                    SourceMode = ProjectPresentationCodecV2.FormatMode(value.SourceMode),
                    SourceTrackIds = value.SourceTrackIds.Select(id => new StableIdJsonV1(id.Value)).ToArray()
                }).ToArray(),
            SubVoiceOnionPresets = canonical.SubVoiceOnionPresets.Select(value =>
                new SubVoiceOnionPresetJsonV2
                {
                    EventInstrumentId = new(value.EventInstrumentId.Value),
                    TargetSubVoiceId = new(value.TargetSubVoiceId.Value),
                    Enabled = value.Enabled,
                    Opacity = value.Opacity,
                    SourceMode = ProjectPresentationCodecV2.FormatMode(value.SourceMode),
                    SourceSubVoiceIds = value.SourceSubVoiceIds.Select(id => new StableIdJsonV1(id.Value)).ToArray()
                }).ToArray(),
            WorkspaceState = ToDto(workspace),
            WorkspaceNavigation = ToDto(navigation)
        };
        return StrictJsonV1.SerializeWithFinalLf(
            dto,
            ProjectPresentationJsonContextV4.Default.ProjectPresentationJsonV4);
    }

    internal static ProjectPresentationSerializationResultV4 PrepareForSave(
        ProjectPresentationStateV3 state,
        MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(project);

        List<string> omitted = [];
        ProjectPresentationStateV3 baseState;
        try
        {
            // Validate the music-adjacent presentation section independently.
            baseState = ProjectPresentationCodecV3.ValidateAndCanonicalize(
                state with
                {
                    WorkspaceState = ProjectPresentationWorkspaceStateV4.Empty,
                    Navigation = ProjectPresentationNavigationStateV4.Empty
                },
                project);
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
        {
            omitted.Add("onionAndAllTracks");
            baseState = ProjectPresentationCodecV3.ValidateAndCanonicalize(
                new(ProjectPresentationAllTracksModeV3.Raw, [], [], ProjectPresentationWorkspaceStateV4.Empty,
                    ProjectPresentationNavigationStateV4.Empty),
                project);
        }

        ProjectPresentationWorkspaceStateV4 workspace = PrepareWorkspaceForSave(
            state.WorkspaceState,
            project,
            omitted);
        ProjectPresentationNavigationStateV4 navigation = PrepareNavigationForSave(
            state.Navigation,
            project,
            omitted);
        ProjectPresentationStateV3 prepared = baseState with
        {
            WorkspaceState = workspace,
            Navigation = navigation
        };
        byte[] bytes = SerializeWithBudget(prepared, omitted);
        return new(prepared, bytes, omitted.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static ProjectPresentationWorkspaceStateV4 PrepareWorkspaceForSave(
        ProjectPresentationWorkspaceStateV4? value,
        MidoraProject project,
        ICollection<string> omitted)
    {
        value ??= ProjectPresentationWorkspaceStateV4.Empty;
        List<ProjectPresentationEditorProfileV4> profiles = [];
        List<ProjectPresentationEditorLocalViewV4> views = [];
        List<ProjectPresentationLaneMemoryV4> lanes = [];
        ProjectPresentationMonitoringV4 monitoring = ProjectPresentationMonitoringV4.Empty;

        TryWorkspaceSection(
            "workspaceState.profiles",
            () => ValidateWorkspaceProfiles(value.Profiles, project),
            profiles,
            omitted);
        TryWorkspaceSection(
            "workspaceState.views",
            () => ValidateWorkspaceViews(value.Views, project),
            views,
            omitted);
        TryWorkspaceSection(
            "workspaceState.lanes",
            () => ValidateWorkspaceLanes(value.Lanes, project),
            lanes,
            omitted);
        try
        {
            monitoring = ValidateMonitoring(value.Monitoring, project);
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
        {
            omitted.Add("workspaceState.monitoring");
        }
        return new(profiles, views, lanes, monitoring);

        static void TryWorkspaceSection<T>(
            string name,
            Func<IReadOnlyList<T>> validate,
            List<T> destination,
            ICollection<string> omitted)
        {
            try { destination.AddRange(validate()); }
            catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
            {
                omitted.Add(name);
            }
        }
    }

    private static ProjectPresentationNavigationStateV4 PrepareNavigationForSave(
        ProjectPresentationNavigationStateV4? value,
        MidoraProject project,
        ICollection<string> omitted)
    {
        try
        {
            return ValidateNavigation(value, project);
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
        {
            omitted.Add("workspaceNavigation");
            return ProjectPresentationNavigationStateV4.Empty;
        }
    }

    private static ProjectPresentationEditorProfileV4[] ValidateWorkspaceProfiles(
        IReadOnlyList<ProjectPresentationEditorProfileV4>? values,
        MidoraProject project)
    {
        if (values is null || values.Count > MaximumSectionEntries)
            throw new InvalidDataException("Workspace profile section exceeds its entry budget.");
        WorkspaceValidationContext context = CreateWorkspaceValidationContext(project);
        HashSet<ProjectPresentationWorkspaceOwnerV4> owners = [];
        return values.Select(item => ValidateProfile(
                item, project, context.TrackIds, context.SegmentIds, context.Instruments, owners))
            .OrderBy(item => item.Owner.Kind)
            .ThenBy(item => item.Owner.InstrumentId)
            .ThenBy(item => item.Owner.Id)
            .ToArray();
    }

    private static ProjectPresentationEditorLocalViewV4[] ValidateWorkspaceViews(
        IReadOnlyList<ProjectPresentationEditorLocalViewV4>? values,
        MidoraProject project)
    {
        if (values is null || values.Count > MaximumSectionEntries)
            throw new InvalidDataException("Workspace view section exceeds its entry budget.");
        WorkspaceValidationContext context = CreateWorkspaceValidationContext(project);
        HashSet<ProjectPresentationWorkspaceOwnerV4> owners = [];
        return values.Select(item => ValidateView(
                item, context.TrackIds, context.SegmentIds, context.Instruments, owners))
            .OrderBy(item => item.Owner.Kind)
            .ThenBy(item => item.Owner.InstrumentId)
            .ThenBy(item => item.Owner.Id)
            .ToArray();
    }

    private static ProjectPresentationLaneMemoryV4[] ValidateWorkspaceLanes(
        IReadOnlyList<ProjectPresentationLaneMemoryV4>? values,
        MidoraProject project)
    {
        if (values is null || values.Count > MaximumSectionEntries)
            throw new InvalidDataException("Workspace lane section exceeds its entry budget.");
        WorkspaceValidationContext context = CreateWorkspaceValidationContext(project);
        HashSet<ProjectPresentationWorkspaceOwnerV4> owners = [];
        return values.Select(item => ValidateLanes(
                item, context.TrackIds, context.SegmentIds, context.Instruments, owners))
            .OrderBy(item => item.Owner.Kind)
            .ThenBy(item => item.Owner.InstrumentId)
            .ThenBy(item => item.Owner.Id)
            .ToArray();
    }

    private static WorkspaceValidationContext CreateWorkspaceValidationContext(MidoraProject project) =>
        new(
            project.ArrangementTracks.Select(value => value.TrackId).ToHashSet(),
            project.Tracks
                .SelectMany(track => track.Segments.Select(segment => segment.Id))
                .Concat(project.PureMidiTracks.SelectMany(track => track.Segments.Select(segment => segment.Id)))
                .ToHashSet(),
            project.EventInstruments.ToDictionary(value => value.Id));

    private sealed record WorkspaceValidationContext(
        IReadOnlySet<MidoraId> TrackIds,
        IReadOnlySet<MidoraId> SegmentIds,
        IReadOnlyDictionary<MidoraId, EventInstrument> Instruments);

    private static byte[] SerializeWithBudget(
        ProjectPresentationStateV3 prepared,
        ICollection<string> omitted)
    {
        // Admit complete sections in a deterministic order. A section that
        // does not fit is omitted as a whole, never truncated. Onion/All
        // Tracks is one logical section because its mode and source lists are
        // interpreted together.
        ProjectPresentationStateV3 acceptedState = prepared with
        {
            AllTracksMode = ProjectPresentationAllTracksModeV3.Raw,
            TrackOnionPresets = [],
            SubVoiceOnionPresets = [],
            WorkspaceState = ProjectPresentationWorkspaceStateV4.Empty,
            Navigation = ProjectPresentationNavigationStateV4.Empty
        };
        byte[] current = SerializeStrict(acceptedState);
        if (current.Length > MaximumSerializedBytes)
            throw new InvalidDataException("The minimal project presentation exceeds its byte budget.");

        ProjectPresentationStateV3 candidateWithOnion = acceptedState with
        {
            AllTracksMode = prepared.AllTracksMode,
            TrackOnionPresets = prepared.TrackOnionPresets,
            SubVoiceOnionPresets = prepared.SubVoiceOnionPresets
        };
        byte[] onionBytes = SerializeStrict(candidateWithOnion);
        if (onionBytes.Length <= MaximumSerializedBytes)
        {
            acceptedState = candidateWithOnion;
            current = onionBytes;
        }
        else
        {
            omitted.Add("onionAndAllTracks");
        }

        ProjectPresentationWorkspaceStateV4 accepted = ProjectPresentationWorkspaceStateV4.Empty;
        foreach (string section in new[]
                 {
                     "workspaceState.profiles",
                     "workspaceState.views",
                     "workspaceState.lanes",
                     "workspaceState.monitoring"
                 })
        {
            ProjectPresentationWorkspaceStateV4 candidate = AddWorkspaceSection(
                accepted,
                prepared.WorkspaceState ?? ProjectPresentationWorkspaceStateV4.Empty,
                section);
            byte[] candidateBytes = SerializeStrict(acceptedState with { WorkspaceState = candidate });
            if (candidateBytes.Length <= MaximumSerializedBytes)
            {
                accepted = candidate;
                acceptedState = acceptedState with { WorkspaceState = candidate };
                current = candidateBytes;
            }
            else
            {
                omitted.Add(section);
            }
        }
        byte[] navigationBytes = SerializeStrict(acceptedState with
        {
            Navigation = prepared.Navigation ?? ProjectPresentationNavigationStateV4.Empty
        });
        if (navigationBytes.Length <= MaximumSerializedBytes)
        {
            current = navigationBytes;
        }
        else
        {
            omitted.Add("workspaceNavigation");
        }
        return current;
    }

    private static ProjectPresentationWorkspaceStateV4 AddWorkspaceSection(
        ProjectPresentationWorkspaceStateV4 accepted,
        ProjectPresentationWorkspaceStateV4 source,
        string section) => section switch
        {
            "workspaceState.profiles" => accepted with { Profiles = source.Profiles },
            "workspaceState.views" => accepted with { Views = source.Views },
            "workspaceState.lanes" => accepted with { Lanes = source.Lanes },
            "workspaceState.monitoring" => accepted with { Monitoring = source.Monitoring },
            _ => throw new InvalidDataException("Unknown presentation workspace section.")
        };

    private static byte[] SerializeStrict(ProjectPresentationStateV3 state)
    {
        ProjectPresentationWorkspaceStateV4 workspace = state.WorkspaceState
            ?? ProjectPresentationWorkspaceStateV4.Empty;
        ProjectPresentationNavigationStateV4 navigation = state.Navigation
            ?? ProjectPresentationNavigationStateV4.Empty;
        ProjectPresentationJsonV4 dto = new()
        {
            SchemaVersion = SchemaVersion,
            AllTracksMode = state.AllTracksMode switch
            {
                ProjectPresentationAllTracksModeV3.Raw => "raw",
                ProjectPresentationAllTracksModeV3.Compiled => "compiled",
                _ => throw new InvalidDataException("Unknown all-tracks presentation mode.")
            },
            TrackOnionPresets = state.TrackOnionPresets.Select(value => new TrackOnionPresetJsonV2
            {
                TargetTrackId = new(value.TargetTrackId.Value),
                Enabled = value.Enabled,
                Opacity = value.Opacity,
                SourceMode = ProjectPresentationCodecV2.FormatMode(value.SourceMode),
                SourceTrackIds = value.SourceTrackIds.Select(id => new StableIdJsonV1(id.Value)).ToArray()
            }).ToArray(),
            SubVoiceOnionPresets = state.SubVoiceOnionPresets.Select(value => new SubVoiceOnionPresetJsonV2
            {
                EventInstrumentId = new(value.EventInstrumentId.Value),
                TargetSubVoiceId = new(value.TargetSubVoiceId.Value),
                Enabled = value.Enabled,
                Opacity = value.Opacity,
                SourceMode = ProjectPresentationCodecV2.FormatMode(value.SourceMode),
                SourceSubVoiceIds = value.SourceSubVoiceIds.Select(id => new StableIdJsonV1(id.Value)).ToArray()
            }).ToArray(),
            WorkspaceState = ToDto(workspace),
            WorkspaceNavigation = ToDto(navigation)
        };
        return StrictJsonV1.SerializeWithFinalLf(
            dto,
            ProjectPresentationJsonContextV4.Default.ProjectPresentationJsonV4);
    }

    internal static ProjectPresentationParseResultV4 Read(
        ReadOnlySpan<byte> utf8,
        MidoraProject project,
        int? declaredSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(project);
        using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("project-presentation.json must be an object.");

        int schemaVersion = RequiredInt(root, "schemaVersion");
        if (schemaVersion is not (3 or 4)
            || declaredSchemaVersion is { } declared && declared != schemaVersion)
        {
            throw new InvalidDataException(
                "project-presentation.json schemaVersion is missing, unsupported, or differs from its manifest.");
        }
        EnsureKnownRootProperties(root, schemaVersion);

        List<string> recovered = [];
        ProjectPresentationAllTracksModeV3 mode = ProjectPresentationAllTracksModeV3.Raw;
        if (!TryReadMode(root, out mode)) recovered.Add("onionAndAllTracks");

        TrackOnionPresetV3[] tracks = [];
        SubVoiceOnionPresetV3[] voices = [];
        if (!TryReadOnionPresets(root, project, out tracks, out voices))
        {
            recovered.Add("onionAndAllTracks");
            // All-Tracks mode and the two Onion preset arrays are one logical
            // section. Do not retain a mode that points at a discarded preset
            // section after recovery.
            mode = ProjectPresentationAllTracksModeV3.Raw;
        }

        ProjectPresentationWorkspaceStateV4 workspace = ProjectPresentationWorkspaceStateV4.Empty;
        if (!root.TryGetProperty("workspaceState", out JsonElement workspaceElement)
            || workspaceElement.ValueKind != JsonValueKind.Object)
        {
            recovered.Add("workspaceState");
        }
        else
        {
            try
            {
                workspace = ReadWorkspace(workspaceElement, project, recovered);
            }
            catch (InvalidDataException)
            {
                recovered.Add("workspaceState");
                workspace = ProjectPresentationWorkspaceStateV4.Empty;
            }
        }

        ProjectPresentationNavigationStateV4 navigation = ProjectPresentationNavigationStateV4.Empty;
        if (schemaVersion == SchemaVersion)
        {
            if (!root.TryGetProperty("workspaceNavigation", out JsonElement navigationElement)
                || navigationElement.ValueKind != JsonValueKind.Object)
            {
                recovered.Add("workspaceNavigation");
            }
            else
            {
                try
                {
                    navigation = ReadNavigation(navigationElement, project, recovered);
                }
                catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
                {
                    recovered.Add("workspaceNavigation");
                    navigation = ProjectPresentationNavigationStateV4.Empty;
                }
            }
        }
        // Validate the music-adjacent presentation section independently from
        // workspace state. A malformed owner in a remembered view must not
        // discard valid Onion/All-Tracks state (or the Project itself).
        ProjectPresentationStateV3 canonicalBase;
        try
        {
            canonicalBase = ProjectPresentationCodecV3.ValidateAndCanonicalize(
                new(mode, tracks, voices, ProjectPresentationWorkspaceStateV4.Empty,
                    ProjectPresentationNavigationStateV4.Empty),
                project);
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
        {
            recovered.Add("onionAndAllTracks");
            canonicalBase = ProjectPresentationCodecV3.ValidateAndCanonicalize(
                new(ProjectPresentationAllTracksModeV3.Raw, [], [], ProjectPresentationWorkspaceStateV4.Empty,
                    ProjectPresentationNavigationStateV4.Empty),
                project);
        }

        ProjectPresentationWorkspaceStateV4 canonicalWorkspace;
        try
        {
            canonicalWorkspace = ValidateWorkspace(workspace, project);
        }
        catch (InvalidDataException)
        {
            recovered.Add("workspaceState");
            canonicalWorkspace = ProjectPresentationWorkspaceStateV4.Empty;
        }

        ProjectPresentationNavigationStateV4 canonicalNavigation;
        try
        {
            canonicalNavigation = ValidateNavigation(navigation, project);
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception))
        {
            recovered.Add("workspaceNavigation");
            canonicalNavigation = ProjectPresentationNavigationStateV4.Empty;
        }

        ProjectPresentationStateV3 state = canonicalBase with
        {
            WorkspaceState = canonicalWorkspace,
            Navigation = canonicalNavigation
        };
        return new(state, recovered.Count != 0, recovered.Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static ProjectPresentationWorkspaceStateV4 ValidateWorkspace(
        ProjectPresentationWorkspaceStateV4? value,
        MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        value ??= ProjectPresentationWorkspaceStateV4.Empty;
        if (value.Profiles is null || value.Views is null || value.Lanes is null || value.Monitoring is null)
            throw new InvalidDataException("Presentation workspace sections cannot be null.");
        if (value.Profiles.Count > MaximumSectionEntries
            || value.Views.Count > MaximumSectionEntries
            || value.Lanes.Count > MaximumSectionEntries)
            throw new InvalidDataException("Presentation workspace section exceeds its entry budget.");

        HashSet<MidoraId> trackIds = project.ArrangementTracks.Select(value => value.TrackId).ToHashSet();
        HashSet<MidoraId> segmentIds = project.Tracks
            .SelectMany(track => track.Segments.Select(segment => segment.Id))
            .Concat(project.PureMidiTracks.SelectMany(track => track.Segments.Select(segment => segment.Id)))
            .ToHashSet();
        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments.ToDictionary(value => value.Id);

        HashSet<ProjectPresentationWorkspaceOwnerV4> profileOwners = [];
        ProjectPresentationEditorProfileV4[] profiles = value.Profiles
            .Select(item => ValidateProfile(item, project, trackIds, segmentIds, instruments, profileOwners))
            .OrderBy(item => item.Owner.Kind)
            .ThenBy(item => item.Owner.InstrumentId)
            .ThenBy(item => item.Owner.Id)
            .ToArray();
        HashSet<ProjectPresentationWorkspaceOwnerV4> viewOwners = [];
        ProjectPresentationEditorLocalViewV4[] views = value.Views
            .Select(item => ValidateView(item, trackIds, segmentIds, instruments, viewOwners))
            .OrderBy(item => item.Owner.Kind)
            .ThenBy(item => item.Owner.InstrumentId)
            .ThenBy(item => item.Owner.Id)
            .ToArray();
        HashSet<ProjectPresentationWorkspaceOwnerV4> laneOwners = [];
        ProjectPresentationLaneMemoryV4[] lanes = value.Lanes
            .Select(item => ValidateLanes(item, trackIds, segmentIds, instruments, laneOwners))
            .OrderBy(item => item.Owner.Kind)
            .ThenBy(item => item.Owner.InstrumentId)
            .ThenBy(item => item.Owner.Id)
            .ToArray();

        ProjectPresentationMonitoringV4 monitoring = ValidateMonitoring(value.Monitoring, project);
        return new(profiles, views, lanes, monitoring);
    }

    private static WorkspaceStateJsonV4 ToDto(ProjectPresentationWorkspaceStateV4 value) => new()
    {
        Profiles = value.Profiles.Select(ToDto).ToArray(),
        Views = value.Views.Select(ToDto).ToArray(),
        Lanes = value.Lanes.Select(ToDto).ToArray(),
        Monitoring = new MonitoringJsonV4
        {
            MutedTrackIds = value.Monitoring.MutedTrackIds.Select(id => new StableIdJsonV1(id.Value)).ToArray(),
            SoloTrackIds = value.Monitoring.SoloTrackIds.Select(id => new StableIdJsonV1(id.Value)).ToArray(),
            MutedGroupIds = value.Monitoring.MutedGroupIds.Select(id => new StableIdJsonV1(id.Value)).ToArray(),
            SoloGroupIds = value.Monitoring.SoloGroupIds.Select(id => new StableIdJsonV1(id.Value)).ToArray()
        }
    };

    private static WorkspaceNavigationJsonV4 ToDto(ProjectPresentationNavigationStateV4 value) => new()
    {
        Tabs = value.Tabs.Select(ToDto).ToArray(),
        ActiveTab = ToDto(value.ActiveTab),
        Views = value.Views.Select(ToDto).ToArray()
    };

    private static NavigationKeyJsonV4 ToDto(ProjectPresentationNavigationKeyV4 value) => new()
    {
        Kind = value.Kind switch
        {
            ProjectPresentationNavigationKindV4.Arrangement => "arrangement",
            ProjectPresentationNavigationKindV4.EventInstrumentLibrary => "eventInstrumentLibrary",
            ProjectPresentationNavigationKindV4.ProjectSettings => "projectSettings",
            ProjectPresentationNavigationKindV4.Diagnostics => "diagnostics",
            ProjectPresentationNavigationKindV4.ConductorTrack => "conductorTrack",
            ProjectPresentationNavigationKindV4.SegmentEditor => "segmentEditor",
            ProjectPresentationNavigationKindV4.EventInstrumentEditor => "eventInstrumentEditor",
            ProjectPresentationNavigationKindV4.AllTracks => "allTracks",
            _ => throw new InvalidDataException("Unknown workspace navigation kind.")
        },
        ObjectId = value.ObjectId is { } id ? new StableIdJsonV1(id.Value) : null
    };

    private static WorkspaceNavigationViewJsonV4 ToDto(ProjectPresentationNavigationViewV4 value) => new()
    {
        Key = ToDto(value.Key),
        Page = value.Page,
        SecondaryId = value.SecondaryId is { } secondary ? new StableIdJsonV1(secondary.Value) : null,
        SearchText = value.SearchText,
        PrimaryFilter = value.PrimaryFilter,
        SecondaryFilter = value.SecondaryFilter,
        TertiaryFilter = value.TertiaryFilter,
        SortMode = value.SortMode,
        StartTick = value.StartTick,
        TickSpan = value.TickSpan,
        FirstLane = value.FirstLane,
        FirstRow = value.FirstRow,
        LaneHeight = value.LaneHeight,
        FollowPlayback = value.FollowPlayback,
        LowerEditorVisible = value.LowerEditorVisible,
        LowerEditorHeight = value.LowerEditorHeight
    };

    private static ProfileJsonV4 ToDto(ProjectPresentationEditorProfileV4 value) => new()
    {
        Owner = ToDto(value.Owner),
        Piano = ToDto(value.Piano),
        Event = ToDto(value.Event),
        TickSpan = value.TickSpan,
        KeyHeight = value.KeyHeight,
        Tool = value.Tool,
        Shape = value.Shape,
        LanesVisible = value.LanesVisible,
        LanesHeight = value.LanesHeight,
        ListVisible = value.ListVisible,
        ListWidth = value.ListWidth
    };

    private static ViewJsonV4 ToDto(ProjectPresentationEditorLocalViewV4 value) => new()
    {
        Owner = ToDto(value.Owner),
        StartTick = value.StartTick,
        FirstLane = value.FirstLane,
        FirstRow = value.FirstRow
    };

    private static LaneMemoryJsonV4 ToDto(ProjectPresentationLaneMemoryV4 value) => new()
    {
        Owner = ToDto(value.Owner),
        Active = ToDto(value.Active),
        Targets = value.Targets.Select(item => new LaneTargetJsonV4
        {
            Key = ToDto(item.Key),
            Hidden = item.Hidden,
            Minimum = item.Minimum,
            Maximum = item.Maximum
        }).ToArray(),
        ExplicitMidiTargets = value.ExplicitMidiTargets.Select(ToDto).ToArray()
    };

    private static OwnerJsonV4 ToDto(ProjectPresentationWorkspaceOwnerV4 value) => new()
    {
        Kind = value.Kind switch
        {
            ProjectPresentationWorkspaceOwnerKindV4.Track => "track",
            ProjectPresentationWorkspaceOwnerKindV4.Segment => "segment",
            ProjectPresentationWorkspaceOwnerKindV4.SubVoice => "subVoice",
            _ => throw new InvalidDataException("Unknown presentation workspace owner kind.")
        },
        Id = new(value.Id.Value),
        InstrumentId = value.InstrumentId is { } id ? new StableIdJsonV1(id.Value) : null
    };

    private static EditorSettingsJsonV4 ToDto(ProjectPresentationEditorSettingsV4 value) => new()
    {
        Operation = new SubdivisionJsonV4
        {
            Numerator = value.Operation.Numerator,
            Denominator = value.Operation.Denominator,
            Label = value.Operation.Label,
            IsBar = value.Operation.IsBar
        },
        Snap = value.Snap,
        Grid = value.Grid,
        Length = value.Length,
        Velocity = value.Velocity
    };

    private static LaneKeyJsonV4 ToDto(ProjectPresentationLaneKeyV4 value) => new()
    {
        Type = value.Type,
        Parameter = value.Parameter is { } id ? new StableIdJsonV1(id.Value) : null,
        Kind = value.Kind,
        Number = value.Number
    };

    private static ProjectPresentationWorkspaceStateV4 ReadWorkspace(
        JsonElement element,
        MidoraProject project,
        List<string> recovered)
    {
        EnsureKnownWorkspaceProperties(element);
        List<ProjectPresentationEditorProfileV4> profiles = [];
        List<ProjectPresentationEditorLocalViewV4> views = [];
        List<ProjectPresentationLaneMemoryV4> lanes = [];
        ProjectPresentationMonitoringV4 monitoring = ProjectPresentationMonitoringV4.Empty;

        if (TryDeserialize(element, "profiles", ProjectPresentationJsonContextV4.Default.ProfileJsonV4Array,
                out ProfileJsonV4[]? profileDtos))
        {
            try
            {
                profiles.AddRange(profileDtos!.Select(FromDto));
            }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            {
                recovered.Add("workspaceState.profiles");
                profiles.Clear();
            }
        }
        else recovered.Add("workspaceState.profiles");

        if (TryDeserialize(element, "views", ProjectPresentationJsonContextV4.Default.ViewJsonV4Array,
                out ViewJsonV4[]? viewDtos))
        {
            try { views.AddRange(viewDtos!.Select(FromDto)); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            { recovered.Add("workspaceState.views"); views.Clear(); }
        }
        else recovered.Add("workspaceState.views");

        if (TryDeserialize(element, "lanes", ProjectPresentationJsonContextV4.Default.LaneMemoryJsonV4Array,
                out LaneMemoryJsonV4[]? laneDtos))
        {
            try { lanes.AddRange(laneDtos!.Select(FromDto)); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            { recovered.Add("workspaceState.lanes"); lanes.Clear(); }
        }
        else recovered.Add("workspaceState.lanes");

        if (TryDeserialize(element, "monitoring", ProjectPresentationJsonContextV4.Default.MonitoringJsonV4,
                out MonitoringJsonV4? monitoringDto))
        {
            try { monitoring = FromDto(monitoringDto!); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
            { recovered.Add("workspaceState.monitoring"); monitoring = ProjectPresentationMonitoringV4.Empty; }
        }
        else recovered.Add("workspaceState.monitoring");

        // The final project-aware validation is intentionally outside the local
        // section readers, so a dangling owner only invalidates the section that
        // contains it.
        return new(profiles, views, lanes, monitoring);
    }

    private static ProjectPresentationNavigationStateV4 ReadNavigation(
        JsonElement element,
        MidoraProject project,
        ICollection<string> recovered)
    {
        EnsureKnownNavigationProperties(element);
        NavigationKeyJsonV4[] tabs = JsonSerializer.Deserialize(
                element.GetProperty("tabs"),
                ProjectPresentationJsonContextV4.Default.NavigationKeyJsonV4Array)
            ?? throw new InvalidDataException("workspaceNavigation.tabs cannot be null.");
        NavigationKeyJsonV4 activeDto = JsonSerializer.Deserialize(
                element.GetProperty("activeTab"),
                ProjectPresentationJsonContextV4.Default.NavigationKeyJsonV4)
            ?? throw new InvalidDataException("workspaceNavigation.activeTab cannot be null.");
        WorkspaceNavigationViewJsonV4[] views = JsonSerializer.Deserialize(
                element.GetProperty("views"),
                ProjectPresentationJsonContextV4.Default.WorkspaceNavigationViewJsonV4Array)
            ?? throw new InvalidDataException("workspaceNavigation.views cannot be null.");

        List<ProjectPresentationNavigationKeyV4> validTabs = [];
        foreach (NavigationKeyJsonV4? item in tabs)
        {
            if (!TryReadNavigationKey(item, project, out ProjectPresentationNavigationKeyV4 key))
            {
                recovered.Add("workspaceNavigation.tabs");
                continue;
            }
            if (validTabs.Contains(key))
            {
                recovered.Add("workspaceNavigation.tabs");
                continue;
            }
            validTabs.Add(key);
        }

        ProjectPresentationNavigationKeyV4 arrangement =
            new(ProjectPresentationNavigationKindV4.Arrangement);
        if (!validTabs.Contains(arrangement))
        {
            validTabs.Insert(0, arrangement);
            recovered.Add("workspaceNavigation.tabs");
        }
        else if (validTabs[0] != arrangement)
        {
            validTabs.Remove(arrangement);
            validTabs.Insert(0, arrangement);
            recovered.Add("workspaceNavigation.tabs");
        }

        ProjectPresentationNavigationKeyV4 active = arrangement;
        if (!TryReadNavigationKey(activeDto, project, out ProjectPresentationNavigationKeyV4 requestedActive)
            || !validTabs.Contains(requestedActive))
        {
            recovered.Add("workspaceNavigation.activeTab");
        }
        else
        {
            active = requestedActive;
        }

        HashSet<ProjectPresentationNavigationKeyV4> tabSet = validTabs.ToHashSet();
        List<ProjectPresentationNavigationViewV4> validViews = [];
        foreach (WorkspaceNavigationViewJsonV4? item in views)
        {
            if (!TryReadNavigationView(item, project, tabSet, out ProjectPresentationNavigationViewV4 view)
                || validViews.Any(existing => existing.Key == view.Key))
            {
                recovered.Add("workspaceNavigation.views");
                continue;
            }
            validViews.Add(view);
        }
        return ValidateNavigation(
            new ProjectPresentationNavigationStateV4(
                validTabs.ToArray(),
                active,
                validViews.ToArray()),
            project);
    }

    private static bool TryReadNavigationKey(
        NavigationKeyJsonV4? value,
        MidoraProject project,
        out ProjectPresentationNavigationKeyV4 key)
    {
        try
        {
            if (value is null) throw new InvalidDataException("A workspace navigation key cannot be null.");
            key = FromDto(value);
            ValidateNavigationKey(key, project);
            return true;
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception)
            || exception is NullReferenceException)
        {
            key = default;
            return false;
        }
    }

    private static bool TryReadNavigationView(
        WorkspaceNavigationViewJsonV4? value,
        MidoraProject project,
        IReadOnlySet<ProjectPresentationNavigationKeyV4> tabs,
        out ProjectPresentationNavigationViewV4 view)
    {
        try
        {
            if (value is null) throw new InvalidDataException("A workspace navigation view cannot be null.");
            view = FromDto(value);
            ValidateNavigationKey(view.Key, project);
            if (!tabs.Contains(view.Key))
                throw new InvalidDataException("A workspace navigation view is not present in the tab list.");
            ValidateNavigationView(view, project);
            return true;
        }
        catch (Exception exception) when (IsRecoverablePresentationValidationFailure(exception)
            || exception is NullReferenceException)
        {
            view = new(new(ProjectPresentationNavigationKindV4.Arrangement));
            return false;
        }
    }

    private static ProjectPresentationNavigationKeyV4 FromDto(NavigationKeyJsonV4 value)
    {
        ProjectPresentationNavigationKindV4 kind = value.Kind switch
        {
            "arrangement" => ProjectPresentationNavigationKindV4.Arrangement,
            "eventInstrumentLibrary" => ProjectPresentationNavigationKindV4.EventInstrumentLibrary,
            "projectSettings" => ProjectPresentationNavigationKindV4.ProjectSettings,
            "diagnostics" => ProjectPresentationNavigationKindV4.Diagnostics,
            "conductorTrack" => ProjectPresentationNavigationKindV4.ConductorTrack,
            "segmentEditor" => ProjectPresentationNavigationKindV4.SegmentEditor,
            "eventInstrumentEditor" => ProjectPresentationNavigationKindV4.EventInstrumentEditor,
            "allTracks" => ProjectPresentationNavigationKindV4.AllTracks,
            _ => throw new InvalidDataException("Unknown workspace navigation kind.")
        };
        return new(kind, value.ObjectId?.ToDomain());
    }

    private static ProjectPresentationNavigationViewV4 FromDto(WorkspaceNavigationViewJsonV4 value) =>
        new(
            FromDto(value.Key),
            value.Page,
            value.SecondaryId?.ToDomain(),
            value.SearchText,
            value.PrimaryFilter,
            value.SecondaryFilter,
            value.TertiaryFilter,
            value.SortMode,
            value.StartTick,
            value.TickSpan,
            value.FirstLane,
            value.FirstRow,
            value.LaneHeight,
            value.FollowPlayback,
            value.LowerEditorVisible,
            value.LowerEditorHeight);

    internal static ProjectPresentationNavigationStateV4 ValidateNavigation(
        ProjectPresentationNavigationStateV4? value,
        MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        value ??= ProjectPresentationNavigationStateV4.Empty;
        if (value.Tabs is null || value.Views is null)
            throw new InvalidDataException("Workspace navigation sections cannot be null.");
        if (value.Tabs.Count is < 1 or > MaximumNavigationTabs)
            throw new InvalidDataException("Workspace navigation tab count is outside its bounded range.");
        if (value.Views.Count > MaximumNavigationViews)
            throw new InvalidDataException("Workspace navigation view count exceeds its bounded range.");

        HashSet<ProjectPresentationNavigationKeyV4> tabs = [];
        foreach (ProjectPresentationNavigationKeyV4 key in value.Tabs)
        {
            ValidateNavigationKey(key, project);
            if (!tabs.Add(key)) throw new InvalidDataException("Workspace navigation contains duplicate tabs.");
        }
        ValidateNavigationKey(value.ActiveTab, project);
        if (!tabs.Contains(value.ActiveTab))
            throw new InvalidDataException("The active workspace tab is not present in the tab list.");

        HashSet<ProjectPresentationNavigationKeyV4> views = [];
        foreach (ProjectPresentationNavigationViewV4 view in value.Views)
        {
            ValidateNavigationKey(view.Key, project);
            if (!views.Add(view.Key)) throw new InvalidDataException("Workspace navigation contains duplicate views.");
            ValidateNavigationView(view, project);
        }

        ProjectPresentationNavigationKeyV4 arrangement =
            new(ProjectPresentationNavigationKindV4.Arrangement);
        ProjectPresentationNavigationKeyV4[] canonicalTabs = new[] { arrangement }
            .Concat(value.Tabs.Where(key => key != arrangement))
            .ToArray();
        ProjectPresentationNavigationViewV4[] canonicalViews = value.Views
            .OrderBy(view => view.Key.Kind)
            .ThenBy(view => view.Key.ObjectId)
            .ToArray();
        return new(canonicalTabs, value.ActiveTab, canonicalViews);
    }

    private static void ValidateNavigationKey(
        ProjectPresentationNavigationKeyV4 key,
        MidoraProject project)
    {
        if (!Enum.IsDefined(key.Kind)) throw new InvalidDataException("Unknown workspace navigation kind.");
        bool objectKind = key.Kind is ProjectPresentationNavigationKindV4.SegmentEditor
            or ProjectPresentationNavigationKindV4.EventInstrumentEditor;
        if (!objectKind && key.ObjectId is not null)
            throw new InvalidDataException("A type-only workspace navigation key cannot have an object ID.");
        if (key.Kind == ProjectPresentationNavigationKindV4.SegmentEditor)
        {
            if (key.ObjectId is not MidoraId segmentId
                || (!project.Tracks.SelectMany(track => track.Segments).Any(segment => segment.Id == segmentId)
                    && !project.PureMidiTracks.SelectMany(track => track.Segments).Any(segment => segment.Id == segmentId)))
                throw new InvalidDataException("A remembered segment workspace no longer exists.");
        }
        else if (key.Kind == ProjectPresentationNavigationKindV4.EventInstrumentEditor)
        {
            if (key.ObjectId is not MidoraId instrumentId
                || !project.EventInstruments.Any(instrument => instrument.Id == instrumentId))
                throw new InvalidDataException("A remembered Event Instrument workspace no longer exists.");
        }
    }

    private static void ValidateNavigationView(
        ProjectPresentationNavigationViewV4 value,
        MidoraProject project)
    {
        if (value.Page is < 0) throw new InvalidDataException("Workspace navigation page cannot be negative.");
        if (value.PrimaryFilter is < 0 or > 4095
            || value.SecondaryFilter is < 0 or > 4095
            || value.TertiaryFilter is < 0 or > 4095
            || value.SortMode is < 0 or > 4095)
            throw new InvalidDataException("Workspace navigation filter is outside its bounded range.");
        if (value.StartTick is < 0)
            throw new InvalidDataException("Workspace navigation start tick cannot be negative.");
        if (value.TickSpan is <= 0 or > (1L << 50))
            throw new InvalidDataException("Workspace navigation tick span is outside its bounded range.");
        if (value.FirstLane is < 0 or > 127 || value.FirstRow is < 0)
            throw new InvalidDataException("Workspace navigation vertical position is outside its bounded range.");
        if (value.LaneHeight is { } laneHeight
            && (!double.IsFinite(laneHeight) || laneHeight <= 0 || laneHeight > 128))
            throw new InvalidDataException("Workspace navigation lane height is invalid.");
        if (value.LowerEditorHeight is { } lowerHeight
            && (!double.IsFinite(lowerHeight) || lowerHeight < 110 || lowerHeight > 720))
            throw new InvalidDataException("Workspace navigation lower-editor height is invalid.");
        if (value.SearchText is { Length: > MaximumNavigationTextLength })
            throw new InvalidDataException("Workspace navigation search text exceeds its bounded length.");
        if (value.SecondaryId is { } secondary)
        {
            if (secondary == default)
                throw new InvalidDataException("Workspace navigation secondary ID cannot be empty.");
            if (value.Key.Kind != ProjectPresentationNavigationKindV4.EventInstrumentEditor
                || value.Key.ObjectId is not MidoraId instrumentId
                || project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                    ?.SubVoices.Any(item => item.Id == secondary) != true)
                throw new InvalidDataException("Workspace navigation secondary owner is invalid.");
        }
    }

    private static bool TryDeserialize<T>(
        JsonElement parent,
        string property,
        JsonTypeInfo<T> typeInfo,
        out T? value)
    {
        value = default;
        if (!parent.TryGetProperty(property, out JsonElement element)) return false;
        try
        {
            value = JsonSerializer.Deserialize(element, typeInfo);
            return value is not null;
        }
        catch (JsonException) { return false; }
    }

    private static ProjectPresentationEditorProfileV4 FromDto(ProfileJsonV4 value) => new(
        FromDto(value.Owner),
        FromDto(value.Piano),
        FromDto(value.Event),
        value.TickSpan,
        value.KeyHeight,
        value.Tool,
        value.Shape,
        value.LanesVisible,
        value.LanesHeight,
        value.ListVisible,
        value.ListWidth);

    private static ProjectPresentationEditorLocalViewV4 FromDto(ViewJsonV4 value) => new(
        FromDto(value.Owner), value.StartTick, value.FirstLane, value.FirstRow);

    private static ProjectPresentationLaneMemoryV4 FromDto(LaneMemoryJsonV4 value) => new(
        FromDto(value.Owner),
        FromDto(value.Active),
        (value.Targets ?? throw new InvalidDataException("Lane targets are required.")).Select(item =>
            new ProjectPresentationLaneTargetV4(
                FromDto(item.Key), item.Hidden, item.Minimum, item.Maximum)).ToArray(),
        (value.ExplicitMidiTargets ?? throw new InvalidDataException("Explicit MIDI targets are required."))
            .Select(FromDto).ToArray());

    private static ProjectPresentationWorkspaceOwnerV4 FromDto(OwnerJsonV4 value)
    {
        ProjectPresentationWorkspaceOwnerKindV4 kind = value.Kind switch
        {
            "track" => ProjectPresentationWorkspaceOwnerKindV4.Track,
            "segment" => ProjectPresentationWorkspaceOwnerKindV4.Segment,
            "subVoice" => ProjectPresentationWorkspaceOwnerKindV4.SubVoice,
            _ => throw new InvalidDataException("Unknown presentation workspace owner kind.")
        };
        return new(kind, value.Id.ToDomain(), value.InstrumentId?.ToDomain());
    }

    private static ProjectPresentationEditorSettingsV4 FromDto(EditorSettingsJsonV4 value)
    {
        SubdivisionJsonV4 operation = value.Operation
            ?? throw new InvalidDataException("Editor operation subdivision is required.");
        return new(new(operation.Numerator, operation.Denominator, operation.Label, operation.IsBar),
            value.Snap, value.Grid, value.Length, value.Velocity);
    }

    private static ProjectPresentationLaneKeyV4 FromDto(LaneKeyJsonV4 value) => new(
        value.Type, value.Parameter?.ToDomain(), value.Kind, value.Number);

    private static ProjectPresentationMonitoringV4 FromDto(MonitoringJsonV4 value) => new(
        ToIds(value.MutedTrackIds), ToIds(value.SoloTrackIds),
        ToIds(value.MutedGroupIds), ToIds(value.SoloGroupIds));

    private static MidoraId[] ToIds(StableIdJsonV1[]? values) =>
        (values ?? throw new InvalidDataException("Monitoring ID arrays are required."))
            .Select(value => value.ToDomain()).ToArray();

    private static ProjectPresentationEditorProfileV4 ValidateProfile(
        ProjectPresentationEditorProfileV4 value,
        MidoraProject project,
        IReadOnlySet<MidoraId> trackIds,
        IReadOnlySet<MidoraId> segmentIds,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        ISet<ProjectPresentationWorkspaceOwnerV4> owners)
    {
        ValidateOwner(value.Owner, trackIds, segmentIds, instruments, requireProfile: true);
        if (!owners.Add(value.Owner)) throw new InvalidDataException("Duplicate workspace profile owner.");
        ValidateSettings(value.Piano); ValidateSettings(value.Event);
        if (value.TickSpan <= 0 || !double.IsFinite(value.KeyHeight) || value.KeyHeight is < .25 or > 128
            || value.Tool is < 0 or > 3 || value.Shape is < 0 or > 2
            || !double.IsFinite(value.LanesHeight) || value.LanesHeight is < 110 or > 520
            || !double.IsFinite(value.ListWidth) || value.ListWidth is < 240 or > 700)
            throw new InvalidDataException("Workspace profile contains an out-of-range value.");
        return value;
    }

    private static ProjectPresentationEditorLocalViewV4 ValidateView(
        ProjectPresentationEditorLocalViewV4 value,
        IReadOnlySet<MidoraId> trackIds,
        IReadOnlySet<MidoraId> segmentIds,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        ISet<ProjectPresentationWorkspaceOwnerV4> owners)
    {
        ValidateOwner(value.Owner, trackIds, segmentIds, instruments, requireProfile: false);
        if (!owners.Add(value.Owner) || value.StartTick < 0 || value.FirstLane is < 0 or > 127 || value.FirstRow < 0)
            throw new InvalidDataException("Workspace local view is invalid or duplicated.");
        return value;
    }

    private static ProjectPresentationLaneMemoryV4 ValidateLanes(
        ProjectPresentationLaneMemoryV4 value,
        IReadOnlySet<MidoraId> trackIds,
        IReadOnlySet<MidoraId> segmentIds,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        ISet<ProjectPresentationWorkspaceOwnerV4> owners)
    {
        ValidateOwner(value.Owner, trackIds, segmentIds, instruments, requireProfile: false);
        if (value.Owner.Kind == ProjectPresentationWorkspaceOwnerKindV4.Track
            || !owners.Add(value.Owner)
            || value.Targets is null || value.ExplicitMidiTargets is null
            || value.Targets.Count + value.ExplicitMidiTargets.Count > MaximumLaneTargets)
            throw new InvalidDataException("Workspace lane memory is invalid or duplicated.");
        ValidateLaneKey(value.Active);
        HashSet<ProjectPresentationLaneKeyV4> targets = [];
        foreach (ProjectPresentationLaneTargetV4 target in value.Targets)
        {
            ValidateLaneKey(target.Key);
            if (!targets.Add(target.Key) || !double.IsFinite(target.Minimum) || !double.IsFinite(target.Maximum)
                || target.Minimum < 0 || target.Maximum > 1 || target.Minimum >= target.Maximum)
                throw new InvalidDataException("Workspace lane target is invalid or duplicated.");
        }
        HashSet<ProjectPresentationLaneKeyV4> explicitTargets = [];
        foreach (ProjectPresentationLaneKeyV4 target in value.ExplicitMidiTargets)
        {
            ValidateLaneKey(target);
            if (target.Type != 3 || !explicitTargets.Add(target))
                throw new InvalidDataException("Workspace explicit MIDI lane target is invalid or duplicated.");
        }
        return value;
    }

    private static void ValidateOwner(
        ProjectPresentationWorkspaceOwnerV4 owner,
        IReadOnlySet<MidoraId> trackIds,
        IReadOnlySet<MidoraId> segmentIds,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        bool requireProfile)
    {
        if (!Enum.IsDefined(owner.Kind) || owner.Id.Value <= 0)
            throw new InvalidDataException("Workspace owner identity is invalid.");
        switch (owner.Kind)
        {
            case ProjectPresentationWorkspaceOwnerKindV4.Track:
                if (owner.InstrumentId is not null || !trackIds.Contains(owner.Id))
                    throw new InvalidDataException("Workspace Track owner is missing.");
                break;
            case ProjectPresentationWorkspaceOwnerKindV4.Segment:
                if (owner.InstrumentId is not null || !segmentIds.Contains(owner.Id))
                    throw new InvalidDataException("Workspace Segment owner is missing.");
                if (requireProfile) throw new InvalidDataException("Segments do not own shared profiles.");
                break;
            case ProjectPresentationWorkspaceOwnerKindV4.SubVoice:
                if (owner.InstrumentId is not { } instrumentId
                    || !instruments.TryGetValue(instrumentId, out EventInstrument? instrument)
                    || !instrument.SubVoices.Any(value => value.Id == owner.Id))
                    throw new InvalidDataException("Workspace SubVoice owner is missing.");
                break;
        }
    }

    private static void ValidateSettings(ProjectPresentationEditorSettingsV4 value)
    {
        if (value.Length <= 0 || value.Velocity is < 1 or > 127
            || value.Operation.Numerator <= 0 || value.Operation.Denominator <= 0
            || value.Operation.Label is null || value.Operation.Label.Length > MaximumLabelLength)
            throw new InvalidDataException("Workspace editor settings are invalid.");
    }

    private static void ValidateLaneKey(ProjectPresentationLaneKeyV4 value)
    {
        if (value.Parameter is { Value: <= 0 } || value.Number is < 0 or > 16_383)
            throw new InvalidDataException("Workspace lane key is invalid.");
        bool valid = value.Type switch
        {
            0 or 1 or 4 => value.Parameter is null && value.Kind == 0 && value.Number == 0,
            2 => value.Parameter is not null && value.Kind == 0 && value.Number == 0,
            3 => value.Parameter is null
                && Enum.IsDefined(typeof(DirectMidiChannelEventKind), value.Kind)
                && value.Number <= 127,
            5 => value.Parameter is null
                && Enum.IsDefined(typeof(MidiValueKind), value.Kind),
            _ => false
        };
        if (!valid) throw new InvalidDataException("Workspace lane key is invalid.");
    }

    private static ProjectPresentationMonitoringV4 ValidateMonitoring(
        ProjectPresentationMonitoringV4 value,
        MidoraProject project)
    {
        IReadOnlySet<MidoraId> tracks = project.ArrangementTracks.Select(item => item.TrackId).ToHashSet();
        IReadOnlySet<MidoraId> groups = project.EventInstrumentUsages.Select(item => item.Id)
            .Concat(project.MidiChannelRoots.Select(item => item.Id)).ToHashSet();
        return new(
            ValidateIds(value.MutedTrackIds, tracks, "muted Track"),
            ValidateIds(value.SoloTrackIds, tracks, "solo Track"),
            ValidateIds(value.MutedGroupIds, groups, "muted group"),
            ValidateIds(value.SoloGroupIds, groups, "solo group"));
    }

    private static MidoraId[] ValidateIds(
        IReadOnlyList<MidoraId>? values,
        IReadOnlySet<MidoraId> valid,
        string label)
    {
        if (values is null || values.Count > MaximumSectionEntries)
            throw new InvalidDataException($"The {label} list is invalid.");
        HashSet<MidoraId> result = [];
        foreach (MidoraId id in values)
            if (!valid.Contains(id) || !result.Add(id))
                throw new InvalidDataException($"The {label} list contains an invalid or duplicate ID.");
        return result.OrderBy(id => id).ToArray();
    }

    private static bool TryReadMode(JsonElement root, out ProjectPresentationAllTracksModeV3 mode)
    {
        mode = ProjectPresentationAllTracksModeV3.Raw;
        if (!root.TryGetProperty("allTracksMode", out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
            return false;
        switch (value.GetString())
        {
            case "raw":
                mode = ProjectPresentationAllTracksModeV3.Raw;
                return true;
            case "compiled":
                mode = ProjectPresentationAllTracksModeV3.Compiled;
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadOnionPresets(
        JsonElement root,
        MidoraProject project,
        out TrackOnionPresetV3[] tracks,
        out SubVoiceOnionPresetV3[] voices)
    {
        tracks = []; voices = [];
        try
        {
            if (!root.TryGetProperty("trackOnionPresets", out JsonElement trackElement)
                || !root.TryGetProperty("subVoiceOnionPresets", out JsonElement voiceElement)) return false;
            TrackOnionPresetJsonV2[] trackDtos = JsonSerializer.Deserialize(
                trackElement,
                ProjectPresentationJsonContextV4.Default.TrackOnionPresetJsonV2Array)
                ?? throw new InvalidDataException("Track onion presets are required.");
            SubVoiceOnionPresetJsonV2[] voiceDtos = JsonSerializer.Deserialize(
                voiceElement,
                ProjectPresentationJsonContextV4.Default.SubVoiceOnionPresetJsonV2Array)
                ?? throw new InvalidDataException("SubVoice onion presets are required.");
            tracks = trackDtos.Select(value => new TrackOnionPresetV3(
                value.TargetTrackId.ToDomain(), value.Enabled, value.Opacity,
                (value.SourceTrackIds ?? throw new InvalidDataException("Track onion sources are required."))
                    .Select(id => id.ToDomain()).ToArray(), ProjectPresentationCodecV2.ParseModeForCodec(value.SourceMode)))
                .ToArray();
            voices = voiceDtos.Select(value => new SubVoiceOnionPresetV3(
                value.EventInstrumentId.ToDomain(), value.TargetSubVoiceId.ToDomain(), value.Enabled, value.Opacity,
                (value.SourceSubVoiceIds ?? throw new InvalidDataException("SubVoice onion sources are required."))
                    .Select(id => id.ToDomain()).ToArray(), ProjectPresentationCodecV2.ParseModeForCodec(value.SourceMode)))
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
        {
            tracks = []; voices = []; return false;
        }
    }

    private static void EnsureKnownRootProperties(JsonElement root, int schemaVersion)
    {
        string[] known = schemaVersion == SchemaVersion
            ? ["schemaVersion", "allTracksMode", "trackOnionPresets", "subVoiceOnionPresets", "workspaceState", "workspaceNavigation"]
            : ["schemaVersion", "allTracksMode", "trackOnionPresets", "subVoiceOnionPresets", "workspaceState"];
        EnsureKnownProperties(root, known, "project-presentation.json");
    }

    private static void EnsureKnownWorkspaceProperties(JsonElement element) => EnsureKnownProperties(
        element, ["profiles", "views", "lanes", "monitoring"], "workspaceState");

    private static void EnsureKnownNavigationProperties(JsonElement element) => EnsureKnownProperties(
        element, ["tabs", "activeTab", "views"], "workspaceNavigation");

    private static void EnsureKnownProperties(JsonElement element, IReadOnlyCollection<string> known, string scope)
    {
        foreach (JsonProperty property in element.EnumerateObject())
            if (!known.Contains(property.Name))
                throw new InvalidDataException($"Unknown presentation property '{property.Name}' in {scope}.");
    }

    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result)
            ? result
            : throw new InvalidDataException($"Presentation property '{name}' is missing or invalid.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProjectPresentationJsonV4
{
    [JsonPropertyOrder(0)] public required int SchemaVersion { get; init; }
    [JsonPropertyOrder(1)] public required string AllTracksMode { get; init; }
    [JsonPropertyOrder(2)] public required TrackOnionPresetJsonV2[] TrackOnionPresets { get; init; }
    [JsonPropertyOrder(3)] public required SubVoiceOnionPresetJsonV2[] SubVoiceOnionPresets { get; init; }
    [JsonPropertyOrder(4)] public required WorkspaceStateJsonV4 WorkspaceState { get; init; }
    [JsonPropertyOrder(5)] public required WorkspaceNavigationJsonV4 WorkspaceNavigation { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class WorkspaceNavigationJsonV4
{
    [JsonPropertyOrder(0)] public required NavigationKeyJsonV4[] Tabs { get; init; }
    [JsonPropertyOrder(1)] public required NavigationKeyJsonV4 ActiveTab { get; init; }
    [JsonPropertyOrder(2)] public required WorkspaceNavigationViewJsonV4[] Views { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class NavigationKeyJsonV4
{
    [JsonPropertyOrder(0)] public required string Kind { get; init; }
    [JsonPropertyOrder(1)] public StableIdJsonV1? ObjectId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class WorkspaceNavigationViewJsonV4
{
    [JsonPropertyOrder(0)] public required NavigationKeyJsonV4 Key { get; init; }
    [JsonPropertyOrder(1)] public int? Page { get; init; }
    [JsonPropertyOrder(2)] public StableIdJsonV1? SecondaryId { get; init; }
    [JsonPropertyOrder(3)] public string? SearchText { get; init; }
    [JsonPropertyOrder(4)] public int? PrimaryFilter { get; init; }
    [JsonPropertyOrder(5)] public int? SecondaryFilter { get; init; }
    [JsonPropertyOrder(6)] public int? TertiaryFilter { get; init; }
    [JsonPropertyOrder(7)] public int? SortMode { get; init; }
    [JsonPropertyOrder(8)] public long? StartTick { get; init; }
    [JsonPropertyOrder(9)] public long? TickSpan { get; init; }
    [JsonPropertyOrder(10)] public int? FirstLane { get; init; }
    [JsonPropertyOrder(11)] public int? FirstRow { get; init; }
    [JsonPropertyOrder(12)] public double? LaneHeight { get; init; }
    [JsonPropertyOrder(13)] public bool? FollowPlayback { get; init; }
    [JsonPropertyOrder(14)] public bool? LowerEditorVisible { get; init; }
    [JsonPropertyOrder(15)] public double? LowerEditorHeight { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class WorkspaceStateJsonV4
{
    [JsonPropertyOrder(0)] public required ProfileJsonV4[] Profiles { get; init; }
    [JsonPropertyOrder(1)] public required ViewJsonV4[] Views { get; init; }
    [JsonPropertyOrder(2)] public required LaneMemoryJsonV4[] Lanes { get; init; }
    [JsonPropertyOrder(3)] public required MonitoringJsonV4 Monitoring { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class OwnerJsonV4
{
    [JsonPropertyOrder(0)] public required string Kind { get; init; }
    [JsonPropertyOrder(1)] public required StableIdJsonV1 Id { get; init; }
    [JsonPropertyOrder(2)] public StableIdJsonV1? InstrumentId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SubdivisionJsonV4
{
    [JsonPropertyOrder(0)] public required int Numerator { get; init; }
    [JsonPropertyOrder(1)] public required int Denominator { get; init; }
    [JsonPropertyOrder(2)] public required string Label { get; init; }
    [JsonPropertyOrder(3)] public bool IsBar { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class EditorSettingsJsonV4
{
    [JsonPropertyOrder(0)] public required SubdivisionJsonV4 Operation { get; init; }
    [JsonPropertyOrder(1)] public required bool Snap { get; init; }
    [JsonPropertyOrder(2)] public required bool Grid { get; init; }
    [JsonPropertyOrder(3)] public required long Length { get; init; }
    [JsonPropertyOrder(4)] public required int Velocity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ProfileJsonV4
{
    [JsonPropertyOrder(0)] public required OwnerJsonV4 Owner { get; init; }
    [JsonPropertyOrder(1)] public required EditorSettingsJsonV4 Piano { get; init; }
    [JsonPropertyOrder(2)] public required EditorSettingsJsonV4 Event { get; init; }
    [JsonPropertyOrder(3)] public required long TickSpan { get; init; }
    [JsonPropertyOrder(4)] public required double KeyHeight { get; init; }
    [JsonPropertyOrder(5)] public required int Tool { get; init; }
    [JsonPropertyOrder(6)] public required int Shape { get; init; }
    [JsonPropertyOrder(7)] public required bool LanesVisible { get; init; }
    [JsonPropertyOrder(8)] public required double LanesHeight { get; init; }
    [JsonPropertyOrder(9)] public required bool ListVisible { get; init; }
    [JsonPropertyOrder(10)] public required double ListWidth { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ViewJsonV4
{
    [JsonPropertyOrder(0)] public required OwnerJsonV4 Owner { get; init; }
    [JsonPropertyOrder(1)] public required long StartTick { get; init; }
    [JsonPropertyOrder(2)] public required int FirstLane { get; init; }
    [JsonPropertyOrder(3)] public required int FirstRow { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class LaneKeyJsonV4
{
    [JsonPropertyOrder(0)] public required int Type { get; init; }
    [JsonPropertyOrder(1)] public StableIdJsonV1? Parameter { get; init; }
    [JsonPropertyOrder(2)] public required int Kind { get; init; }
    [JsonPropertyOrder(3)] public required int Number { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class LaneTargetJsonV4
{
    [JsonPropertyOrder(0)] public required LaneKeyJsonV4 Key { get; init; }
    [JsonPropertyOrder(1)] public required bool Hidden { get; init; }
    [JsonPropertyOrder(2)] public required double Minimum { get; init; }
    [JsonPropertyOrder(3)] public required double Maximum { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class LaneMemoryJsonV4
{
    [JsonPropertyOrder(0)] public required OwnerJsonV4 Owner { get; init; }
    [JsonPropertyOrder(1)] public required LaneKeyJsonV4 Active { get; init; }
    [JsonPropertyOrder(2)] public required LaneTargetJsonV4[] Targets { get; init; }
    [JsonPropertyOrder(3)] public required LaneKeyJsonV4[] ExplicitMidiTargets { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class MonitoringJsonV4
{
    [JsonPropertyOrder(0)] public required StableIdJsonV1[] MutedTrackIds { get; init; }
    [JsonPropertyOrder(1)] public required StableIdJsonV1[] SoloTrackIds { get; init; }
    [JsonPropertyOrder(2)] public required StableIdJsonV1[] MutedGroupIds { get; init; }
    [JsonPropertyOrder(3)] public required StableIdJsonV1[] SoloGroupIds { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ProjectPresentationJsonV4))]
[JsonSerializable(typeof(ProfileJsonV4[]))]
[JsonSerializable(typeof(ViewJsonV4[]))]
[JsonSerializable(typeof(LaneMemoryJsonV4[]))]
[JsonSerializable(typeof(MonitoringJsonV4))]
[JsonSerializable(typeof(TrackOnionPresetJsonV2[]))]
[JsonSerializable(typeof(SubVoiceOnionPresetJsonV2[]))]
[JsonSerializable(typeof(WorkspaceNavigationJsonV4))]
[JsonSerializable(typeof(NavigationKeyJsonV4))]
[JsonSerializable(typeof(NavigationKeyJsonV4[]))]
[JsonSerializable(typeof(WorkspaceNavigationViewJsonV4))]
[JsonSerializable(typeof(WorkspaceNavigationViewJsonV4[]))]
internal sealed partial class ProjectPresentationJsonContextV4 : JsonSerializerContext;
