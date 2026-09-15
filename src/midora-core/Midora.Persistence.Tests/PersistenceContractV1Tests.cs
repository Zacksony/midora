using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Midora.Domain;
using Midora.Persistence.Wire.Proto.V1;

namespace Midora.Persistence.Tests;

public sealed class PersistenceContractV1Tests
{
    [Fact]
    public void ProjectSettingsV1UsesSmfCompatibleTicksPerQuarterNoteRange()
    {
        MidoraProject maximum = new(32_767);
        ProjectSettingsJsonV1 parsed = ProjectSettingsCodecV1.Parse(
            ProjectSettingsCodecV1.Serialize(maximum));

        Assert.Equal(32_767, parsed.TicksPerQuarterNote);
        string canonical = Encoding.UTF8.GetString(ProjectSettingsCodecV1.Serialize(maximum));
        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Parse(
            Encoding.UTF8.GetBytes(canonical.Replace(
                "\"ticksPerQuarterNote\": 32767",
                "\"ticksPerQuarterNote\": 0",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ProjectSettingsCodecV1.Parse(
            Encoding.UTF8.GetBytes(canonical.Replace(
                "\"ticksPerQuarterNote\": 32767",
                "\"ticksPerQuarterNote\": 32768",
                StringComparison.Ordinal))));
    }

    [Fact]
    public void ContractVersionsAndTextLimitsAreFrozen()
    {
        Assert.Equal(1, PersistenceContractV1.FileFormatVersion);
        Assert.Equal(1, PersistenceContractV1.SchemaVersion);
        Assert.Equal("2024", PersistenceContractV1.ProtobufEdition);
        Assert.Equal("3.35.1", PersistenceContractV1.GoogleProtobufVersion);
        Assert.Equal("2.83.0", PersistenceContractV1.GrpcToolsVersion);
        Assert.Equal(256, PersistenceContractV1.ShortTextMaximumScalars);
        Assert.Equal(4_096, PersistenceContractV1.MetadataTextMaximumScalars);
        Assert.Equal(65_536, PersistenceContractV1.DescriptionMaximumScalars);
        Assert.Equal(8_192, PersistenceContractV1.MappingBodyMaximumScalars);
        Assert.Equal(4_096, PersistenceContractV1.RelativePathMaximumScalars);
    }

    [Fact]
    public void TimestampUsesFixedSevenDigitUtcForm()
    {
        DateTimeOffset value = new(2026, 8, 6, 12, 34, 56, TimeSpan.FromHours(8));
        value = value.AddTicks(1_234_567);

        string text = PersistenceContractV1.FormatUtcTimestamp(value);

        Assert.Equal("2026-08-06T04:34:56.1234567Z", text);
        Assert.True(PersistenceContractV1.TryParseUtcTimestamp(text, out DateTimeOffset parsed));
        Assert.Equal(value, parsed);
        Assert.False(PersistenceContractV1.TryParseUtcTimestamp("2026-08-06T04:34:56Z", out _));
        Assert.False(PersistenceContractV1.TryParseUtcTimestamp("2026-08-06T12:34:56.1234567+08:00", out _));
        PersistenceValueValidationV1.ValidateEditingDuration(long.MaxValue, "totalEditingTimeMilliseconds");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateEditingDuration(-1, "totalEditingTimeMilliseconds"));
    }

    [Fact]
    public void TextAndPathValidationUsesUnicodeScalarsWithoutNormalization()
    {
        string decomposed = "e\u0301";
        PersistenceValueValidationV1.ValidateShortText(decomposed, "name", allowEmpty: false);
        Assert.Equal(decomposed, decomposed.Normalize(NormalizationForm.FormD));
        PersistenceValueValidationV1.ValidateShortText(string.Concat(Enumerable.Repeat("\U0001f3b5", 256)), "name");

        PersistenceValueValidationV1.ValidateRelativePath("soundfonts/音色.sf2", "path");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("soundfonts/../escape.sf2", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("C:/soundfonts/a.sf2", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("soundfonts\\a.sf2", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRelativePath("soundfonts/", "path"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateShortText(string.Concat(Enumerable.Repeat("\U0001f3b5", 257)), "name"));
        PersistenceValueValidationV1.ValidateMappingBody("Math.Clamp(value, 0, 127)", "mapping");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateMappingBody("value +\n1", "mapping"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateMappingBody(
                "value" + new string(' ', PersistenceContractV1.MappingBodyMaximumScalars),
                "mapping"));
    }

    [Fact]
    public void DomainColorIsOpaqueRgb()
    {
        MidoraColor color = MidoraColor.DefaultInstrument;

        Assert.Equal((byte)0x6b, color.Red);
        Assert.Equal((byte)0x72, color.Green);
        Assert.Equal((byte)0x80, color.Blue);
        Assert.DoesNotContain(typeof(MidoraColor).GetProperties(), property => property.Name is "Alpha" or "Argb");
        PersistenceValueValidationV1.ValidateRgb(color.Red, color.Green, color.Blue, "color");
        Assert.Throws<InvalidDataException>(() => PersistenceValueValidationV1.ValidateRgb(256, 0, 0, "color"));
    }

    [Fact]
    public void ProtobufPrimitiveFieldNumbersAndGoldenBytesAreFrozen()
    {
        Assert.True(RgbColor.Descriptor.File.ToProto().HasEdition);
        Assert.Equal(1001, (int)RgbColor.Descriptor.File.ToProto().Edition);
        Assert.Equal(1, RgbColor.Descriptor.FindFieldByName("red")!.FieldNumber);
        Assert.Equal(2, RgbColor.Descriptor.FindFieldByName("green")!.FieldNumber);
        Assert.Equal(3, RgbColor.Descriptor.FindFieldByName("blue")!.FieldNumber);
        Assert.Equal(3, EventInstrumentV1.Descriptor.FindFieldByName("id")!.FieldNumber);
        Assert.Equal(FieldType.Int64, EventInstrumentV1.Descriptor.FindFieldByName("id")!.FieldType);
        Assert.Equal(3, LogicalTrackV1.Descriptor.FindFieldByName("id")!.FieldNumber);
        Assert.Equal(FieldType.Int64, LogicalTrackV1.Descriptor.FindFieldByName("id")!.FieldType);
        Assert.Equal(5, LogicalTrackV1.Descriptor.FindFieldByName("event_instrument_usage_id")!.FieldNumber);
        Assert.Equal(FieldType.Int64,
            LogicalTrackV1.Descriptor.FindFieldByName("event_instrument_usage_id")!.FieldType);

        PersistenceValueValidationV1.ValidateStableId(long.MaxValue, "id");
        PersistenceValueValidationV1.ValidateStableIdText("9223372036854775807", "id");
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateStableId(0, "id"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateStableId(-1, "id"));
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateStableIdText("01", "id"));

        LogicalNoteV1 id = new() { Id = 300 };
        byte[] bytes = StrictProtobufWireV1.SerializeDeterministic(id);

        Assert.Equal("08ac02", Convert.ToHexString(bytes).ToLowerInvariant());
        StrictProtobufWireV1.Validate(bytes, LogicalNoteV1.Descriptor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("\"1\"")]
    [InlineData("9223372036854775808")]
    public void StableIdJsonRejectsEveryNonCanonicalOrOutOfRangeToken(string json)
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<StableIdJsonV1>(json));
    }

    [Fact]
    public void StableIdJsonWritesExactDecimalIntegerTokensAcrossProjectAndConductor()
    {
        StableIdJsonV1 maximum = JsonSerializer.Deserialize<StableIdJsonV1>("9223372036854775807");
        Assert.Equal(long.MaxValue, maximum.Value);
        Assert.Equal("9223372036854775807", JsonSerializer.Serialize(maximum));

        MidoraProject project = new(480, new DateTimeOffset(2026, 8, 7, 0, 0, 0, TimeSpan.Zero));
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        EventInstrumentUsage usage = new(project) { EventInstrumentId = instrument.Id };
        project.EventInstrumentUsages.Add(usage);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            EventInstrumentUsageId = usage.Id
        };
        project.Tracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));

        using JsonDocument projectJson = JsonDocument.Parse(ProjectCodecV1.Serialize(project));
        JsonElement root = projectJson.RootElement;
        Assert.Equal(JsonValueKind.Number, root.GetProperty("nextStableId").ValueKind);
        Assert.Equal(JsonValueKind.Number,
            root.GetProperty("arrangementTracks")[0].GetProperty("id").ValueKind);

        using JsonDocument conductorJson = JsonDocument.Parse(ConductorTrackCodecV1.Serialize(project));
        Assert.Equal(JsonValueKind.Number,
            conductorJson.RootElement.GetProperty("tempos")[0].GetProperty("id").ValueKind);
    }

    [Fact]
    public void ConductorPersistenceRejectsTimeSignatureIncompatibleWithProjectTpq()
    {
        MidoraProject project = new(1);
        string json = Encoding.UTF8.GetString(ConductorTrackCodecV1.Serialize(project));
        string incompatibleJson = json.Replace(
            "\"denominator\": 4",
            "\"denominator\": 8",
            StringComparison.Ordinal);
        Assert.NotEqual(json, incompatibleJson);
        ConductorTrackJsonV1 incompatible = ConductorTrackCodecV1.Parse(
            Encoding.UTF8.GetBytes(incompatibleJson));

        Assert.Throws<InvalidDataException>(() =>
            ConductorTrackCodecV1.Restore(project, incompatible));
        project.Conductor.TimeSignatures[0] = project.Conductor.TimeSignatures[0] with
        {
            Denominator = 8
        };
        Assert.Throws<InvalidDataException>(() => ConductorTrackCodecV1.Serialize(project));
    }

    [Fact]
    public void ProtobufStableIdScalarRejectsZeroAndNegativeValues()
    {
        Assert.Equal(long.MaxValue,
            ProtobufValueCodecV1.FromWire(long.MaxValue, "id").Value);
        Assert.Throws<InvalidDataException>(() => ProtobufValueCodecV1.FromWire(0, "id"));
        Assert.Throws<InvalidDataException>(() => ProtobufValueCodecV1.FromWire(-1, "id"));
    }

    [Fact]
    public void StrictProtobufRejectsUnknownFieldsWrongWireTypesAndInvalidRgb()
    {
        byte[] unknownField = Convert.FromHexString("08013001");
        byte[] wrongWireType = Convert.FromHexString("090100000000000000");
        byte[] uint32Overflow = Convert.FromHexString("088080808010");
        byte[] nonFiniteDouble = Convert.FromHexString("09000000000000f87f");

        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(unknownField, LogicalNoteV1.Descriptor));
        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(wrongWireType, LogicalNoteV1.Descriptor));
        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(uint32Overflow, RgbColor.Descriptor));
        Assert.Throws<InvalidDataException>(() =>
            StrictProtobufWireV1.Validate(nonFiniteDouble, DoubleValue.Descriptor));

        RgbColor invalid = new() { Red = 256 };
        Assert.Throws<InvalidDataException>(() =>
            PersistenceValueValidationV1.ValidateRgb(invalid.Red, invalid.Green, invalid.Blue, "color"));
    }

    [Fact]
    public void ManifestJsonIsStrictAndDeterministic()
    {
        ManifestJsonV1 manifest = CreateManifest();

        byte[] first = ManifestCodecV1.Serialize(manifest);
        byte[] second = ManifestCodecV1.Serialize(manifest);

        Assert.Equal(first, second);
        Assert.Equal((byte)'\n', first[^1]);
        Assert.DoesNotContain((byte)'\r', first);
        Assert.False(first.Length >= 3
            && first[0] == 0xef
            && first[1] == 0xbb
            && first[2] == 0xbf);
        ManifestJsonV1 parsed = ManifestCodecV1.Parse(first);
        Assert.Equal("project.json", parsed.Files[0].Path);

        string duplicate = """
            {"magic":"midora-project","magic":"midora-project"}
            """;
        Assert.Throws<InvalidDataException>(() => ManifestCodecV1.Parse(Encoding.UTF8.GetBytes(duplicate)));

        string unknown = Encoding.UTF8.GetString(first).Replace(
            "\"files\": [",
            "\"unknown\": 1,\n  \"files\": [",
            StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => ManifestCodecV1.Parse(Encoding.UTF8.GetBytes(unknown)));

        string futureKind = Encoding.UTF8.GetString(first).Replace(
            "\"settings-json\"",
            "\"future-extension\"",
            StringComparison.Ordinal);
        ManifestJsonV1 withFutureKind = ManifestCodecV1.Parse(Encoding.UTF8.GetBytes(futureKind));
        Assert.Equal("future-extension", withFutureKind.Files[1].Kind);
        Assert.Throws<InvalidDataException>(() => ManifestCodecV1.Serialize(withFutureKind));
    }

    [Fact]
    public void CheckedInJsonSchemasUseDraft202012AndStrictObjects()
    {
        string schemaDirectory = Path.Combine(AppContext.BaseDirectory, "Schemas", "Json");
        string[] paths = Directory.GetFiles(schemaDirectory, "*-v1.schema.json", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) != "project-presentation-v1.schema.json")
            .ToArray();
        Assert.Equal(8, paths.Length);
        foreach (string path in paths)
        {
            using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.Equal(PersistenceContractV1.JsonSchemaDialect,
                schema.RootElement.GetProperty("$schema").GetString());
            if (schema.RootElement.TryGetProperty("type", out JsonElement type)
                && type.GetString() == "object")
            {
                Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
            }
        }
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(schemaDirectory, "manifest-v1.schema.json")));
        Assert.False(manifest.RootElement.GetProperty("additionalProperties").GetBoolean());

        Dictionary<string, string> expectedHashes = File.ReadAllLines(
                Path.Combine(schemaDirectory, "midora-json-v1.schema-set.sha256"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None))
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        Assert.Equal(paths.Length, expectedHashes.Count);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            Assert.True(expectedHashes.TryGetValue(name, out string? expected),
                $"JSON schema '{name}' is absent from the frozen format-v1 hash set.");
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void CheckedInDescriptorHashMatchesFrozenProtoDescriptor()
    {
        FileDescriptorSet descriptorSet = new();
        descriptorSet.File.Add(RgbColor.Descriptor.File.ToProto());
        byte[] descriptorBytes = StrictProtobufWireV1.SerializeDeterministic(descriptorSet);
        string actual = Convert.ToHexString(SHA256.HashData(descriptorBytes)).ToLowerInvariant();
        string baselinePath = Path.Combine(
            AppContext.BaseDirectory, "Schemas", "Proto", "midora-common-v1.descriptor.sha256");

        string expected = File.ReadAllText(baselinePath).Trim();
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
            $"Common protobuf descriptor hash mismatch. Expected={expected}; Actual={actual}");
    }

    private static ManifestJsonV1 CreateManifest() => new()
    {
        Magic = "midora-project",
        FileFormatVersion = 1,
        MinimumReadableVersion = 1,
        ManifestSchemaVersion = 1,
        CreatedWithSoftwareVersion = "0.1.0",
        LastSavedWithSoftwareVersion = "0.1.0",
        Files =
        [
            new ManifestFileEntryJsonV1
            {
                Path = "settings/project-settings.json",
                Kind = "settings-json",
                SchemaVersion = 1,
                Sha256 = new string('a', 64)
            },
            new ManifestFileEntryJsonV1
            {
                Path = "project.json",
                Kind = "core-json",
                SchemaVersion = 1,
                Sha256 = new string('0', 64)
            }
        ]
    };
}
