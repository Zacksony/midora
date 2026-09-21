using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class ProjectPresentationSchema2Tests
{
    [Theory]
    [InlineData(OnionSourceMode.Custom)]
    [InlineData(OnionSourceMode.Previous)]
    [InlineData(OnionSourceMode.Next)]
    public async Task ModesAndCustomSourcesRoundTripDeterministically(OnionSourceMode mode)
    {
        using var project = CreateProject();
        var state = State(project, mode);
        byte[] bytes = ProjectPresentationCodecV3.Serialize(state, project);
        Assert.Equal(4, JsonNode.Parse(bytes)!["schemaVersion"]!.GetValue<int>());
        var parsed = ProjectPresentationCodecV3.Parse(bytes, project, 4);
        Assert.Equal(bytes, ProjectPresentationCodecV3.Serialize(parsed, project));
        AssertState(state, parsed, mode);
        using var directory = new TestDirectory();
        var packages = new MidoraProjectPackageV1("1.0.0-dev-test");
        string first = Path.Combine(directory.Path, "first.midora"), second = Path.Combine(directory.Path, "second.midora");
        await packages.SaveCopyAsync(project, state, first);
        await using var opened = await packages.OpenAsync(first);
        Assert.False(opened.IsModified); Assert.False(opened.IsPresentationModified);
        AssertState(state, opened.Presentation, mode);
        await packages.SaveCopyAsync(opened.Project, opened.Presentation, second);
        await using var copy = await packages.OpenAsync(second);
        AssertState(state, copy.Presentation, mode);
    }

    [Fact]
    public async Task Schema1IsReadAsCustomAndSavedAsSchema4WithoutChangingMusic()
    {
        using var project = CreateProject();
        var state = State(project, OnionSourceMode.Next);
        JsonObject legacy = JsonNode.Parse(ProjectPresentationCodecV3.Serialize(state, project))!.AsObject();
        legacy["schemaVersion"] = 1;
        legacy.Remove("workspaceState");
        legacy.Remove("workspaceNavigation");
        foreach (string name in new[] { "trackOnionPresets", "subVoiceOnionPresets" })
            foreach (var preset in legacy[name]!.AsArray()) preset!.AsObject().Remove("sourceMode");
        byte[] bytes = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        AssertState(state, ProjectPresentationCodecV3.Parse(bytes, project, 1), OnionSourceMode.Custom);
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "legacy.midora");
        var packages = new MidoraProjectPackageV1("1.0.0-dev-test");
        await packages.SaveCopyAsync(project, state, path);
        RewritePresentation(path, bytes, 1);
        await using var opened = await packages.OpenAsync(path);
        Assert.False(opened.IsModified); Assert.False(opened.RequiresFormatUpgrade);
        AssertState(state, opened.Presentation, OnionSourceMode.Custom);
        string upgraded = Path.Combine(directory.Path, "new.midora");
        await packages.SaveCopyAsync(opened.Project, opened.Presentation, upgraded);
        using var archive = ZipFile.OpenRead(upgraded);
        Assert.Equal(4, JsonNode.Parse(ReadEntry(archive, MidoraPackagePathsV1.ProjectPresentation))!["schemaVersion"]!.GetValue<int>());
        Assert.Equal(project.Tracks.Select(t => t.Id), opened.Project.Tracks.Select(t => t.Id));
        Assert.Empty(opened.Diagnostics);
    }

    [Theory]
    [InlineData("future-version")]
    [InlineData("mismatched-version")]
    [InlineData("unknown-mode")]
    [InlineData("missing-mode")]
    [InlineData("null-mode")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-mode")]
    [InlineData("string-version")]
    [InlineData("legacy-unknown-field")]
    public async Task InvalidOrUnsupportedPresentationDoesNotBlockMusic(string corruption)
    {
        using var project = CreateProject();
        var state = State(project, OnionSourceMode.Previous);
        JsonObject payload = JsonNode.Parse(ProjectPresentationCodecV3.Serialize(state, project))!.AsObject();
        JsonObject preset = payload["trackOnionPresets"]![0]!.AsObject();
        int declared = 4;
        switch (corruption)
        {
            case "future-version": payload["schemaVersion"] = declared = 99; break;
            case "mismatched-version": declared = 1; break;
            case "unknown-mode": preset["sourceMode"] = "random"; break;
            case "missing-mode": preset.Remove("sourceMode"); break;
            case "null-mode": preset["sourceMode"] = null; break;
            case "unknown-field": preset["unsupported"] = true; break;
            case "string-version": payload["schemaVersion"] = "4"; break;
            case "legacy-unknown-field": payload["schemaVersion"] = declared = 1; payload.Remove("workspaceState"); payload.Remove("workspaceNavigation"); break;
        }
        string json = payload.ToJsonString();
        if (corruption == "duplicate-mode") json = json.Replace("\"sourceMode\":\"previous\"", "\"sourceMode\":\"previous\",\"sourceMode\":\"custom\"");
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "recover.midora");
        var packages = new MidoraProjectPackageV1("1.0.0-dev-test");
        await packages.SaveCopyAsync(project, state, path);
        RewritePresentation(path, Encoding.UTF8.GetBytes(json), declared);
        await using var opened = await packages.OpenAsync(path);
        Assert.False(opened.IsModified); Assert.True(opened.IsPresentationModified);
        Assert.Empty(opened.Presentation.TrackOnionPresets);
        Assert.Empty(opened.Presentation.SubVoiceOnionPresets);
        Assert.Empty(opened.Presentation.WorkspaceState!.Profiles);
        Assert.Equal(ProjectPresentationNavigationKindV4.Arrangement,
            opened.Presentation.Navigation!.ActiveTab.Kind);
        Assert.Equal(project.Tracks.Select(t => t.Id), opened.Project.Tracks.Select(t => t.Id));
        string expectedDiagnostic = corruption is "null-mode" or "unknown-mode" or "missing-mode" or "unknown-field"
            ? "MIDORA-PERSIST-PRESENTATION-SECTION-RECOVERED"
            : "MIDORA-PERSIST-PRESENTATION-RECOVERED";
        Assert.Equal(expectedDiagnostic, Assert.Single(opened.Diagnostics).Code);
    }

    [Fact]
    public void WriterRejectsUnknownModes()
    {
        using var project = CreateProject();
        Assert.Throws<InvalidDataException>(() => ProjectPresentationCodecV3.Serialize(State(project, (OnionSourceMode)99), project));
    }

    [Fact]
    public void Schema3RemainsReadableAndRoundTripsWorkspaceAndMonitoringState()
    {
        using var project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        EventInstrumentUsage usage = Assert.Single(project.EventInstrumentUsages);
        ProjectPresentationEditorSettingsV4 settings = new(
            new(1, 4, "1/4"), Snap: true, Grid: false, Length: 192, Velocity: 96);
        ProjectPresentationWorkspaceStateV4 workspace = new(
            [new(
                new(ProjectPresentationWorkspaceOwnerKindV4.Track, track.Id),
                settings,
                settings with { Snap = false },
                TickSpan: 1024,
                KeyHeight: 5,
                Tool: 1,
                Shape: 0,
                LanesVisible: true,
                LanesHeight: 220,
                ListVisible: false,
                ListWidth: 300)],
            [],
            [],
            new(
                [track.Id],
                [],
                [usage.Id],
                []));
        ProjectPresentationStateV3 source = State(project, OnionSourceMode.Custom) with
        {
            WorkspaceState = workspace
        };

        JsonObject legacy = JsonNode.Parse(ProjectPresentationCodecV3.Serialize(source, project))!.AsObject();
        legacy["schemaVersion"] = 3;
        legacy.Remove("workspaceNavigation");
        byte[] bytes = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        ProjectPresentationStateV3 restored = ProjectPresentationCodecV3.Parse(bytes, project, 3);
        ProjectPresentationEditorProfileV4 profile = Assert.Single(restored.WorkspaceState!.Profiles);
        Assert.Equal(track.Id, profile.Owner.Id);
        Assert.True(profile.Piano.Snap);
        Assert.False(profile.Event.Snap);
        Assert.Equal([track.Id], restored.WorkspaceState!.Monitoring.MutedTrackIds);
        Assert.Equal([usage.Id], restored.WorkspaceState!.Monitoring.MutedGroupIds);
        AssertState(source, restored, OnionSourceMode.Custom);
    }

    [Fact]
    public void Schema4ReadsWorkspaceStateAndNavigationTogether()
    {
        using var project = CreateProject();
        LogicalTrack track = project.Tracks[0];
        ProjectPresentationEditorSettingsV4 settings = new(
            new(1, 4, "1/4"), Snap: true, Grid: true, Length: 192, Velocity: 100);
        ProjectPresentationWorkspaceStateV4 workspace = new(
            [new(
                new(ProjectPresentationWorkspaceOwnerKindV4.Track, track.Id),
                settings,
                settings,
                TickSpan: 2048,
                KeyHeight: 6,
                Tool: 0,
                Shape: 0,
                LanesVisible: true,
                LanesHeight: 180,
                ListVisible: false,
                ListWidth: 300)],
            [], [], ProjectPresentationMonitoringV4.Empty);
        ProjectPresentationNavigationKeyV4 arrangement = new(ProjectPresentationNavigationKindV4.Arrangement);
        ProjectPresentationStateV3 source = State(project, OnionSourceMode.Custom) with
        {
            WorkspaceState = workspace,
            Navigation = new(
                [arrangement],
                arrangement,
                [new(arrangement, StartTick: 384, TickSpan: 4096, FirstLane: 2, LaneHeight: 14)])
        };

        byte[] bytes = ProjectPresentationCodecV3.Serialize(source, project);
        ProjectPresentationStateV3 restored = ProjectPresentationCodecV3.Parse(bytes, project, 4);

        Assert.Single(restored.WorkspaceState!.Profiles);
        Assert.Equal(track.Id, restored.WorkspaceState.Profiles[0].Owner.Id);
        Assert.Equal(arrangement, restored.Navigation!.ActiveTab);
        ProjectPresentationNavigationViewV4 view = Assert.Single(restored.Navigation.Views);
        Assert.Equal(384, view.StartTick);
        Assert.Equal(14, view.LaneHeight);
    }

    [Fact]
    public void InvalidNavigationOwnerIsDroppedWithoutDiscardingValidTabs()
    {
        using var project = CreateProject();
        ProjectPresentationNavigationKeyV4 arrangement =
            new(ProjectPresentationNavigationKindV4.Arrangement);
        ProjectPresentationNavigationKeyV4 instrument =
            new(ProjectPresentationNavigationKindV4.EventInstrumentEditor, project.EventInstruments[0].Id);
        ProjectPresentationStateV3 source = State(project, OnionSourceMode.Custom) with
        {
            Navigation = new(
                [arrangement, instrument],
                instrument,
                [new(arrangement, StartTick: 96, TickSpan: 192), new(instrument, Page: 1)])
        };
        JsonObject payload = JsonNode.Parse(ProjectPresentationCodecV3.Serialize(source, project))!.AsObject();
        JsonObject invalid = new()
        {
            ["kind"] = "segmentEditor",
            ["objectId"] = 987654321L
        };
        payload["workspaceNavigation"]!["tabs"]!.AsArray().Add(invalid);
        payload["workspaceNavigation"]!["activeTab"] = invalid.DeepClone();
        payload["workspaceNavigation"]!["views"]!.AsArray().Add(new JsonObject
        {
            ["key"] = invalid.DeepClone(),
            ["startTick"] = 96,
            ["tickSpan"] = 192
        });

        ProjectPresentationParseResultV4 result = ProjectPresentationCodecV3.ParseWithRecovery(
            Encoding.UTF8.GetBytes(payload.ToJsonString()), project, 4);

        Assert.True(result.Recovered);
        Assert.Contains("workspaceNavigation.tabs", result.RecoveredSections);
        Assert.Contains("workspaceNavigation.activeTab", result.RecoveredSections);
        Assert.Contains("workspaceNavigation.views", result.RecoveredSections);
        Assert.Equal([arrangement, instrument], result.State.Navigation!.Tabs);
        Assert.Equal(arrangement, result.State.Navigation.ActiveTab);
        Assert.Equal(2, result.State.Navigation.Views.Count);
    }

    [Fact]
    public void DamagedWorkspaceSectionIsRecoveredWithoutDiscardingOnionState()
    {
        using var project = CreateProject();
        ProjectPresentationStateV3 source = State(project, OnionSourceMode.Next) with
        {
            WorkspaceState = new(
                [new(
                    new(ProjectPresentationWorkspaceOwnerKindV4.Track, project.Tracks[0].Id),
                    new(new(1, 4, "1/4"), true, true, 192, 100),
                    new(new(1, 4, "1/4"), true, true, 192, 100),
                    1024, 4, 0, 0, true, 220, false, 300)],
                [], [], ProjectPresentationMonitoringV4.Empty)
        };
        JsonObject payload = JsonNode.Parse(ProjectPresentationCodecV3.Serialize(source, project))!.AsObject();
        payload["schemaVersion"] = 3;
        payload.Remove("workspaceNavigation");
        payload["workspaceState"]!["profiles"]![0]!["owner"]!["id"] = long.MaxValue;
        ProjectPresentationParseResultV4 result = ProjectPresentationCodecV3.ParseWithRecovery(
            Encoding.UTF8.GetBytes(payload.ToJsonString()), project, 3);
        Assert.True(result.Recovered);
        Assert.Contains("workspaceState", result.RecoveredSections);
        Assert.Equal(source.TrackOnionPresets.Select(value => value.TargetTrackId),
            result.State.TrackOnionPresets.Select(value => value.TargetTrackId));
        Assert.Empty(result.State.WorkspaceState!.Profiles);
    }

    [Fact]
    public async Task SavePreparationOmitsOnlyInvalidWorkspaceSectionAndReportsIt()
    {
        using var project = CreateProject();
        ProjectPresentationStateV3 source = State(project, OnionSourceMode.Next) with
        {
            WorkspaceState = new(
                [new(
                    new(ProjectPresentationWorkspaceOwnerKindV4.Segment, project.Tracks[0].Id),
                    new(new(1, 4, "1/4"), true, true, 192, 100),
                    new(new(1, 4, "1/4"), true, true, 192, 100),
                    1024, 4, 0, 0, true, 220, false, 300)],
                [], [], ProjectPresentationMonitoringV4.Empty)
        };

        ProjectPresentationSerializationResultV4 prepared =
            ProjectPresentationCodecV4.PrepareForSave(source, project);
        Assert.Contains("workspaceState.profiles", prepared.OmittedSections);
        Assert.Empty(prepared.State.WorkspaceState!.Profiles);
        Assert.NotEmpty(prepared.State.TrackOnionPresets);

        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "partial-presentation.midora");
        MidoraProjectSaveResultV1 saved = await new MidoraProjectPackageV1("1.0.0-dev-test")
            .SaveCopyAsync(project, source, path);
        Assert.Contains(saved.Diagnostics,
            diagnostic => diagnostic.Code == "MIDORA-PERSIST-PRESENTATION-SECTION-OMITTED"
                && diagnostic.Message.Contains("workspaceState.profiles", StringComparison.Ordinal));
        Assert.Contains("workspaceState.profiles", saved.OmittedPresentationSections ?? []);
        await using var opened = await new MidoraProjectPackageV1("1.0.0-dev-test").OpenAsync(path);
        Assert.False(opened.IsModified);
        Assert.Empty(opened.Diagnostics);
        Assert.Empty(opened.Presentation.WorkspaceState!.Profiles);
        Assert.NotEmpty(opened.Presentation.TrackOnionPresets);
    }

    [Fact]
    public async Task OnionSaveWithAnUnboundTrackWritesTheRequiredNullRoutingMember()
    {
        using var project = CreateProject();
        project.Tracks[0].EventInstrumentUsageId = null;
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "unbound.midora");
        var packages = new MidoraProjectPackageV1("1.0.0-dev-test");
        await packages.SaveCopyAsync(project, State(project, OnionSourceMode.Next), path);
        await using var opened = await packages.OpenAsync(path);
        Assert.False(opened.IsModified); Assert.Empty(opened.Diagnostics);
        Assert.Null(opened.Project.Tracks[0].EventInstrumentUsageId);
        using var archive = ZipFile.OpenRead(path);
        var index = JsonNode.Parse(ReadEntry(archive, "project.json"))!["arrangementTracks"]![0]!.AsObject();
        Assert.True(index.ContainsKey("sharedGroupId")); Assert.Null(index["sharedGroupId"]);
        Assert.Equal(OnionSourceMode.Next, Assert.Single(opened.Presentation.TrackOnionPresets).SourceMode);
    }

    private static void AssertState(ProjectPresentationStateV3 expected, ProjectPresentationStateV3 actual, OnionSourceMode mode)
    {
        var track = Assert.Single(actual.TrackOnionPresets);
        var voice = Assert.Single(actual.SubVoiceOnionPresets);
        Assert.Equal(mode, track.SourceMode); Assert.Equal(mode, voice.SourceMode);
        Assert.Equal(expected.TrackOnionPresets[0] with { SourceMode = mode, SourceTrackIds = track.SourceTrackIds }, track);
        Assert.Equal(expected.SubVoiceOnionPresets[0] with { SourceMode = mode, SourceSubVoiceIds = voice.SourceSubVoiceIds }, voice);
        Assert.Equal(expected.TrackOnionPresets[0].SourceTrackIds, track.SourceTrackIds);
        Assert.Equal(expected.SubVoiceOnionPresets[0].SourceSubVoiceIds, voice.SourceSubVoiceIds);
    }

    private static MidoraProject CreateProject()
    {
        var project = new MidoraProject(192);
        var instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.SubVoices.Add(new SubVoice(project) { Name = "Second" });
        var usage = new EventInstrumentUsage(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        foreach (string name in new[] { "Target", "Custom Source" })
        {
            var track = new LogicalTrack(project) { Name = name, EventInstrumentUsageId = usage.Id };
            project.Tracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        }
        return project;
    }

    private static ProjectPresentationStateV3 State(MidoraProject project, OnionSourceMode mode) => new(
        ProjectPresentationAllTracksModeV3.Compiled,
        [new(project.Tracks[0].Id, true, .42, [project.Tracks[1].Id], mode)],
        [new(project.EventInstruments[0].Id, project.EventInstruments[0].SubVoices[0].Id, false, .61,
            [project.EventInstruments[0].SubVoices[1].Id], mode)]);

    private static byte[] ReadEntry(ZipArchive archive, string path)
    {
        using var input = archive.GetEntry(path)!.Open();
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    private static void RewritePresentation(string path, byte[] payload, int version)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var manifest = JsonNode.Parse(ReadEntry(archive, "manifest.json"))!;
        var entry = manifest["files"]!.AsArray().Single(f => f!["kind"]!.GetValue<string>() == "project-presentation-json")!;
        entry["schemaVersion"] = version;
        entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(payload));
        foreach (var (name, bytes) in new[] { (MidoraPackagePathsV1.ProjectPresentation, payload), ("manifest.json", Encoding.UTF8.GetBytes(manifest.ToJsonString())) })
        {
            archive.GetEntry(name)!.Delete();
            using var output = archive.CreateEntry(name).Open(); output.Write(bytes);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, ".tmp", "onion-schema-tests", Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
