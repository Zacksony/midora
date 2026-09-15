using System.Text;

namespace Midora.Application.Tests;

public sealed class InstrumentCatalogImportExportTests
{
    [Fact]
    public void ExportedProfileIsPortableAndImportsAsDetachedUserProfile()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "piano.midora-catalog.json");
        SoundFontEntryId soundFontEntryId = SoundFontEntryId.Create();
        InstrumentCatalogProfile source = new(
            InstrumentCatalogProfileId.Create(),
            "Piano Library",
            true,
            InstrumentCatalogSourceKind.ImportedSf2,
            soundFontEntryId,
            [new(0, 0, "Pianos", [new(0, "Concert Grand")])]);

        InstrumentCatalogExchangeSaveResult exported = InstrumentCatalogImportExport.Export(path, source);
        InstrumentCatalogImportCandidate imported = InstrumentCatalogImportExport.Import(path);
        InstrumentCatalogProfile detached = imported.CreateUserProfile();
        string json = File.ReadAllText(path, Encoding.UTF8);

        Assert.True(exported.Succeeded);
        Assert.DoesNotContain(source.ProfileId.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(soundFontEntryId.ToString(), json, StringComparison.Ordinal);
        Assert.Equal("Piano Library", imported.DisplayName);
        Assert.Contains("Imported SF2 snapshot", imported.SourceDescription, StringComparison.Ordinal);
        Assert.Equal(InstrumentCatalogSourceKind.User, detached.SourceKind);
        Assert.Null(detached.SourceSoundFontEntryId);
        Assert.Equal("Concert Grand", detached.Banks.Single().Programs.Single().DisplayName);
    }

    [Fact]
    public void MergePreviewListsEveryReplacementBeforeApplyingImportedValues()
    {
        InstrumentCatalogProfile existing = new(
            InstrumentCatalogProfileId.Create(),
            "Existing",
            true,
            InstrumentCatalogSourceKind.User,
            null,
            [new(0, 0, "Old Bank", [new(0, "Old Piano"), new(1, "Keep")])]);
        InstrumentCatalogImportCandidate imported = new(
            "Incoming",
            "Test",
            [
                new(0, 0, "New Bank", [new(0, "New Piano"), new(2, "Added")]),
                new(1, 0, null, [new(3, "Other Bank")])
            ]);

        InstrumentCatalogMergePreview preview = InstrumentCatalogImportExport.PreviewMerge(
            existing,
            imported);

        Assert.Equal(2, preview.Conflicts.Count);
        Assert.Contains(preview.Conflicts, value =>
            value.Kind == InstrumentCatalogMergeConflictKind.BankDisplayName
            && value.ExistingValue == "Old Bank"
            && value.ImportedValue == "New Bank");
        Assert.Contains(preview.Conflicts, value =>
            value.Kind == InstrumentCatalogMergeConflictKind.ProgramDisplayName
            && value.ExistingValue == "Old Piano"
            && value.ImportedValue == "New Piano");
        Assert.Equal(1, preview.AddedBanks);
        Assert.Equal(2, preview.AddedPrograms);
        Assert.Equal(0, preview.UnchangedPrograms);
        Assert.Equal(1, preview.ReplacedProgramNames);
        Assert.Equal(existing.ProfileId, preview.MergedProfile.ProfileId);
        Assert.Equal("New Bank", preview.MergedProfile.Banks[0].DisplayName);
        Assert.Equal(
            ["New Piano", "Keep", "Added"],
            preview.MergedProfile.Banks[0].Programs.Select(value => value.DisplayName));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"displayName\":\"X\",\"sourceDescription\":\"X\",\"banks\":[],\"unknown\":0}")]
    [InlineData("{\"schemaVersion\":1,\"displayName\":\"X\",\"displayName\":\"Y\",\"sourceDescription\":\"X\",\"banks\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"displayName\":\"X\",\"sourceDescription\":\"X\",\"banks\":[]}")]
    public void ImportRejectsUnknownDuplicateAndUnsupportedData(string json)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "invalid.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));

        Assert.ThrowsAny<Exception>(() => InstrumentCatalogImportExport.Import(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-catalog-exchange-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
