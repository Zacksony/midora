using System.Text.Json;
using System.Text.Json.Nodes;
using Midora.Application;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class TimelineGenerationPresetStoreTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PresetsRoundTripAllSettingsButNeverCaptureBaseOwnerOrTarget(bool notes)
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore store = new(notes, directory.Path);
        TimelineGenerationPreset preset = CreatePreset(notes, "Wave");
        TimelineGenerationPresetInfo saved = store.Save(preset);
        Assert.Equal(notes ? "NoteGenerationPresets" : "EventGenerationPresets", Directory.GetParent(saved.Path)!.Name);
        Assert.Equal(preset, Assert.Single(store.Load()).Preset);
        JsonObject json = JsonNode.Parse(File.ReadAllText(saved.Path))!.AsObject();
        Assert.Equal(notes ? 1 : MidiEditingValueDomain.NumericContractVersion, json["numericContractVersion"]!.GetValue<int>());
        Assert.True(json["createFirstFromInitialValues"]!.GetValue<bool>());
        Assert.Equal(100, json["maximumCandidates"]!.GetValue<int>());
        Assert.Equal(2048, json["maximumRelativeStartTick"]!.GetValue<long>());
        Assert.DoesNotContain("baseTick", File.ReadAllText(saved.Path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner", File.ReadAllText(saved.Path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("target", File.ReadAllText(saved.Path), StringComparison.OrdinalIgnoreCase);
        if (notes)
        {
            var applied = preset.ToNoteOptions(987);
            Assert.Equal(987, applied.BaseTick);
            Assert.Equal(0.75, applied.InitialVelocity);
            Assert.Equal(-4, applied.InitialKey);
            Assert.Equal(1.25, applied.InitialGate);
            Assert.Equal(48, applied.InitialTick);
        }
        else
        {
            var applied = preset.ToEventOptions(987);
            Assert.Equal(987, applied.BaseTick);
            Assert.Equal(-64, applied.InitialValue);
            Assert.Equal(48, applied.InitialTick);
        }
        Assert.Throws<IOException>(() => store.Save(preset with { Name = "wave" }));
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryPath, "*.tmp"));
        store.Delete(saved);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void NoteAndEventHaveSeparateNamespacesAndCannotLoadEachOthersPresets()
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore notes = new(true, directory.Path), events = new(false, directory.Path);
        var note = notes.Save(CreatePreset(true, "Shared Name"));
        var point = events.Save(CreatePreset(false, "Shared Name"));
        Assert.NotEqual(note.Path, point.Path);
        Assert.Throws<InvalidDataException>(() => notes.Save(point.Preset));
        Assert.Throws<InvalidDataException>(() => events.Save(note.Preset));
        Assert.Throws<InvalidOperationException>(() => notes.Delete(point));
        Assert.True(File.Exists(point.Path));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("toolVersion")]
    [InlineData("expressionProfileVersion")]
    [InlineData("numericContractVersion")]
    public void UnknownAndMissingVersionFieldsAreRejected(string property)
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore store = new(true, directory.Path);
        var valid = store.Save(CreatePreset(true, "Valid"));
        JsonObject altered = JsonNode.Parse(File.ReadAllText(valid.Path))!.AsObject();
        altered[property] = 999;
        Write(store, "Unknown", altered);
        altered.Remove(property);
        Write(store, "Missing", altered);
        Assert.Equal("Valid", Assert.Single(store.Load()).Name);
    }

    [Theory]
    [InlineData("toolId")]
    [InlineData("expressionProfileId")]
    [InlineData("createFirstFromInitialValues")]
    [InlineData("maximumCandidates")]
    [InlineData("maximumRelativeStartTick")]
    [InlineData("note")]
    [InlineData("event")]
    public void MissingRequiredSchemaFieldsAreNotTreatedAsSilentDefaults(string property)
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore store = new(true, directory.Path);
        var valid = store.Save(CreatePreset(true, "Valid"));
        JsonObject altered = JsonNode.Parse(File.ReadAllText(valid.Path))!.AsObject();
        altered.Remove(property);
        Write(store, "Missing", altered);
        Assert.Equal("Valid", Assert.Single(store.Load()).Name);
    }

    [Fact]
    public void InvalidNestedFieldsUnknownDuplicateAndOversizedFilesAreIsolated()
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore store = new(true, directory.Path);
        var valid = store.Save(CreatePreset(true, "Valid"));
        string json = File.ReadAllText(valid.Path);
        File.WriteAllText(Path.Combine(store.DirectoryPath, "Broken.json"), "{unfinished");
        File.WriteAllText(Path.Combine(store.DirectoryPath, "Duplicate.json"), json.Replace("\"initialVelocity\": 0.75", "\"initialVelocity\": 1, \"initialVelocity\": 0.75", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(store.DirectoryPath, "Oversized.json"), new string(' ', TimelineGenerationPresetStore.MaximumPresetFileBytes + 1));
        JsonObject unknown = JsonNode.Parse(json)!.AsObject();
        unknown["note"]!["baseTick"] = 123;
        Write(store, "UnknownNested", unknown);
        JsonObject missing = JsonNode.Parse(json)!.AsObject();
        missing["note"]!.AsObject().Remove("initialTick");
        Write(store, "MissingNested", missing);
        JsonObject nullExpression = JsonNode.Parse(json)!.AsObject();
        nullExpression["note"]!["tickExpression"] = null;
        Write(store, "NullExpression", nullExpression);
        Assert.Equal("Valid", Assert.Single(store.Load()).Name);
    }

    [Fact]
    public void PresetLoadRecompilesDependenciesSyntaxAndAllLimits()
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore store = new(true, directory.Path);
        var valid = store.Save(CreatePreset(true, "Valid"));
        string json = File.ReadAllText(valid.Path);
        foreach (string expression in new[] { "123", "=", "=new object()", "=System.IO.File.ReadAllText(\"x\")", "=t1", "=t0\n+ 1" })
        {
            JsonObject altered = JsonNode.Parse(json)!.AsObject();
            altered["note"]!["tickExpression"] = expression;
            Write(store, "BadExpression" + Guid.NewGuid().ToString("N"), altered);
        }
        JsonObject cycle = JsonNode.Parse(json)!.AsObject();
        cycle["note"]!["gateExpression"] = "=v1";
        cycle["note"]!["velocityExpression"] = "=g1";
        Write(store, "Cycle", cycle);
        JsonObject tooMany = JsonNode.Parse(json)!.AsObject();
        tooMany["maximumCandidates"] = 16_777_217;
        Write(store, "TooMany", tooMany);
        JsonObject negativeLimit = JsonNode.Parse(json)!.AsObject();
        negativeLimit["maximumRelativeStartTick"] = -1;
        Write(store, "NegativeLimit", negativeLimit);
        JsonObject wrongTool = JsonNode.Parse(json)!.AsObject();
        wrongTool["toolId"] = "midora.tool.batch-note";
        Write(store, "WrongTool", wrongTool);
        Assert.Equal("Valid", Assert.Single(store.Load()).Name);
        Assert.Throws<ArgumentException>(() => store.Save(CreatePreset(true, "CON")));
        Assert.Throws<ArgumentException>(() => store.Save(CreatePreset(true, "../bad")));
        Assert.Throws<ArgumentException>(() => store.Save(CreatePreset(true, "bad.")));
    }

    [Fact]
    public void IdentityPresetsAndDisabledRelativeLimitsRemainExplicitAndPortable()
    {
        using TemporaryDirectory directory = new();
        TimelineGenerationPresetStore store = new(false, directory.Path);
        var expected = TimelineGenerationPreset.FromEvents("Identity", new() { BaseTick = 24576 });
        store.Save(expected);
        Assert.Equal(expected, Assert.Single(store.Load()).Preset);
        Assert.False(expected.CreateFirstFromInitialValues);
        Assert.Null(expected.MaximumRelativeStartTick);
        Assert.Equal(string.Empty, expected.Event!.ValueExpression);
        Assert.Equal(string.Empty, expected.Event.TickExpression);
    }

    private static void Write(TimelineGenerationPresetStore store, string file, JsonObject json) =>
        File.WriteAllText(Path.Combine(store.DirectoryPath, file + ".json"), json.ToJsonString());

    private static TimelineGenerationPreset CreatePreset(bool notes, string name) => notes
        ? TimelineGenerationPreset.FromNotes(name, new()
        {
            BaseTick = 1234, MaximumCandidates = 100, MaximumRelativeStartTick = 2048, CreateFirstFromInitialValues = true,
            InitialVelocity = 0.75, InitialKey = -4, InitialGate = 1.25, InitialTick = 48,
            VelocityExpression = "=Clamp(v0 + 1, 1, 127)", KeyExpression = "=60 + i % 12", GateExpression = "=24", TickExpression = "=tr + 24"
        })
        : TimelineGenerationPreset.FromEvents(name, new()
        {
            BaseTick = 1234, MaximumCandidates = 100, MaximumRelativeStartTick = 2048, CreateFirstFromInitialValues = true,
            InitialValue = -64, InitialTick = 48, ValueExpression = "=64 * Sin(t1 / 96)", TickExpression = "=t0 + 12"
        });

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "midora-generation-presets-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
