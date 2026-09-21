using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V2;

namespace Midora.Persistence.Tests;

public sealed class PersistenceContractV2Tests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 30, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void ContractVersionsAreIndependentAndFrozenV1RemainsUnchanged()
    {
        Assert.Equal(1, PersistenceContractV1.FileFormatVersion);
        Assert.Equal(1, PersistenceContractV1.SchemaVersion);
        Assert.Equal(2, PersistenceContractV2.FileFormatVersion);
        Assert.Equal(2, PersistenceContractV2.ManifestSchemaVersion);
        Assert.Equal(2, PersistenceContractV2.EventInstrumentSchemaVersion);
        Assert.Equal(1, PersistenceContractV2.ReusedComponentSchemaVersion);
        Assert.Equal(3, PersistenceContractV3.FileFormatVersion);
        Assert.Equal(3, PersistenceContractV3.ManifestSchemaVersion);
        Assert.Equal(3, PersistenceContractV3.ProjectPresentationSchemaVersion);
        Assert.Equal(4, PersistenceContractV4.ProjectPresentationSchemaVersion);
    }

    [Fact]
    public void EventInstrumentV2DescriptorAndGoldenBytesAreStable()
    {
        FieldDescriptor field = EventInstrumentV2.Descriptor.FindFieldByName("pre_roll_ticks")!;
        Assert.Equal(4, field.FieldNumber);
        Assert.Equal(FieldType.Int64, field.FieldType);
        Assert.True(field.HasPresence);

        FileDescriptorSet descriptorSet = new();
        descriptorSet.File.Add(EventInstrumentV2.Descriptor.File.ToProto());
        byte[] descriptorBytes = StrictProtobufWireV1.SerializeDeterministic(descriptorSet);
        string actualDescriptorHash = Convert.ToHexStringLower(SHA256.HashData(descriptorBytes));
        string expectedDescriptorHash = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Schemas",
            "Proto",
            "midora-event-instrument-v2.descriptor.sha256")).Trim();
        Assert.True(
            string.Equals(expectedDescriptorHash, actualDescriptorHash, StringComparison.Ordinal),
            $"Actual descriptor hash: {actualDescriptorHash}");

        MidoraProject project = new(192, FixedTime);
        EventInstrument instrument = new(project)
        {
            Name = "I",
            TemplateLengthTicks = 16,
            PreRollTicks = 4
        };
        byte[] first = EventInstrumentProtobufCodecV2.Serialize(instrument);
        byte[] second = EventInstrumentProtobufCodecV2.Serialize(instrument);
        Assert.Equal(first, second);
        string actualGolden = Convert.ToHexStringLower(first);
        const string expectedGolden =
            "080212106576656e742d696e737472756d656e741a33080112106576656e742d696e737472756d656e7418032201492a07086b1072188001383c4010480050005800600068008201002004";
        Assert.Equal(expectedGolden, actualGolden);
        byte[] reordered = [first[^2], first[^1], .. first[..^2]];
        EventInstrument reorderedInstrument = EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(192, FixedTime),
            reordered);
        Assert.Equal(first, EventInstrumentProtobufCodecV2.Serialize(reorderedInstrument));

        instrument.PreRollTicks = 0;
        Assert.Equal(
            "080212106576656e742d696e737472756d656e741a33080112106576656e742d696e737472756d656e7418032201492a07086b1072188001383c4010480050005800600068008201002000",
            Convert.ToHexStringLower(EventInstrumentProtobufCodecV2.Serialize(instrument)));
        instrument.PreRollTicks = instrument.TemplateLengthTicks;
        Assert.Equal(
            "080212106576656e742d696e737472756d656e741a33080112106576656e742d696e737472756d656e7418032201492a07086b1072188001383c4010480050005800600068008201002010",
            Convert.ToHexStringLower(EventInstrumentProtobufCodecV2.Serialize(instrument)));
    }

    [Fact]
    public void EventInstrumentV2RoundTripsRequiredPreRollStrictly()
    {
        MidoraProject sourceProject = new(480, FixedTime);
        EventInstrument source = EventInstrumentLibrary.Create(sourceProject, "Ahead");
        source.TemplateLengthTicks = 960;
        source.PreRollTicks = 240;

        byte[] bytes = EventInstrumentProtobufCodecV2.Serialize(source);
        EventInstrument restored = EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(480, FixedTime),
            bytes);

        Assert.Equal(240, restored.PreRollTicks);
        Assert.Equal(bytes, EventInstrumentProtobufCodecV2.Serialize(restored));

        EventInstrumentV2 missing = EventInstrumentV2.Parser.ParseFrom(bytes);
        missing.ClearPreRollTicks();
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(480, FixedTime),
            StrictProtobufWireV1.SerializeDeterministic(missing)));

        EventInstrumentV2 negative = EventInstrumentV2.Parser.ParseFrom(bytes);
        negative.PreRollTicks = -1;
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(480, FixedTime),
            StrictProtobufWireV1.SerializeDeterministic(negative)));

        EventInstrumentV2 tooLarge = EventInstrumentV2.Parser.ParseFrom(bytes);
        tooLarge.PreRollTicks = 961;
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(480, FixedTime),
            StrictProtobufWireV1.SerializeDeterministic(tooLarge)));

        byte[] unknown = [.. bytes, 0x28, 0x01];
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(480, FixedTime),
            unknown));

        byte[] duplicatePreRoll = [.. bytes, 0x20, 0xf0, 0x01];
        Assert.Throws<InvalidDataException>(() => EventInstrumentProtobufCodecV2.Restore(
            new MidoraProject(480, FixedTime),
            duplicatePreRoll));
    }

    [Fact]
    public async Task CurrentSaveWritesV4AndRoundTripsPreRoll()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.PathFor("current.midora");
        string equivalentPath = temporary.PathFor("current-equivalent.midora");
        MidoraProject source = CreateProject(preRollTicks: 240);
        EventInstrument sourceInstrument = Assert.Single(source.EventInstruments);
        MidoraProjectPackageV1 packages = CreateService();

        await packages.SaveCopyAsync(source, path);
        await packages.SaveCopyAsync(source, equivalentPath);
        byte[] packageBytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(packageBytes, await File.ReadAllBytesAsync(equivalentPath));
        string packageHash = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
        Assert.True(packageHash == "93cecb8215bd264c0002961c345f882a4db8f11153cd87c4ab4e74bba8d9b0d0",
            $"Actual Format 4 / presentation schema 4 package hash: {packageHash}");

        using (ZipArchive archive = ZipFile.OpenRead(path))
        {
            byte[] manifestBytes = ReadEntry(archive, "manifest.json");
            Assert.Throws<InvalidDataException>(() => ManifestCodecV1.Parse(manifestBytes));
            ManifestJsonV4 manifest = ManifestCodecV4.Parse(manifestBytes);
            Assert.Equal(4, manifest.FileFormatVersion);
            Assert.Equal(4, manifest.MinimumReadableVersion);
            Assert.Equal(4, manifest.ManifestSchemaVersion);
            ManifestFileEntryJsonV1 presentationEntry = Assert.Single(
                manifest.Files,
                item => item.Kind == "project-presentation-json");
            Assert.Equal(MidoraPackagePathsV1.ProjectPresentation, presentationEntry.Path);
            Assert.Equal(4, presentationEntry.SchemaVersion);
            ManifestFileEntryJsonV1 instrumentEntry = Assert.Single(
                manifest.Files,
                item => item.Kind == "event-instrument-pb");
            Assert.Equal(2, instrumentEntry.SchemaVersion);
            EventInstrumentV2 wire = EventInstrumentV2.Parser.ParseFrom(
                ReadEntry(archive, instrumentEntry.Path));
            Assert.True(wire.HasPreRollTicks);
            Assert.Equal(240, wire.PreRollTicks);
        }

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(path);
        Assert.False(opened.IsModified);
        Assert.False(opened.RequiresFormatUpgrade);
        Assert.Empty(opened.Diagnostics);
        Assert.Equal(sourceInstrument.PreRollTicks, Assert.Single(opened.Project.EventInstruments).PreRollTicks);

        // Keep the old Format 3 byte baseline, not merely its schema constants.
        // The legacy fixture is reconstructed with the frozen V3 codec; no V4
        // association data is permitted to leak into that contract.
        string legacyPath = temporary.PathFor("frozen-format-3.midora");
        using (var current = ZipFile.OpenRead(path))
        using (var legacy = ZipFile.Open(legacyPath, ZipArchiveMode.Create))
        {
            var v4 = ManifestCodecV4.Parse(ReadEntry(current, "manifest.json"));
            var v3 = new ManifestJsonV3 { Magic = v4.Magic, FileFormatVersion = 3, MinimumReadableVersion = 3,
                ManifestSchemaVersion = 3, CreatedWithSoftwareVersion = v4.CreatedWithSoftwareVersion,
                LastSavedWithSoftwareVersion = v4.LastSavedWithSoftwareVersion,
                Files = v4.Files.Where(file => file.Kind != "instrument-changes-pb")
                    .Select(file => new ManifestFileEntryJsonV1
                    {
                        Path = file.Path,
                        Kind = file.Kind,
                        SchemaVersion = file.Kind == "project-presentation-json"
                            ? PersistenceContractV3.ProjectPresentationSchemaVersion
                            : file.SchemaVersion,
                        Sha256 = file.Sha256
                    }).ToArray() };
            foreach (var entry in current.Entries.Where(entry => entry.FullName != PersistenceContractV4.InstrumentChangesPath))
            {
                var copy = legacy.CreateEntry(entry.FullName, CompressionLevel.Optimal); copy.LastWriteTime = entry.LastWriteTime;
                using var output = copy.Open();
                byte[] entryBytes = ReadEntry(current, entry.FullName);
                if (entry.FullName == "manifest.json") entryBytes = ManifestCodecV3.Serialize(v3);
                else if (entry.FullName == MidoraPackagePathsV1.ProjectPresentation)
                {
                    JsonObject presentation = JsonNode.Parse(entryBytes)!.AsObject();
                    presentation["schemaVersion"] = PersistenceContractV3.ProjectPresentationSchemaVersion;
                    presentation.Remove("workspaceNavigation");
                    entryBytes = Encoding.UTF8.GetBytes(presentation.ToJsonString());
                }
                output.Write(entryBytes);
            }
        }
        Assert.NotEmpty(await File.ReadAllBytesAsync(legacyPath));
        await using var legacyOpened = await packages.OpenAsync(legacyPath);
        Assert.Equal(3, legacyOpened.SourceFileFormatVersion); Assert.True(legacyOpened.RequiresFormatUpgrade);
        Assert.Empty(legacyOpened.Project.EventInstruments.SelectMany(i => i.SubVoices).SelectMany(v => v.InstrumentChanges.Values));
    }

    [Fact]
    public async Task FormatThreePresentationRoundTripsDeterministicallyWithoutEnteringProjectSourceData()
    {
        using TemporaryDirectory temporary = new();
        string firstPath = temporary.PathFor("presentation-a.midora");
        string secondPath = temporary.PathFor("presentation-b.midora");
        using MidoraProject project = CreateProject(preRollTicks: 0);
        EventInstrument instrument = Assert.Single(project.EventInstruments);
        SubVoice secondVoice = new(project) { Name = "Layer" };
        instrument.SubVoices.Add(secondVoice);
        EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack firstTrack = new(project)
        {
            Name = "One",
            EventInstrumentUsageId = usage.Id
        };
        LogicalTrack secondTrack = new(project)
        {
            Name = "Two",
            EventInstrumentUsageId = usage.Id
        };
        project.Tracks.Add(firstTrack);
        project.Tracks.Add(secondTrack);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, firstTrack.Id));
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, secondTrack.Id));
        ProjectPresentationStateV3 presentation = new(
            ProjectPresentationAllTracksModeV3.Compiled,
            [new(firstTrack.Id, Enabled: true, Opacity: 0.35, [secondTrack.Id])],
            [new(
                instrument.Id,
                instrument.SubVoices[0].Id,
                Enabled: false,
                Opacity: 0.75,
                [secondVoice.Id])]);
        MidoraProjectPackageV1 packages = CreateService();

        await packages.SaveCopyAsync(project, presentation, firstPath);
        await packages.SaveCopyAsync(project, presentation, secondPath);

        Assert.Equal(
            await File.ReadAllBytesAsync(firstPath),
            await File.ReadAllBytesAsync(secondPath));
        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(firstPath);
        Assert.Equal(ProjectPresentationAllTracksModeV3.Compiled, opened.Presentation.AllTracksMode);
        TrackOnionPresetV3 openedTrackPreset = Assert.Single(opened.Presentation.TrackOnionPresets);
        Assert.Equal(firstTrack.Id, openedTrackPreset.TargetTrackId);
        Assert.True(openedTrackPreset.Enabled);
        Assert.Equal(0.35, openedTrackPreset.Opacity);
        Assert.Equal([secondTrack.Id], openedTrackPreset.SourceTrackIds);
        SubVoiceOnionPresetV3 openedVoicePreset = Assert.Single(
            opened.Presentation.SubVoiceOnionPresets);
        Assert.Equal(instrument.Id, openedVoicePreset.EventInstrumentId);
        Assert.Equal(instrument.SubVoices[0].Id, openedVoicePreset.TargetSubVoiceId);
        Assert.False(openedVoicePreset.Enabled);
        Assert.Equal(0.75, openedVoicePreset.Opacity);
        Assert.Equal([secondVoice.Id], openedVoicePreset.SourceSubVoiceIds);
        Assert.False(opened.IsPresentationModified);
        Assert.False(opened.IsModified);
        Assert.Empty(opened.Diagnostics);
    }

    [Fact]
    public async Task DamagedFormatThreePresentationIsIsolatedAndMusicStillOpens()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.PathFor("damaged-presentation.midora");
        using MidoraProject source = CreateProject(preRollTicks: 7);
        source.Metadata.ProjectName = "Music survives";
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(source, path);
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.GetEntry(MidoraPackagePathsV1.ProjectPresentation)!.Delete();
            using Stream output = archive.CreateEntry(MidoraPackagePathsV1.ProjectPresentation).Open();
            output.Write("{not-json"u8);
        }

        await using MidoraProjectOpenResultV1 opened = await packages.OpenAsync(path);

        Assert.Equal("Music survives", opened.Project.Metadata.ProjectName);
        Assert.Equal(7, Assert.Single(opened.Project.EventInstruments).PreRollTicks);
        Assert.Equal(ProjectPresentationStateV3.Empty, opened.Presentation);
        Assert.True(opened.IsPresentationModified);
        Assert.False(opened.IsModified);
        MidoraPackageDiagnosticV1 warning = Assert.Single(
            opened.Diagnostics,
            value => value.Code == "MIDORA-PERSIST-PRESENTATION-RECOVERED");
        Assert.Equal(MidoraPackageDiagnosticSeverityV1.Warning, warning.Severity);
        Assert.Equal(MidoraPackagePathsV1.ProjectPresentation, warning.PackagePath);
    }

    [Fact]
    public void PresentationRejectsDanglingDuplicateAndSelfReferencesBeforeSave()
    {
        using MidoraProject project = CreateProject(preRollTicks: 0);
        LogicalTrack track = new(project) { Name = "Only" };
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
        MidoraId missing = new(long.MaxValue);

        Assert.Throws<InvalidDataException>(() => ProjectPresentationCodecV3.Serialize(
            new(
                ProjectPresentationAllTracksModeV3.Raw,
                [new(track.Id, true, 1, [track.Id])],
                []),
            project));
        Assert.Throws<InvalidDataException>(() => ProjectPresentationCodecV3.Serialize(
            new(
                ProjectPresentationAllTracksModeV3.Raw,
                [new(track.Id, true, 1, [missing])],
                []),
            project));
        Assert.Throws<InvalidDataException>(() => ProjectPresentationCodecV3.Serialize(
            new(
                ProjectPresentationAllTracksModeV3.Raw,
                [
                    new(track.Id, true, 1, []),
                    new(track.Id, false, 0.5, [])
                ],
                []),
            project));
    }

    [Fact]
    public async Task FrozenV1OpensAsDetachedMigrationWithoutChangingSource()
    {
        using TemporaryDirectory temporary = new();
        string v1Path = temporary.PathFor("legacy-v1.midora");
        string v3Path = temporary.PathFor("migrated-v3.midora");
        MidoraProject source = CreateProject(preRollTicks: 0);
        MidoraProjectPackageV1 packages = CreateService();
        await packages.SaveCopyAsync(source, v1Path);
        DowngradeFixtureToFrozenV1(v1Path, Assert.Single(source.EventInstruments));
        byte[] sourceBytes = await File.ReadAllBytesAsync(v1Path);

        MidoraProjectOpenResultV1 opened = await packages.OpenAsync(v1Path);

        Assert.True(opened.IsModified);
        Assert.True(opened.RequiresFormatUpgrade);
        Assert.Equal(0, Assert.Single(opened.Project.EventInstruments).PreRollTicks);
        MidoraPackageDiagnosticV1 migration = Assert.Single(
            opened.Diagnostics,
            item => item.Code == "MIDORA-PERSIST-FORMAT-MIGRATED");
        Assert.Equal(MidoraPackageDiagnosticCategoryV1.VersionCompatibility, migration.Category);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(v1Path));

        Assert.Equal(1, opened.SourceFileFormatVersion);
        Assert.NotNull(opened.LegacySourceIdentity);
        await packages.SaveCopyAsync(opened.Project, v3Path, opened.FileInformation);
        MidoraProjectOpenResultV1 reopened = await packages.OpenAsync(v3Path);
        Assert.False(reopened.IsModified);
        Assert.False(reopened.RequiresFormatUpgrade);
        Assert.Empty(reopened.Diagnostics);
        Assert.Equal(0, Assert.Single(reopened.Project.EventInstruments).PreRollTicks);
    }

    [Fact]
    public void V2JsonSchemaSetIsStrictAndHasFrozenHashes()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Schemas", "Json");
        string[] names =
        [
            "common-v1.schema.json",
            "conductor-track-v1.schema.json",
            "global-event-scope-defaults-v1.schema.json",
            "global-reset-defaults-v1.schema.json",
            "manifest-v2.schema.json",
            "metadata-v1.schema.json",
            "project-settings-v1.schema.json",
            "project-v1.schema.json"
        ];
        Dictionary<string, string> expected = File.ReadAllLines(
                Path.Combine(directory, "midora-json-v2.schema-set.sha256"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        Assert.Equal(names.Order(), expected.Keys.Order());
        foreach (string name in names)
        {
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(directory, name))));
            Assert.Equal(expected[name], actual);
        }
    }

    [Fact]
    public void V3JsonSchemaSetIsStrictAndHasFrozenHashes()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Schemas", "Json");
        string[] names =
        [
            "common-v1.schema.json",
            "conductor-track-v1.schema.json",
            "global-event-scope-defaults-v1.schema.json",
            "global-reset-defaults-v1.schema.json",
            "manifest-v3.schema.json",
            "metadata-v1.schema.json",
            "project-presentation-v1.schema.json",
            "project-presentation-v2.schema.json",
            "project-presentation-v3.schema.json",
            "project-settings-v1.schema.json",
            "project-v1.schema.json"
        ];
        Dictionary<string, string> expected = File.ReadAllLines(
                Path.Combine(directory, "midora-json-v3.schema-set.sha256"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        Assert.Equal(names.Order(), expected.Keys.Order());
        foreach (string name in names)
        {
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(directory, name))));
            Assert.Equal(expected[name], actual);
        }
    }

    [Fact]
    public void V4JsonSchemaSetIsStrictAndHasFrozenHashes()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Schemas", "Json");
        string[] names =
        [
            "common-v1.schema.json",
            "conductor-track-v1.schema.json",
            "global-event-scope-defaults-v1.schema.json",
            "global-reset-defaults-v1.schema.json",
            "manifest-v4.schema.json",
            "metadata-v1.schema.json",
            "project-presentation-v1.schema.json",
            "project-presentation-v2.schema.json",
            "project-presentation-v3.schema.json",
            "project-presentation-v4.schema.json",
            "project-settings-v1.schema.json",
            "project-v1.schema.json"
        ];
        Dictionary<string, string> expected = File.ReadAllLines(
                Path.Combine(directory, "midora-json-v4.schema-set.sha256"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        Assert.Equal(names.Order(), expected.Keys.Order());
        foreach (string name in names)
        {
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(directory, name))));
            Assert.Equal(expected[name], actual);
        }
    }

    private static MidoraProject CreateProject(long preRollTicks)
    {
        MidoraProject project = new(480, FixedTime);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        instrument.TemplateLengthTicks = 960;
        instrument.PreRollTicks = preRollTicks;
        return project;
    }

    private static MidoraProjectPackageV1 CreateService() =>
        new("1.0.0-dev-test", new FixedTimeProvider(FixedTime));

    private static void DowngradeFixtureToFrozenV1(string path, EventInstrument instrument)
    {
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update);
        ManifestJsonV4 current = ManifestCodecV4.Parse(ReadEntry(archive, "manifest.json"));
        ManifestFileEntryJsonV1 currentInstrument = Assert.Single(
            current.Files,
            item => item.Kind == "event-instrument-pb");
        byte[] v1Bytes = EventInstrumentProtobufCodecV1.Serialize(instrument);

        archive.GetEntry(currentInstrument.Path)!.Delete();
        using (Stream output = archive.CreateEntry(currentInstrument.Path).Open())
        {
            output.Write(v1Bytes);
        }

        ManifestJsonV1 v1 = new()
        {
            Magic = current.Magic,
            FileFormatVersion = 1,
            MinimumReadableVersion = 1,
            ManifestSchemaVersion = 1,
            CreatedWithSoftwareVersion = current.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = current.LastSavedWithSoftwareVersion,
            Files = current.Files
                .Where(item => item.Kind is not ("project-presentation-json" or "instrument-changes-pb"))
                .Select(item => new ManifestFileEntryJsonV1
            {
                Path = item.Path,
                Kind = item.Kind,
                SchemaVersion = 1,
                Sha256 = item.Path == currentInstrument.Path
                    ? Convert.ToHexStringLower(SHA256.HashData(v1Bytes))
                    : item.Sha256
            }).ToArray()
        };
        archive.GetEntry(MidoraPackagePathsV1.ProjectPresentation)!.Delete();
        archive.GetEntry(PersistenceContractV4.InstrumentChangesPath)!.Delete();
        archive.GetEntry("manifest.json")!.Delete();
        using Stream manifestOutput = archive.CreateEntry("manifest.json").Open();
        manifestOutput.Write(ManifestCodecV1.Serialize(v1));
    }

    private static byte[] ReadEntry(ZipArchive archive, string path)
    {
        using Stream input = archive.GetEntry(path)!.Open();
        using MemoryStream output = new();
        input.CopyTo(output);
        return output.ToArray();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-persistence-v2-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string PathFor(string fileName) => System.IO.Path.Combine(Path, fileName);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
