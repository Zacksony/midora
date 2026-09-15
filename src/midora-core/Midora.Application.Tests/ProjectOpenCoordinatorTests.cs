using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectOpenCoordinatorTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 6, 10, 11, 12, TimeSpan.Zero);

    [Fact]
    public async Task ValidPackageReturnsPersistedCandidateWithoutHoldingSourceFile()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.2.3", clock);
        string path = temporary.PathFor("Project.midora");
        using MidoraProject source = new(480, CreatedAt);
        source.Metadata.ProjectName = "Opened";
        _ = await packages.SaveProjectAsync(source, path);
        RecordingProgress progress = new();

        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path, progress);

        Assert.Equal(ProjectDocumentOrigin.Persisted, candidate.Origin);
        Assert.Equal(Path.GetFullPath(path), candidate.CurrentProjectPath);
        Assert.Equal(new("1.2.3", "1.2.3"), candidate.FileInformation);
        Assert.Equal("Opened", candidate.Project.Metadata.ProjectName);
        Assert.False(candidate.RequiresSave);
        Assert.False(candidate.HasDamagedProjectObjects);
        Assert.True(candidate.CanSaveProject);
        Assert.Empty(candidate.Diagnostics);
        Assert.Equal(
            [
                ProjectOpenCandidateStage.ValidatingInput,
                ProjectOpenCandidateStage.ReadingAndValidatingPackage,
                ProjectOpenCandidateStage.CandidateReady
            ],
            progress.Values.Select(value => value.Stage));

        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task CommitFactoriesStartTimeAndBindDocumentPersistenceToCandidate()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        string path = temporary.PathFor("Project.midora");
        using (MidoraProject source = new(192, CreatedAt))
        {
            _ = await packages.SaveProjectAsync(source, path);
        }
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, candidate.Project.Metadata.TotalEditingTimeMilliseconds);

        using ProjectCompilationSession compilation = new(
            candidate.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);
        clock.Advance(TimeSpan.FromMilliseconds(1_250));

        Assert.Equal(1_250, compilation.SnapshotTotalEditingTimeMilliseconds());
        Assert.False(document.IsModified);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.Equal(candidate.CurrentProjectPath, persistence.CurrentProjectPath);
        Assert.Equal(candidate.FileInformation, persistence.FileInformation);
    }

    [Fact]
    public async Task FormatTwoCandidateCanBeConfirmedAndUpgradedInPlaceWithExactPermanentBackup()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("2.0.0");
        string legacyPath = temporary.PathFor("Legacy-Format-2.midora");
        CanonicalCompiledResult originalCanonical;
        using (MidoraProject source = new(192, CreatedAt))
        {
            source.Metadata.ProjectName = "Legacy";
            EventInstrument instrument = new(source)
            {
                Name = "Instrument",
                TemplateLengthTicks = 192,
                OverlapPolicy = OverlapPolicy.Warn
            };
            SubVoice subVoice = new(source);
            subVoice.Events.Add(TemplateEvent.Note(source, 0, 96, 60, 100));
            instrument.SubVoices.Add(subVoice);
            source.EventInstruments.Add(instrument);
            LogicalTrack track = new(source) { Name = "Track" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(source, track, instrument.Id);
            Segment segment = new(source) { LengthTicks = 384 };
            segment.Notes.Add(new LogicalNote(source)
            {
                StartTick = 48,
                LengthTicks = 192,
                Note = 64,
                Velocity = 96
            });
            track.Segments.Add(segment);
            originalCanonical = new MidoraCompiler().CompileFull(source);
            Assert.True(originalCanonical.IsConsumable);
            _ = await packages.SaveProjectAsync(source, legacyPath);
        }
        DowngradeToFormatTwo(legacyPath);
        byte[] originalBytes = await File.ReadAllBytesAsync(legacyPath);
        string occupiedBackupPath = temporary.PathFor(
            "Legacy-Format-2 - Original Format 2 before Format 4.midora");
        await File.WriteAllBytesAsync(occupiedBackupPath, [9, 8, 7]);
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(legacyPath);
        using ProjectCompilationSession compilation = new(candidate.Project);

        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);
        CanonicalCompiledResult migratedCanonical = new MidoraCompiler().CompileFull(candidate.Project);

        Assert.Equal(originalCanonical.Fingerprint, migratedCanonical.Fingerprint);
        Assert.Equal(originalCanonical.Events.ToArray(), migratedCanonical.Events.ToArray());
        Assert.Equal(originalCanonical.Allocations.ToArray(), migratedCanonical.Allocations.ToArray());

        Assert.Equal(ProjectDocumentOrigin.Unsaved, candidate.Origin);
        Assert.Equal(legacyPath, candidate.SourceProjectPath);
        Assert.Null(candidate.CurrentProjectPath);
        Assert.False(document.HasPersistentOrigin);
        Assert.True(document.NeedsSaveBeforeClose);
        Assert.Equal([ProjectOpenCandidate.FormatUpgradeDirtyReason], document.ExternalDirtyReasons);
        Assert.Null(persistence.CurrentProjectPath);
        Assert.Equal(candidate.FileInformation, persistence.FileInformation);
        Assert.Equal(legacyPath, persistence.ProtectedSourceProjectPath);
        Assert.Equal(2, persistence.LegacySourceFileFormatVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveProjectAsync(legacyPath, overwriteAuthorized: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            persistence.SaveCopyAsync(legacyPath, overwriteAuthorized: true));

        MidoraLegacyProjectUpgradePlanV3 plan =
            await persistence.PrepareLegacyProjectUpgradeAsync();
        Assert.Equal(occupiedBackupPath.Replace(".midora", " (2).midora", StringComparison.Ordinal),
            plan.PermanentBackupPath);
        MidoraProjectSaveResultV1 result =
            await persistence.UpgradeLegacyProjectInPlaceAsync(plan);

        Assert.Equal(Path.GetFullPath(legacyPath), persistence.CurrentProjectPath);
        Assert.Null(persistence.ProtectedSourceProjectPath);
        Assert.False(persistence.RequiresFormatUpgrade);
        Assert.False(document.NeedsSaveBeforeClose);
        Assert.NotNull(result.PermanentLegacyBackupPath);
        Assert.EndsWith(
            "Legacy-Format-2 - Original Format 2 before Format 4 (2).midora",
            result.PermanentLegacyBackupPath,
            StringComparison.Ordinal);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(result.PermanentLegacyBackupPath!));
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(legacyPath);
        Assert.Equal(4, reopened.SourceFileFormatVersion);
        Assert.False(reopened.RequiresFormatUpgrade);
        Assert.Equal("Legacy", reopened.Project.Metadata.ProjectName);
        CanonicalCompiledResult reopenedCanonical = new MidoraCompiler().CompileFull(reopened.Project);
        Assert.Equal(originalCanonical.Fingerprint, reopenedCanonical.Fingerprint);
        Assert.Equal(originalCanonical.Events.ToArray(), reopenedCanonical.Events.ToArray());
        Assert.Equal(originalCanonical.Allocations.ToArray(), reopenedCanonical.Allocations.ToArray());
    }

    [Fact]
    public async Task InPlaceUpgradeRefusesAConfirmedBackupPathOccupiedBeforePublish()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("2.0.0");
        string legacyPath = temporary.PathFor("Backup-Path-Race.midora");
        using (MidoraProject source = new(192, CreatedAt))
        {
            source.Metadata.ProjectName = "Legacy";
            _ = await packages.SaveProjectAsync(source, legacyPath);
        }
        DowngradeToFormatTwo(legacyPath);
        byte[] originalBytes = await File.ReadAllBytesAsync(legacyPath);
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(legacyPath);
        using ProjectCompilationSession compilation = new(candidate.Project);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);
        MidoraLegacyProjectUpgradePlanV3 plan =
            await persistence.PrepareLegacyProjectUpgradeAsync();
        await File.WriteAllBytesAsync(plan.PermanentBackupPath, [9, 8, 7]);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            persistence.UpgradeLegacyProjectInPlaceAsync(plan));

        Assert.Equal(MidoraPackageStageV1.Backup, failure.Stage);
        Assert.Equal(plan.PermanentBackupPath, failure.BackupPath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(legacyPath));
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(plan.PermanentBackupPath));
        Assert.True(persistence.RequiresFormatUpgrade);
        Assert.True(document.NeedsSaveBeforeClose);
        MidoraLegacyProjectUpgradePlanV3 replacementPlan =
            await persistence.PrepareLegacyProjectUpgradeAsync();
        Assert.EndsWith(
            "Backup-Path-Race - Original Format 2 before Format 4 (2).midora",
            replacementPlan.PermanentBackupPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InPlaceUpgradeRefusesAFormatTwoSourceChangedAfterOpen()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("2.0.0");
        string legacyPath = temporary.PathFor("Externally-Changed.midora");
        using (MidoraProject source = new(192, CreatedAt))
        {
            _ = await packages.SaveProjectAsync(source, legacyPath);
        }
        DowngradeToFormatTwo(legacyPath);
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(legacyPath);
        using ProjectCompilationSession compilation = new(candidate.Project);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);
        await using (FileStream output = new(
            legacyPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.None))
        {
            await output.WriteAsync(new byte[] { 0x45, 0x58, 0x54 });
            await output.FlushAsync();
        }
        byte[] externallyChangedBytes = await File.ReadAllBytesAsync(legacyPath);

        MidoraPackageExceptionV1 failure = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() =>
            persistence.UpgradeLegacyProjectInPlaceAsync());

        Assert.Equal(MidoraPackageStageV1.Preflight, failure.Stage);
        Assert.Equal(externallyChangedBytes, await File.ReadAllBytesAsync(legacyPath));
        Assert.Empty(Directory.GetFiles(
            Path.GetDirectoryName(legacyPath)!,
            "*Original Format*"));
        Assert.True(persistence.RequiresFormatUpgrade);
        Assert.True(document.NeedsSaveBeforeClose);
    }

    [Fact]
    public async Task RecoveredMetadataMarksDocumentModifiedUntilSuccessfulSave()
    {
        using TemporaryDirectory temporary = new();
        ManualTimeProvider clock = new(CreatedAt);
        MidoraProjectPackageV1 packages = new("1.0.0", clock);
        string path = temporary.PathFor("Recovered.midora");
        using (MidoraProject source = new(192, CreatedAt))
        {
            source.Metadata.ProjectName = "Will be recovered";
            _ = await packages.SaveProjectAsync(source, path);
        }
        DeleteEntry(path, "metadata.json");
        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);

        Assert.True(candidate.RequiresSave);
        Assert.Empty(candidate.Project.Metadata.ProjectName);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Error, Assert.Single(candidate.Diagnostics).Severity);
        using ProjectCompilationSession compilation = new(
            candidate.Project,
            editingTimeProvider: clock);
        ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
        Assert.True(document.IsModified);
        Assert.Equal([ProjectOpenCandidate.RecoveredSourceDirtyReason], document.ExternalDirtyReasons);
        ProjectPersistenceCoordinator persistence = candidate.CreatePersistenceCoordinator(document);

        _ = await persistence.SaveProjectAsync();

        Assert.False(document.IsModified);
        Assert.Empty(document.ExternalDirtyReasons);
        await using MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(path);
        Assert.False(reopened.IsModified);
        Assert.Empty(reopened.Diagnostics);
    }

    [Fact]
    public async Task ExtraPackageEntryIsInformationWithoutModifiedState()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        string path = temporary.PathFor("Extra.midora");
        using (MidoraProject source = new(192))
        {
            _ = await packages.SaveProjectAsync(source, path);
        }
        AddEntry(path, "unknown/readme.txt", "ignored");

        await using ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages)
            .OpenAsync(path);

        Assert.False(candidate.RequiresSave);
        MidoraPackageDiagnosticV1 diagnostic = Assert.Single(candidate.Diagnostics);
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Information, diagnostic.Severity);
        Assert.Equal("unknown/readme.txt", diagnostic.PackagePath);
    }

    [Fact]
    public async Task InvalidPathPackageAndCancellationDoNotProduceCandidate()
    {
        using TemporaryDirectory temporary = new();
        ProjectOpenCoordinator coordinator = new(new MidoraProjectPackageV1("1.0.0"));
        string invalid = temporary.PathFor("invalid.midora");
        await File.WriteAllTextAsync(invalid, "not a zip");

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.OpenAsync("relative.midora"));
        MidoraPackageExceptionV1 packageError = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(
            () => coordinator.OpenAsync(invalid));
        Assert.Equal(MidoraPackageStageV1.Container, packageError.Stage);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.OpenAsync(invalid, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task CandidateFactoriesRejectDifferentProjectAndDisposedCandidate()
    {
        using TemporaryDirectory temporary = new();
        MidoraProjectPackageV1 packages = new("1.0.0");
        string path = temporary.PathFor("Project.midora");
        using (MidoraProject source = new(192))
        {
            _ = await packages.SaveProjectAsync(source, path);
        }
        ProjectOpenCandidate candidate = await new ProjectOpenCoordinator(packages).OpenAsync(path);
        using MidoraProject otherProject = new(192);
        using ProjectCompilationSession other = new(otherProject);

        Assert.Throws<InvalidOperationException>(() => candidate.CreateDocumentSession(other));
        await candidate.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => candidate.CreateDocumentSession(other));
    }

    private static void DeleteEntry(string packagePath, string entryPath)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"Missing test package entry: {entryPath}");
        entry.Delete();
    }

    private static void AddEntry(string packagePath, string entryPath, string contents)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry entry = archive.CreateEntry(entryPath);
        using StreamWriter writer = new(entry.Open());
        writer.Write(contents);
    }

    private static void DowngradeToFormatTwo(string packagePath)
    {
        using FileStream stream = new(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Update);
        ZipArchiveEntry manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("Missing test manifest.");
        JsonObject manifest;
        using (Stream input = manifestEntry.Open())
        {
            manifest = JsonNode.Parse(input)?.AsObject()
                ?? throw new InvalidOperationException("Invalid test manifest.");
        }
        manifest["fileFormatVersion"] = 2;
        manifest["minimumReadableVersion"] = 2;
        manifest["manifestSchemaVersion"] = 2;
        JsonArray files = manifest["files"]?.AsArray()
            ?? throw new InvalidOperationException("Missing manifest files.");
        JsonNode presentation = files.Single(value =>
            string.Equals(
                value?["path"]?.GetValue<string>(),
                "settings/project-presentation.json",
                StringComparison.Ordinal))!;
        files.Remove(presentation);
        files.Remove(files.Single(value => value?["path"]?.GetValue<string>() == "settings/instrument-changes.pb"));
        archive.GetEntry("settings/instrument-changes.pb")?.Delete();
        manifestEntry.Delete();
        archive.GetEntry("settings/project-presentation.json")?.Delete();
        using Stream output = archive.CreateEntry("manifest.json").Open();
        byte[] bytes = Encoding.UTF8.GetBytes(
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        output.Write(bytes);
    }

    private sealed class RecordingProgress : IProgress<ProjectOpenCandidateProgress>
    {
        public List<ProjectOpenCandidateProgress> Values { get; } = [];
        public void Report(ProjectOpenCandidateProgress value) => Values.Add(value);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan value)
        {
            _utcNow = _utcNow.Add(value);
            _timestamp = checked(_timestamp + value.Ticks);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"midora-open-project-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);
        public string PathFor(string fileName) => Path.Combine(_path, fileName);
        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
