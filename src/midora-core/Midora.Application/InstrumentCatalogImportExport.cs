using System.Text.Json;
using System.Text.Json.Serialization;

namespace Midora.Application;

public sealed record InstrumentCatalogImportCandidate(
    string DisplayName,
    string SourceDescription,
    IReadOnlyList<InstrumentCatalogBank> Banks)
{
    public InstrumentCatalogProfile CreateUserProfile(
        InstrumentCatalogProfileId? profileId = null,
        bool enabled = true) => new InstrumentCatalogProfile(
            profileId ?? InstrumentCatalogProfileId.Create(),
            DisplayName,
            enabled,
            InstrumentCatalogSourceKind.User,
            SourceSoundFontEntryId: null,
            Banks).Normalize();
}

public enum InstrumentCatalogMergeConflictKind
{
    BankDisplayName,
    ProgramDisplayName
}

public sealed record InstrumentCatalogMergeConflict(
    InstrumentCatalogMergeConflictKind Kind,
    InstrumentAddress Address,
    string ExistingValue,
    string ImportedValue);

public sealed record InstrumentCatalogMergePreview(
    InstrumentCatalogProfile MergedProfile,
    IReadOnlyList<InstrumentCatalogMergeConflict> Conflicts,
    int AddedBanks,
    int AddedPrograms,
    int UnchangedPrograms,
    int ReplacedProgramNames);

public sealed record InstrumentCatalogExchangeSaveResult(
    bool Succeeded,
    ApplicationPreferenceNotice? Notice);

public static class InstrumentCatalogImportExport
{
    public const int CurrentSchemaVersion = 1;

    public static InstrumentCatalogExchangeSaveResult Export(
        string filePath,
        InstrumentCatalogProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Normalize();
        InstrumentCatalogExchangeJsonV1 dto = new()
        {
            SchemaVersion = CurrentSchemaVersion,
            DisplayName = profile.DisplayName,
            SourceDescription = profile.SourceKind == InstrumentCatalogSourceKind.ImportedSf2
                ? $"Imported SF2 snapshot: {profile.DisplayName}"
                : $"User profile: {profile.DisplayName}",
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
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            dto,
            InstrumentCatalogExchangeJsonContextV1.Default.InstrumentCatalogExchangeJsonV1);
        if (json.Length > InstrumentCatalogLimits.MaximumCatalogFileBytes)
        {
            throw new InvalidDataException(
                $"The exported catalog exceeds {InstrumentCatalogLimits.MaximumCatalogFileBytes} bytes.");
        }

        string fullPath = Path.GetFullPath(filePath);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The catalog export path has no parent directory.");
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.SequentialScan))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
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
                    "InstrumentCatalogExportFailed",
                    "The Instrument Catalog profile could not be exported.",
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
                    // Best-effort cleanup must not replace the original export result.
                }
            }
        }
    }

    public static InstrumentCatalogImportCandidate Import(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        byte[] json = InstrumentCatalogStore.ReadBoundedFile(Path.GetFullPath(filePath));
        StrictApplicationJson.RejectDuplicateProperties(json);
        InstrumentCatalogExchangeJsonV1 dto = JsonSerializer.Deserialize(
            json,
            InstrumentCatalogExchangeJsonContextV1.Default.InstrumentCatalogExchangeJsonV1)
            ?? throw new InvalidDataException("The imported Instrument Catalog JSON is null.");
        if (dto.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported Instrument Catalog exchange schema version {dto.SchemaVersion}.");
        }
        List<InstrumentCatalogBankJsonV1> importedBanks = dto.Banks
            ?? throw new InvalidDataException("The imported catalog banks collection is required.");
        if (importedBanks.Count > InstrumentCatalogLimits.MaximumBanksPerProfile)
        {
            throw new InvalidDataException("The imported catalog bank limit was exceeded.");
        }
        long totalPrograms = 0;
        InstrumentCatalogBank[] banks = importedBanks.Select(bank =>
        {
            List<InstrumentCatalogProgramJsonV1> importedPrograms = bank.Programs
                ?? throw new InvalidDataException("An imported catalog programs collection is required.");
            if (importedPrograms.Count > InstrumentCatalogLimits.MaximumProgramsPerBank)
            {
                throw new InvalidDataException("The imported catalog program-per-bank limit was exceeded.");
            }
            totalPrograms = checked(totalPrograms + importedPrograms.Count);
            if (totalPrograms > InstrumentCatalogLimits.MaximumTotalPrograms)
            {
                throw new InvalidDataException("The imported catalog total program limit was exceeded.");
            }
            return new InstrumentCatalogBank(
                bank.BankMsb,
                bank.BankLsb,
                bank.DisplayName,
                importedPrograms.Select(program => new InstrumentCatalogProgram(
                    program.Program,
                    program.DisplayName)).ToArray());
        }).ToArray();
        InstrumentCatalogImportCandidate candidate = new(
            InstrumentCatalogValidation.NormalizeRequiredName(dto.DisplayName, nameof(dto.DisplayName)),
            InstrumentCatalogValidation.NormalizeRequiredName(
                dto.SourceDescription,
                nameof(dto.SourceDescription)),
            banks);
        _ = candidate.CreateUserProfile(new InstrumentCatalogProfileId(Guid.Parse(
            "d3de78af-eadb-48be-9e28-50c01d581669")));
        return candidate with { Banks = banks.Select(value => value.Normalize()).ToArray() };
    }

    public static InstrumentCatalogMergePreview PreviewMerge(
        InstrumentCatalogProfile existing,
        InstrumentCatalogImportCandidate imported)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(imported);
        existing = existing.Normalize();
        InstrumentCatalogProfile incoming = imported.CreateUserProfile();

        Dictionary<InstrumentBankAddress, InstrumentCatalogBank> banks = existing.Banks
            .ToDictionary(value => value.Address);
        List<InstrumentCatalogMergeConflict> conflicts = [];
        int addedBanks = 0;
        int addedPrograms = 0;
        int unchangedPrograms = 0;
        int replacedPrograms = 0;
        foreach (InstrumentCatalogBank importedBank in incoming.Banks)
        {
            if (!banks.TryGetValue(importedBank.Address, out InstrumentCatalogBank? existingBank))
            {
                banks.Add(importedBank.Address, importedBank);
                addedBanks++;
                addedPrograms = checked(addedPrograms + importedBank.Programs.Count);
                continue;
            }

            if (importedBank.DisplayName is { } importedBankName
                && existingBank.DisplayName is { } existingBankName
                && !string.Equals(importedBankName, existingBankName, StringComparison.Ordinal))
            {
                conflicts.Add(new(
                    InstrumentCatalogMergeConflictKind.BankDisplayName,
                    new(importedBank.BankMsb, importedBank.BankLsb, 0),
                    existingBankName,
                    importedBankName));
            }
            Dictionary<byte, InstrumentCatalogProgram> programs = existingBank.Programs
                .ToDictionary(value => value.Program);
            foreach (InstrumentCatalogProgram importedProgram in importedBank.Programs)
            {
                if (programs.TryGetValue(importedProgram.Program, out InstrumentCatalogProgram? oldProgram))
                {
                    if (!string.Equals(
                        oldProgram.DisplayName,
                        importedProgram.DisplayName,
                        StringComparison.Ordinal))
                    {
                        conflicts.Add(new(
                            InstrumentCatalogMergeConflictKind.ProgramDisplayName,
                            new(importedBank.BankMsb, importedBank.BankLsb, importedProgram.Program),
                            oldProgram.DisplayName,
                            importedProgram.DisplayName));
                        replacedPrograms++;
                    }
                    else
                    {
                        unchangedPrograms++;
                    }
                }
                else
                {
                    addedPrograms++;
                }
                programs[importedProgram.Program] = importedProgram;
            }
            banks[importedBank.Address] = existingBank with
            {
                DisplayName = importedBank.DisplayName ?? existingBank.DisplayName,
                Programs = programs.Values.OrderBy(value => value.Program).ToArray()
            };
        }

        InstrumentCatalogProfile merged = existing with
        {
            Banks = banks.Values
                .OrderBy(value => value.BankMsb)
                .ThenBy(value => value.BankLsb)
                .ToArray()
        };
        return new(
            merged.Normalize(),
            conflicts.AsReadOnly(),
            addedBanks,
            addedPrograms,
            unchangedPrograms,
            replacedPrograms);
    }
}

internal sealed class InstrumentCatalogExchangeJsonV1
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; }
    [JsonPropertyOrder(1)] public required string DisplayName { get; set; }
    [JsonPropertyOrder(2)] public required string SourceDescription { get; set; }
    [JsonPropertyOrder(3)] public required List<InstrumentCatalogBankJsonV1> Banks { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(InstrumentCatalogExchangeJsonV1))]
internal sealed partial class InstrumentCatalogExchangeJsonContextV1 : JsonSerializerContext;
