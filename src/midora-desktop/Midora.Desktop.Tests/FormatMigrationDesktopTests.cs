using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Midora.Application;
using Midora.Domain;
using Midora.Persistence;
using Xunit;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class FormatMigrationDesktopTests
{
    [Fact]
    public async Task DesktopCanExplicitlyUpgradeFormatOneInPlaceWithExactBackup()
    {
        using TemporaryDirectory temporary = new();
        string legacyPath = Path.Combine(temporary.Path, "legacy.midora");
        using MidoraProject project = new(192);
        MidoraProjectPackageV1 packages = new("1.0.0-dev-test");
        await packages.SaveCopyAsync(project, legacyPath);
        DowngradeStructureOnlyPackageToFormatOne(legacyPath);

        await using DesktopSessionController session = new();
        await session.OpenProjectAsync(legacyPath);

        ProjectPersistenceCoordinator persistence = Assert.IsType<ProjectPersistenceCoordinator>(
            session.Persistence);
        Assert.Null(persistence.CurrentProjectPath);
        Assert.Equal(Path.GetFullPath(legacyPath), persistence.ProtectedSourceProjectPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveProjectAsync(legacyPath, overwriteAuthorized: true));

        byte[] original = await File.ReadAllBytesAsync(legacyPath);
        MidoraLegacyProjectUpgradePlanV3 plan =
            await session.PrepareLegacyProjectUpgradeAsync();
        string backupPath = await session.UpgradeLegacyProjectInPlaceAsync(plan);

        Assert.Equal(original, await File.ReadAllBytesAsync(backupPath));
        Assert.Equal(Path.GetFullPath(legacyPath), persistence.CurrentProjectPath);
        Assert.False(persistence.RequiresFormatUpgrade);
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(legacyPath);
        Assert.Equal(4, reopened.SourceFileFormatVersion);
    }

    private static void DowngradeStructureOnlyPackageToFormatOne(string path)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update);
        ZipArchiveEntry manifest = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("The test package has no manifest.");
        JsonObject json;
        using (Stream input = manifest.Open())
        {
            json = JsonNode.Parse(input)?.AsObject()
                ?? throw new InvalidDataException("The test manifest is not an object.");
        }
        json["fileFormatVersion"] = 1;
        json["minimumReadableVersion"] = 1;
        json["manifestSchemaVersion"] = 1;
        JsonArray files = json["files"]?.AsArray()
            ?? throw new InvalidDataException("The test manifest has no files array.");
        JsonNode? presentation = files.FirstOrDefault(value =>
            string.Equals(value?["path"]?.GetValue<string>(), "settings/project-presentation.json", StringComparison.Ordinal));
        presentation?.Parent?.AsArray().Remove(presentation);
        archive.GetEntry("settings/project-presentation.json")?.Delete();
        manifest.Delete();
        using Stream output = archive.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(output, json, new JsonSerializerOptions { WriteIndented = false });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "midora-format-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
