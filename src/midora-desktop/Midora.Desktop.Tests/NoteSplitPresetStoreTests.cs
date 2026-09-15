using System.Text.Json;
using System.Text.Json.Nodes;
using Midora.Application;
using Midora.Compiler;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class NoteSplitPresetStoreTests
{
    [Fact]
    public void RoundTripUsesTheDedicatedPortableDirectoryAndRejectsNameCollisions()
    {
        using TemporaryDirectory temporary = new();
        NoteSplitPresetStore store = new(temporary.Path);
        NoteSplitPreset preset = CreatePreset("Accelerando") with
        {
            Mode = NoteSplitMode.Expression,
            Expression = "=Max(12, 96 - i)",
            MaximumCuts = 4096
        };

        NoteSplitPresetInfo saved = store.Save(preset);

        Assert.Equal("NoteSplitPresets", Directory.GetParent(saved.Path)!.Name);
        Assert.Equal(preset, Assert.Single(store.Load()).Preset);
        Assert.Contains(
            $"\"numericContractVersion\": {NoteSplitPresetStore.CurrentNumericContractVersion}",
            File.ReadAllText(saved.Path),
            StringComparison.Ordinal);
        Assert.Throws<IOException>(() => store.Save(preset));
        Assert.Empty(Directory.EnumerateFiles(store.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public void UnknownDamagedAndWrongProfilePresetsAreIsolated()
    {
        using TemporaryDirectory temporary = new();
        NoteSplitPresetStore store = new(temporary.Path);
        NoteSplitPresetInfo valid = store.Save(CreatePreset("Valid"));
        string directory = Directory.GetParent(valid.Path)!.FullName;

        File.WriteAllText(Path.Combine(directory, "Damaged.json"), "{not json");
        string unknown = File.ReadAllText(valid.Path).Replace(
            "{",
            "{\n  \"unexpected\": true,",
            StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, "Unknown.json"), unknown);
        NoteSplitPreset wrongProfile = CreatePreset("WrongProfile") with
        {
            ExpressionProfileId = "midora.tool.note-split/v999"
        };
        File.WriteAllText(
            Path.Combine(directory, "WrongProfile.json"),
            JsonSerializer.Serialize(wrongProfile, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        NoteSplitPresetInfo loaded = Assert.Single(store.Load());
        Assert.Equal("Valid", loaded.Name);
    }

    [Fact]
    public void LoadingRevalidatesExpressionAndAllBoundedFields()
    {
        NoteSplitPreset valid = NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("Valid Expression") with
            {
                Mode = NoteSplitMode.Expression,
                Expression = "=Clamp(96 - i + tr * 0, 1, 192)"
            });
        Assert.Equal("=Clamp(96 - i + tr * 0, 1, 192)", valid.Expression);

        Assert.ThrowsAny<Exception>(() => NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("Unsafe") with
            {
                Mode = NoteSplitMode.Expression,
                Expression = "=new object()"
            }));
        Assert.Throws<InvalidDataException>(() => NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("Too Many") with
            {
                MaximumCuts = NoteSplitPresetStore.MaximumAllowedCuts + 1
            }));
        Assert.Throws<ArgumentException>(() => NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("CON")));
        Assert.Throws<InvalidDataException>(() => NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("Multiline Expression") with
            {
                Mode = NoteSplitMode.Expression,
                Expression = "=i\n+ 1"
            }));
        Assert.Throws<InvalidDataException>(() => NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("Hidden Multiline Expression") with
            {
                Mode = NoteSplitMode.FixedPieceLength,
                Expression = "=i\r\n+ 1"
            }));
        Assert.Throws<InvalidDataException>(() => NoteSplitPresetStore.NormalizeAndValidate(
            CreatePreset("Wrong Numeric Contract") with
            {
                NumericContractVersion = NoteSplitPresetStore.CurrentNumericContractVersion + 1
            }));
    }

    [Fact]
    public void MissingDuplicateAndOversizedJsonAreIsolated()
    {
        using TemporaryDirectory temporary = new();
        NoteSplitPresetStore store = new(temporary.Path);
        NoteSplitPresetInfo valid = store.Save(CreatePreset("Valid"));
        string directory = Directory.GetParent(valid.Path)!.FullName;
        string validJson = File.ReadAllText(valid.Path);

        JsonObject missingNumericContract = JsonNode.Parse(validJson)!.AsObject();
        Assert.True(missingNumericContract.Remove("numericContractVersion"));
        File.WriteAllText(
            Path.Combine(directory, "MissingNumericContract.json"),
            missingNumericContract.ToJsonString());

        File.WriteAllText(
            Path.Combine(directory, "Duplicate.json"),
            validJson.Replace(
                "{",
                $"{{\n  \"schemaVersion\": {NoteSplitPresetStore.CurrentSchemaVersion},",
                StringComparison.Ordinal));
        File.WriteAllText(
            Path.Combine(directory, "Oversized.json"),
            new string(' ', NoteSplitPresetStore.MaximumPresetFileBytes + 1));

        NoteSplitPresetInfo loaded = Assert.Single(store.Load());
        Assert.Equal("Valid", loaded.Name);
    }

    [Fact]
    public void DeleteRejectsPathsOutsideTheOwnedDirectory()
    {
        using TemporaryDirectory temporary = new();
        NoteSplitPresetStore store = new(temporary.Path);
        NoteSplitPreset preset = CreatePreset("Owned");
        NoteSplitPresetInfo foreign = new(
            Path.Combine(temporary.Path, "Foreign.json"),
            preset);
        NoteSplitPresetInfo nested = new(
            Path.Combine(store.DirectoryPath, "Nested", "Owned.json"),
            preset);

        Assert.Throws<InvalidOperationException>(() => store.Delete(foreign));
        Assert.Throws<InvalidOperationException>(() => store.Delete(nested));
    }

    private static NoteSplitPreset CreatePreset(string name) => new(
        NoteSplitPresetStore.CurrentSchemaVersion,
        NoteSplitPresetStore.CurrentToolId,
        NoteSplitPresetStore.CurrentToolVersion,
        NumericExpressionProfiles.NoteSplit.Id,
        NumericExpressionProfiles.NoteSplit.Version,
        name,
        NoteSplitMode.FixedPieceLength,
        192,
        4,
        string.Empty,
        65_535);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "midora-note-split-presets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
