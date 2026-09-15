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
        Assert.Equal(2, JsonNode.Parse(bytes)!["schemaVersion"]!.GetValue<int>());
        var parsed = ProjectPresentationCodecV3.Parse(bytes, project, 2);
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
    public async Task Schema1IsReadAsCustomAndSavedAsSchema2WithoutChangingMusic()
    {
        using var project = CreateProject();
        var state = State(project, OnionSourceMode.Next);
        JsonObject legacy = JsonNode.Parse(ProjectPresentationCodecV3.Serialize(state, project))!.AsObject();
        legacy["schemaVersion"] = 1;
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
        Assert.Equal(2, JsonNode.Parse(ReadEntry(archive, MidoraPackagePathsV1.ProjectPresentation))!["schemaVersion"]!.GetValue<int>());
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
        int declared = 2;
        switch (corruption)
        {
            case "future-version": payload["schemaVersion"] = declared = 99; break;
            case "mismatched-version": declared = 1; break;
            case "unknown-mode": preset["sourceMode"] = "random"; break;
            case "missing-mode": preset.Remove("sourceMode"); break;
            case "null-mode": preset["sourceMode"] = null; break;
            case "unknown-field": preset["unsupported"] = true; break;
            case "string-version": payload["schemaVersion"] = "2"; break;
            case "legacy-unknown-field": payload["schemaVersion"] = declared = 1; break;
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
        Assert.Equal(ProjectPresentationStateV3.Empty, opened.Presentation);
        Assert.Equal(project.Tracks.Select(t => t.Id), opened.Project.Tracks.Select(t => t.Id));
        Assert.Equal("MIDORA-PERSIST-PRESENTATION-RECOVERED", Assert.Single(opened.Diagnostics).Code);
    }

    [Fact]
    public void WriterRejectsUnknownModes()
    {
        using var project = CreateProject();
        Assert.Throws<InvalidDataException>(() => ProjectPresentationCodecV3.Serialize(State(project, (OnionSourceMode)99), project));
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
