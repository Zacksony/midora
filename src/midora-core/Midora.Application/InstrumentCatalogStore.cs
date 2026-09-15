using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Common;

namespace Midora.Application;

public sealed record InstrumentCatalogLoadResult(
    InstrumentCatalogState Catalog,
    ApplicationPreferenceNotice? Notice,
    bool CanPublish);

public sealed record InstrumentCatalogSaveResult(
    bool Succeeded,
    ApplicationPreferenceNotice? Notice);

public sealed class InstrumentCatalogStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly string _filePath;
    private bool _publicationBlockedByReadFailure;

    public InstrumentCatalogStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? GetDefaultFilePath());
    }

    public string FilePath => _filePath;

    public static string GetDefaultFilePath() => Path.Combine(
        MidoraProgramData.Current.CatalogsDirectory,
        "instrument-catalogs.json");

    public InstrumentCatalogLoadResult Load()
    {
        try
        {
            byte[] json = ReadBoundedFile(_filePath);
            StrictApplicationJson.RejectDuplicateProperties(json);
            InstrumentCatalogJsonV1 dto = JsonSerializer.Deserialize(
                json,
                InstrumentCatalogJsonContextV1.Default.InstrumentCatalogJsonV1)
                ?? throw new InvalidDataException("The Instrument Catalog JSON is null.");
            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported Instrument Catalog schema version {dto.SchemaVersion}.");
            }
            InstrumentCatalogState state = FromDto(dto).Normalize();
            _publicationBlockedByReadFailure = false;
            return new(state, null, CanPublish: true);
        }
        catch (FileNotFoundException)
        {
            _publicationBlockedByReadFailure = false;
            return new(InstrumentCatalogState.Default, null, CanPublish: true);
        }
        catch (DirectoryNotFoundException)
        {
            _publicationBlockedByReadFailure = false;
            return new(InstrumentCatalogState.Default, null, CanPublish: true);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            _publicationBlockedByReadFailure = true;
            return new(
                InstrumentCatalogState.Default,
                new ApplicationPreferenceNotice(
                    "InstrumentCatalogReadFailed",
                    "Instrument Catalogs could not be read; the built-in General MIDI catalog is in use.",
                    exception),
                CanPublish: false);
        }
    }

    public InstrumentCatalogSaveResult Save(InstrumentCatalogState catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (_publicationBlockedByReadFailure)
        {
            InvalidOperationException exception = new(
                "Publishing is blocked because the active Instrument Catalog file did not load successfully.");
            return new(
                false,
                new ApplicationPreferenceNotice(
                    "InstrumentCatalogWriteBlockedAfterReadFailure",
                    "Instrument Catalogs were not saved because the existing file is damaged or unreadable. Its original bytes remain unchanged.",
                    exception));
        }
        InstrumentCatalogState normalized = catalog.Normalize();
        InstrumentCatalogJsonV1 dto = ToDto(normalized);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            dto,
            InstrumentCatalogJsonContextV1.Default.InstrumentCatalogJsonV1);
        if (json.Length > InstrumentCatalogLimits.MaximumCatalogFileBytes)
        {
            throw new InvalidDataException(
                $"The Instrument Catalog file exceeds {InstrumentCatalogLimits.MaximumCatalogFileBytes} bytes.");
        }

        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException(
                    "The Instrument Catalog path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
            temporaryPath = null;
            return new(true, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            return new(
                false,
                new ApplicationPreferenceNotice(
                    "InstrumentCatalogWriteFailed",
                    "Instrument Catalogs could not be saved; the previous file remains active.",
                    exception));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup must not replace the original write result.
                }
            }
        }
    }

    internal static byte[] ReadBoundedFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > InstrumentCatalogLimits.MaximumCatalogFileBytes)
        {
            throw new InvalidDataException(
                $"The Instrument Catalog file exceeds {InstrumentCatalogLimits.MaximumCatalogFileBytes} bytes.");
        }
        byte[] result = new byte[checked((int)stream.Length)];
        stream.ReadExactly(result);
        return result;
    }

    internal static InstrumentCatalogJsonV1 ToDto(InstrumentCatalogState state) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        GeneralMidiEnabled = state.GeneralMidiEnabled,
        Profiles = state.Profiles.Select(profile =>
        {
            if (profile.SourceKind == InstrumentCatalogSourceKind.BuiltIn)
            {
                throw new ArgumentException(
                    "Built-in catalog profiles are supplied by the application and cannot be persisted.",
                    nameof(state));
            }
            return new InstrumentCatalogProfileJsonV1
            {
                ProfileId = profile.ProfileId.ToString(),
                DisplayName = profile.DisplayName,
                Enabled = profile.Enabled,
                SourceKind = profile.SourceKind.ToString(),
                SourceSoundFontEntryId = profile.SourceSoundFontEntryId?.ToString(),
                Banks = profile.Banks.Select(bank => new InstrumentCatalogBankJsonV1
                {
                    BankMsb = bank.BankMsb,
                    BankLsb = bank.BankLsb,
                    DisplayName = bank.DisplayName,
                    Programs = bank.Programs.Select(program => new InstrumentCatalogProgramJsonV1
                    {
                        Program = program.Program,
                        DisplayName = program.DisplayName
                    }).ToList()
                }).ToList()
            };
        }).ToList(),
        Overrides = state.Overrides.Select(value => new InstrumentCatalogOverrideJsonV1
        {
            BankMsb = value.BankMsb,
            BankLsb = value.BankLsb,
            Program = value.Program,
            BankDisplayName = value.BankDisplayName,
            ProgramDisplayName = value.ProgramDisplayName
        }).ToList()
    };

    internal static InstrumentCatalogState FromDto(InstrumentCatalogJsonV1 dto)
    {
        List<InstrumentCatalogProfileJsonV1> profiles = dto.Profiles
            ?? throw new InvalidDataException("Instrument Catalog profiles is required.");
        List<InstrumentCatalogOverrideJsonV1> overrides = dto.Overrides
            ?? throw new InvalidDataException("Instrument Catalog overrides is required.");
        if (profiles.Count > InstrumentCatalogLimits.MaximumProfiles
            || overrides.Count > InstrumentCatalogLimits.MaximumOverrides)
        {
            throw new InvalidDataException("Instrument Catalog collection limits were exceeded.");
        }

        long totalPrograms = overrides.Count;
        List<InstrumentCatalogProfile> mappedProfiles = new(profiles.Count);
        foreach (InstrumentCatalogProfileJsonV1 profile in profiles)
        {
            List<InstrumentCatalogBankJsonV1> banks = profile.Banks
                ?? throw new InvalidDataException("A catalog profile banks collection is required.");
            if (banks.Count > InstrumentCatalogLimits.MaximumBanksPerProfile)
            {
                throw new InvalidDataException("A catalog profile bank limit was exceeded.");
            }
            List<InstrumentCatalogBank> mappedBanks = new(banks.Count);
            foreach (InstrumentCatalogBankJsonV1 bank in banks)
            {
                List<InstrumentCatalogProgramJsonV1> programs = bank.Programs
                    ?? throw new InvalidDataException("A catalog bank programs collection is required.");
                if (programs.Count > InstrumentCatalogLimits.MaximumProgramsPerBank)
                {
                    throw new InvalidDataException("A catalog bank program limit was exceeded.");
                }
                totalPrograms = checked(totalPrograms + programs.Count);
                if (totalPrograms > InstrumentCatalogLimits.MaximumTotalPrograms)
                {
                    throw new InvalidDataException("The total Instrument Catalog program limit was exceeded.");
                }
                mappedBanks.Add(new(
                    bank.BankMsb,
                    bank.BankLsb,
                    bank.DisplayName,
                    programs.Select(program => new InstrumentCatalogProgram(
                        program.Program,
                        program.DisplayName)).ToArray()));
            }

            InstrumentCatalogSourceKind sourceKind = profile.SourceKind switch
            {
                nameof(InstrumentCatalogSourceKind.BuiltIn) => throw new InvalidDataException(
                    "Built-in catalog profiles cannot be supplied by the persisted catalog file."),
                nameof(InstrumentCatalogSourceKind.User) => InstrumentCatalogSourceKind.User,
                nameof(InstrumentCatalogSourceKind.ImportedSf2) => InstrumentCatalogSourceKind.ImportedSf2,
                _ => throw new InvalidDataException(
                    $"Unknown Instrument Catalog source kind '{profile.SourceKind}'.")
            };
            SoundFontEntryId? sourceSoundFontId = profile.SourceSoundFontEntryId is null
                ? null
                : ParseSoundFontEntryId(profile.SourceSoundFontEntryId);
            mappedProfiles.Add(new(
                ParseProfileId(profile.ProfileId),
                profile.DisplayName,
                profile.Enabled,
                sourceKind,
                sourceSoundFontId,
                mappedBanks));
        }

        return new(
            dto.GeneralMidiEnabled,
            mappedProfiles,
            overrides.Select(value => new InstrumentCatalogOverride(
                value.BankMsb,
                value.BankLsb,
                value.Program,
                value.BankDisplayName,
                value.ProgramDisplayName)).ToArray());
    }

    private static InstrumentCatalogProfileId ParseProfileId(string value)
    {
        try
        {
            return InstrumentCatalogProfileId.Parse(value);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidDataException("An Instrument Catalog profile ID is invalid.", exception);
        }
    }

    private static SoundFontEntryId ParseSoundFontEntryId(string value)
    {
        try
        {
            return SoundFontEntryId.Parse(value);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidDataException("An Instrument Catalog SoundFont entry ID is invalid.", exception);
        }
    }
}

internal sealed class InstrumentCatalogJsonV1
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; }
    [JsonPropertyOrder(1)] public bool GeneralMidiEnabled { get; set; }
    [JsonPropertyOrder(2)] public required List<InstrumentCatalogProfileJsonV1> Profiles { get; set; }
    [JsonPropertyOrder(3)] public required List<InstrumentCatalogOverrideJsonV1> Overrides { get; set; }
}

internal sealed class InstrumentCatalogProfileJsonV1
{
    [JsonPropertyOrder(0)] public required string ProfileId { get; set; }
    [JsonPropertyOrder(1)] public required string DisplayName { get; set; }
    [JsonPropertyOrder(2)] public bool Enabled { get; set; }
    [JsonPropertyOrder(3)] public required string SourceKind { get; set; }
    [JsonPropertyOrder(4)] public string? SourceSoundFontEntryId { get; set; }
    [JsonPropertyOrder(5)] public required List<InstrumentCatalogBankJsonV1> Banks { get; set; }
}

internal sealed class InstrumentCatalogBankJsonV1
{
    [JsonPropertyOrder(0)] public byte BankMsb { get; set; }
    [JsonPropertyOrder(1)] public byte BankLsb { get; set; }
    [JsonPropertyOrder(2)] public string? DisplayName { get; set; }
    [JsonPropertyOrder(3)] public required List<InstrumentCatalogProgramJsonV1> Programs { get; set; }
}

internal sealed class InstrumentCatalogProgramJsonV1
{
    [JsonPropertyOrder(0)] public byte Program { get; set; }
    [JsonPropertyOrder(1)] public required string DisplayName { get; set; }
}

internal sealed class InstrumentCatalogOverrideJsonV1
{
    [JsonPropertyOrder(0)] public byte BankMsb { get; set; }
    [JsonPropertyOrder(1)] public byte BankLsb { get; set; }
    [JsonPropertyOrder(2)] public byte Program { get; set; }
    [JsonPropertyOrder(3)] public string? BankDisplayName { get; set; }
    [JsonPropertyOrder(4)] public string? ProgramDisplayName { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstrumentCatalogJsonV1))]
internal sealed partial class InstrumentCatalogJsonContextV1 : JsonSerializerContext;
