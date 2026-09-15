using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class MidoraProjectPackageV1Tests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 1, 2, 3, 4, TimeSpan.Zero);
    private static readonly DateTimeOffset SavedAt =
        new(2026, 8, 6, 7, 8, 9, TimeSpan.Zero);

    [Fact]
    public async Task SaveCopyRoundTripsSupportedProjectAndReleasesPackageHandle()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("round-trip.midora");
        MidoraProject source = CreatePopulatedProject();
        long expectedNextStableId = source.NextStableId;
        ProjectMetadataSnapshot originalMetadata = source.Metadata.Snapshot();
        MidoraProjectPackageV1 packages = CreateService();

        MidoraProjectSaveResultV1 saved = await packages.SaveCopyAsync(source, packagePath);
        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(packagePath);

        Assert.Equal(originalMetadata, source.Metadata.Snapshot());
        Assert.Equal("0.1.0-test", saved.FileInformation.CreatedWithSoftwareVersion);
        Assert.Equal("0.1.0-test", saved.FileInformation.LastSavedWithSoftwareVersion);
        Assert.False(opened.IsModified);
        Assert.Empty(opened.Diagnostics);
        Assert.Equal(saved.FileInformation, opened.FileInformation);
        Assert.Equal(960, opened.Project.TicksPerQuarterNote);
        Assert.Equal(expectedNextStableId, opened.Project.NextStableId);
        Assert.Equal("Round Trip", opened.Project.Metadata.ProjectName);
        Assert.Equal(CreatedAt, opened.Project.Metadata.CreatedAtUtc);
        Assert.Equal(SavedAt, opened.Project.Metadata.ModifiedAtUtc);
        Assert.Equal(2, opened.Project.Conductor.Tempos.Count);
        Assert.Equal(2, opened.Project.Conductor.TimeSignatures.Count);
        Assert.Single(opened.Project.Conductor.KeySignatures);
        Assert.Single(opened.Project.Conductor.Markers);
        Assert.Equal(3_840, opened.Project.Conductor.EndMarkerTick);
        Assert.Equal(12, opened.Project.GlobalInitialState.BankMsb);
        Assert.Equal(99, opened.Project.GlobalInitialState.Controllers[7]);
        Assert.Equal(64, opened.Project.GlobalResetDefaults.Controllers[64]);
        using (FileStream exclusive = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusive.Length > 0);
        }

        AssertCanonicalContainer(packagePath);
    }

    [Fact]
    public async Task EquivalentSaveCopiesAreByteIdenticalAndDoNotMutateCurrentMetadata()
    {
        using TemporaryDirectory temporary = new();
        MidoraProject project = CreatePopulatedProject();
        ProjectMetadataSnapshot before = project.Metadata.Snapshot();
        MidoraProjectPackageV1 packages = CreateService();
        string first = temporary.PathFor("first.midora");
        string second = temporary.PathFor("second.midora");

        await packages.SaveCopyAsync(project, first);
        await packages.SaveCopyAsync(project, second);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        Assert.Equal(before, project.Metadata.Snapshot());
    }

    [Fact]
    public async Task SaveSnapshotIncludesCurrentEditingSessionElapsedTimeExactlyOnce()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("editing-time.midora");
        MidoraProject project = new(480, CreatedAt);
        ManualTimeProvider sessionClock = new(CreatedAt);
        using ProjectEditingTimeSession session = new(project, sessionClock);
        sessionClock.Advance(TimeSpan.FromMilliseconds(1_234));

        await CreateService().SaveCopyAsync(project, packagePath, editingTimeSession: session);
        MidoraProjectOpenResultV1 opened = await CreateService().OpenAsync(packagePath);

        Assert.Equal(1_234, project.Metadata.TotalEditingTimeMilliseconds);
        Assert.Equal(1_234, opened.Project.Metadata.TotalEditingTimeMilliseconds);
        Assert.Equal(1_234, session.SnapshotTotalEditingTimeMilliseconds());
    }

    [Fact]
    public async Task SaveProjectRequiresOverwriteAuthorizationAndCommitsMetadataOnlyAfterPublish()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("existing.midora");
        byte[] original = Encoding.UTF8.GetBytes("existing target");
        await File.WriteAllBytesAsync(packagePath, original);
        MidoraProject project = CreatePopulatedProject();
        DateTimeOffset before = project.Metadata.ModifiedAtUtc;
        MidoraProjectPackageV1 packages = CreateService();

        MidoraPackageExceptionV1 denied = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.SaveProjectAsync(project, packagePath));

        Assert.Equal(MidoraPackageStageV1.Preflight, denied.Stage);
        Assert.Equal(original, await File.ReadAllBytesAsync(packagePath));
        Assert.Equal(before, project.Metadata.ModifiedAtUtc);

        await packages.SaveProjectAsync(project, packagePath, overwriteAuthorized: true);

        Assert.Equal(SavedAt, project.Metadata.ModifiedAtUtc);
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".midora-save-*"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".*.midora-temp-*"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".*.midora-backup-*"));
        Assert.False((await packages.OpenAsync(packagePath)).IsModified);
    }

    [Fact]
    public async Task CanceledSaveLeavesExistingTargetAndCurrentModifiedTimeUntouched()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("cancel.midora");
        byte[] original = [9, 8, 7, 6];
        await File.WriteAllBytesAsync(packagePath, original);
        MidoraProject project = CreatePopulatedProject();
        DateTimeOffset originalModifiedAt = project.Metadata.ModifiedAtUtc;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().SaveProjectAsync(
            project,
            packagePath,
            overwriteAuthorized: true,
            cancellationToken: cancellation.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(packagePath));
        Assert.Equal(originalModifiedAt, project.Metadata.ModifiedAtUtc);
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".midora-save-*"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".*.midora-temp-*"));
        Assert.Empty(Directory.GetFileSystemEntries(temporary.Path, ".*.midora-backup-*"));
    }

    [Fact]
    public async Task CorruptOrdinarySettingsAndConductorRecoverWithErrorsAndModifiedState()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("recover.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        TamperEntriesWithoutUpdatingManifest(
            packagePath,
            "settings/project-settings.json",
            "conductor-track.json");

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(packagePath);

        Assert.True(opened.IsModified);
        Assert.Equal(2, opened.Diagnostics.Count(item =>
            item.Severity == MidoraPackageDiagnosticSeverityV1.Error
            && item.Code == "MIDORA-PERSIST-RECOVERED-DEFAULT"));
        Assert.Equal(192, opened.Project.TicksPerQuarterNote);
        Assert.Single(opened.Project.Conductor.Tempos);
        Assert.Single(opened.Project.Conductor.TimeSignatures);
        Assert.Equal(0, opened.Project.Conductor.Tempos[0].Tick);
        Assert.Equal(0, opened.Project.Conductor.TimeSignatures[0].Tick);
    }

    [Theory]
    [InlineData("project.json")]
    [InlineData("metadata.json")]
    public async Task CorruptRequiredOrPresentMetadataFilesFailStrictly(string entryName)
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("strict.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        TamperEntriesWithoutUpdatingManifest(packagePath, entryName);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.HashValidation, failure.Stage);
        Assert.Equal(entryName, failure.PackagePath);
    }

    [Fact]
    public async Task UnindexedEntryIsReportedAndNotPreservedBySaveCopy()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("extra.midora");
        string copyPath = temporary.PathFor("clean.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), sourcePath);
        AddEntry(sourcePath, "extensions/vendor.bin", [1, 2, 3]);

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(sourcePath);

        Assert.False(opened.IsModified);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(opened.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Information, diagnostic.Severity);
        Assert.Equal("MIDORA-PERSIST-INFO-UNINDEXED", diagnostic.Code);
        await packages.SaveCopyAsync(opened.Project, copyPath, opened.FileInformation);
        using ZipArchive copy = ZipFile.OpenRead(copyPath);
        Assert.DoesNotContain(copy.Entries, entry => entry.FullName == "extensions/vendor.bin");
    }

    [Fact]
    public async Task MissingMetadataAndOrdinarySettingRecoverWithDefaults()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("missing-recoverable.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        DeleteEntries(packagePath, "metadata.json");

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(packagePath);

        Assert.True(opened.IsModified);
        Assert.Equal(1, opened.Diagnostics.Count(item =>
            item.Severity == MidoraPackageDiagnosticSeverityV1.Error
            && item.Code == "MIDORA-PERSIST-RECOVERED-DEFAULT"));
        Assert.Equal(string.Empty, opened.Project.Metadata.ProjectName);
        Assert.Equal(SavedAt, opened.Project.Metadata.CreatedAtUtc);
        Assert.Equal(SavedAt, opened.Project.Metadata.ModifiedAtUtc);

        await packages.SaveProjectAsync(
            opened.Project,
            packagePath,
            opened.FileInformation,
            overwriteAuthorized: true);
        MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(packagePath);
        Assert.False(reopened.IsModified);
        Assert.Empty(reopened.Diagnostics);
    }

    [Fact]
    public async Task CaseConflictingZipEntriesAreRejectedBeforeManifestConsumption()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("case-conflict.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        AddEntry(packagePath, "PROJECT.JSON", Encoding.UTF8.GetBytes("{}"));

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Container, failure.Stage);
        Assert.Equal("PROJECT.JSON", failure.PackagePath);
    }

    [Fact]
    public async Task ExactDuplicateZipEntriesAreRejectedBeforeManifestConsumption()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("duplicate.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        AddEntry(packagePath, "project.json", Encoding.UTF8.GetBytes("{}"));

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Container, failure.Stage);
        Assert.Equal("project.json", failure.PackagePath);
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("/absolute.json")]
    [InlineData("C:/drive.json")]
    [InlineData("settings\\backslash.json")]
    public async Task InvalidZipEntryPathsAreRejectedDuringContainerValidation(string entryName)
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("invalid-path.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        AddEntry(packagePath, entryName, [1]);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Container, failure.Stage);
        Assert.Equal(entryName, failure.PackagePath);
    }

    [Fact]
    public async Task OrdinaryEmptyDirectoryEntryIsAllowedAndDroppedBySaveCopy()
    {
        using TemporaryDirectory temporary = new();
        string sourcePath = temporary.PathFor("empty-directory.midora");
        string copyPath = temporary.PathFor("copy.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), sourcePath);
        AddEntry(sourcePath, "empty/", []);

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(sourcePath);
        await packages.SaveCopyAsync(opened.Project, copyPath);

        using ZipArchive copy = ZipFile.OpenRead(copyPath);
        Assert.DoesNotContain(copy.Entries, value => value.FullName == "empty/");
    }

    [Fact]
    public async Task InvalidZipIsReportedAsContainerFailure()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("not-a-zip.midora");
        await File.WriteAllTextAsync(packagePath, "not a zip");

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            CreateService().OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Container, failure.Stage);
    }

    [Fact]
    public async Task NonRegularDirectoryZipEntryIsRejected()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("non-regular.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            ZipArchiveEntry link = archive.CreateEntry("link/");
            link.ExternalAttributes = 0xA000 << 16;
        }

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Container, failure.Stage);
        Assert.Equal("link/", failure.PackagePath);
    }

    [Fact]
    public async Task EncryptedZipEntryIsRejectedDuringContainerValidation()
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("encrypted.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        MarkZipEntriesEncrypted(packagePath);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.Container, failure.Stage);
    }

    [Theory]
    [InlineData("fileFormatVersion", 5)]
    [InlineData("minimumReadableVersion", 5)]
    [InlineData("manifestSchemaVersion", 5)]
    public async Task FutureManifestVersionIsRejectedDuringVersionPreflight(
        string propertyName,
        int futureVersion)
    {
        using TemporaryDirectory temporary = new();
        string packagePath = temporary.PathFor("future.midora");
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(CreatePopulatedProject(), packagePath);
        ReplaceManifestInteger(packagePath, propertyName, futureVersion);

        MidoraPackageVersionCompatibilityExceptionV1 failure =
            await Assert.ThrowsAsync<MidoraPackageVersionCompatibilityExceptionV1>(() =>
                packages.OpenAsync(packagePath));

        Assert.Equal(MidoraPackageStageV1.VersionPreflight, failure.Stage);
        Assert.Equal("manifest.json", failure.PackagePath);
        Assert.Equal(4, failure.SupportedFileFormatVersion);
        Assert.Equal(4, failure.SupportedManifestSchemaVersion);
        Assert.Equal(propertyName == "fileFormatVersion" ? 5 : 4, failure.FileFormatVersion);
        Assert.Equal(propertyName == "minimumReadableVersion" ? 5 : 4, failure.MinimumReadableVersion);
        Assert.Equal(propertyName == "manifestSchemaVersion" ? 5 : 4, failure.ManifestSchemaVersion);
    }

    [Fact]
    public void NewJsonCodecsRejectDuplicateAndUnknownProperties()
    {
        byte[] duplicate = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"schemaVersion\":1,\"ticksPerQuarterNote\":480," +
            "\"globalInitialState\":{\"controllers\":[],\"registeredParameters\":[]," +
            "\"nonRegisteredParameters\":[]}}");
        byte[] unknown = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":1,\"unknown\":true,\"tempos\":[]," +
            "\"timeSignatures\":[],\"keySignatures\":[],\"markers\":[]}");

        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Parse(duplicate));
        Assert.Throws<System.Text.Json.JsonException>(() => ConductorTrackCodecV1.Parse(unknown));
    }

    [Fact]
    public void MidiStateCodecUsesSignedPitchBendAndInitialStateControllerBoundaries()
    {
        MidoraProject project = new(480, CreatedAt);
        project.GlobalInitialState.PitchBend = -8_192;
        project.GlobalInitialState.PitchBendRangeCents = 99;
        project.GlobalInitialState.Controllers.Add(119, 127);

        ProjectSettingsJsonV1 parsed = ProjectSettingsCodecV1.Parse(ProjectSettingsCodecV1.Serialize(project));

        Assert.Equal(-8_192, parsed.GlobalInitialState.PitchBend);
        Assert.Equal(99, parsed.GlobalInitialState.PitchBendRangeCents);
        Assert.Equal(119, Assert.Single(parsed.GlobalInitialState.Controllers).Number);

        project.GlobalInitialState.PitchBend = 8_192;
        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Serialize(project));
        project.GlobalInitialState.PitchBend = 8_191;
        project.GlobalInitialState.PitchBendRangeCents = 100;
        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Serialize(project));
        project.GlobalInitialState.PitchBendRangeCents = 99;
        project.GlobalInitialState.Controllers.Clear();
        project.GlobalInitialState.Controllers.Add(91, 0);
        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Serialize(project));
        project.GlobalInitialState.Controllers.Clear();
        project.GlobalInitialState.Controllers.Add(120, 0);
        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Serialize(project));
    }

    private static MidoraProjectPackageV1 CreateService() =>
        new("0.1.0-test", new FixedTimeProvider(SavedAt));

    private static MidoraProject CreatePopulatedProject()
    {
        MidoraProject project = new(960, CreatedAt);
        project.Metadata.ProjectName = "Round Trip";
        project.Metadata.ProjectVersion = "v1";
        project.Metadata.AuthorOrTeam = "Midora contributors";
        project.Metadata.OriginalWork = "Original";
        project.Metadata.Copyright = "Copyright";
        project.Conductor.Tempos.Add(new TempoChange(project, 960, 90.5m));
        project.Conductor.TimeSignatures.Add(new TimeSignatureChange(project, 1_920, 3, 4));
        project.Conductor.KeySignatures.Add(new KeySignatureChange(project, 0, -2, true));
        project.Conductor.Markers.Add(new ProjectMarker(project, 480, "Intro"));
        project.SetEndMarker(3_840);
        project.GlobalInitialState.BankMsb = 12;
        project.GlobalInitialState.Program = 41;
        project.GlobalInitialState.Controllers.Add(7, 99);
        project.GlobalInitialState.RegisteredParameters.Add(0, 256);
        project.GlobalResetDefaults.Controllers.Add(64, 64);
        return project;
    }

    private static void AssertCanonicalContainer(string packagePath)
    {
        string[] expected =
        [
            "manifest.json",
            "project.json",
            "metadata.json",
            "conductor-track.json",
            "settings/project-settings.json",
            "settings/global-reset-defaults.json",
            "settings/global-event-scope-defaults.json",
            "settings/project-presentation.json",
            "settings/instrument-changes.pb"
        ];
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        Assert.Equal(expected, archive.Entries.Select(entry => entry.FullName));
        Assert.All(archive.Entries, entry =>
            Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0), entry.LastWriteTime.DateTime));

        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")!;
        using Stream manifestStream = manifestEntry.Open();
        using MemoryStream buffer = new();
        manifestStream.CopyTo(buffer);
        ManifestJsonV4 manifest = ManifestCodecV4.Parse(buffer.ToArray());
        Assert.Equal(PersistenceContractV4.FileFormatVersion, manifest.FileFormatVersion);
        Assert.Equal(PersistenceContractV4.ManifestSchemaVersion, manifest.ManifestSchemaVersion);
        Assert.Equal(
            expected.Skip(1).OrderBy(path => path, StringComparer.Ordinal),
            manifest.Files.Select(item => item.Path));
        foreach (ManifestFileEntryJsonV1 item in manifest.Files)
        {
            ZipArchiveEntry entry = archive.GetEntry(item.Path)!;
            using Stream content = entry.Open();
            string hash = Convert.ToHexStringLower(SHA256.HashData(content));
            Assert.Equal(item.Sha256, hash);
            Assert.Equal(
                item.Kind == "event-instrument-pb"
                    ? PersistenceContractV3.EventInstrumentSchemaVersion
                    : item.Kind == "project-presentation-json"
                        ? PersistenceContractV3.ProjectPresentationSchemaVersion
                        : PersistenceContractV3.ReusedComponentSchemaVersion,
                item.SchemaVersion);
        }
    }

    private static void TamperEntriesWithoutUpdatingManifest(string packagePath, params string[] entryNames)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        foreach (string entryName in entryNames)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName)!;
            entry.Delete();
            ZipArchiveEntry replacement = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using Stream output = replacement.Open();
            output.Write("{}"u8);
        }
    }

    private static void ReplaceManifestInteger(string packagePath, string propertyName, int value)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ZipArchiveEntry original = archive.GetEntry("manifest.json")!;
        string json;
        using (StreamReader reader = new(original.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false))
        {
            json = reader.ReadToEnd();
        }
        original.Delete();
        string current = $"\"{propertyName}\": {PersistenceContractV4.FileFormatVersion}";
        string replacement = $"\"{propertyName}\": {value}";
        Assert.Contains(current, json, StringComparison.Ordinal);
        ZipArchiveEntry updated = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using StreamWriter writer = new(updated.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(json.Replace(current, replacement, StringComparison.Ordinal));
    }

    private static void AddEntry(string packagePath, string entryName, byte[] bytes)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        output.Write(bytes);
    }

    private static void DeleteEntries(string packagePath, params string[] entryNames)
    {
        using ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Update);
        foreach (string entryName in entryNames)
        {
            archive.GetEntry(entryName)!.Delete();
        }
    }

    private static void MarkZipEntriesEncrypted(string packagePath)
    {
        byte[] bytes = File.ReadAllBytes(packagePath);
        for (int index = 0; index <= bytes.Length - 10; index++)
        {
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4b
                && bytes[index + 2] == 0x03 && bytes[index + 3] == 0x04)
            {
                bytes[index + 6] |= 0x01;
            }
            else if (bytes[index] == 0x50 && bytes[index + 1] == 0x4b
                && bytes[index + 2] == 0x01 && bytes[index + 3] == 0x02)
            {
                bytes[index + 8] |= 0x01;
            }
        }
        File.WriteAllBytes(packagePath, bytes);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration)
        {
            _timestamp = checked(_timestamp + (long)(duration.TotalSeconds * TimestampFrequency));
            utcNow = utcNow.Add(duration);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-package-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
