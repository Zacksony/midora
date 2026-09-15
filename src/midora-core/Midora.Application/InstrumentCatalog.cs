using System.Security.Cryptography;
using System.Text;

namespace Midora.Application;

public readonly record struct SoundFontEntryId(Guid Value)
{
    private const string LegacyMigrationNamespace =
        "Midora.Application.SoundFontEntryId.Schema2Migration.v1\0";

    public static SoundFontEntryId Create() => new(Guid.NewGuid());

    public static SoundFontEntryId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out Guid parsed))
        {
            throw new FormatException("A SoundFont entry ID must contain exactly 32 hexadecimal digits.");
        }
        SoundFontEntryId result = new(parsed);
        result.Validate();
        return result;
    }

    public static bool TryParse(string? value, out SoundFontEntryId result)
    {
        if (value is not null
            && value.Length == 32
            && Guid.TryParseExact(value, "N", out Guid parsed)
            && parsed != Guid.Empty)
        {
            result = new(parsed);
            return true;
        }
        result = default;
        return false;
    }

    internal static SoundFontEntryId CreateForSchema2Migration(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalizedPath = Path.GetFullPath(path)
            .Replace('/', '\\');
        byte[] input = Encoding.UTF8.GetBytes(LegacyMigrationNamespace + normalizedPath);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        Span<byte> idBytes = hash[..16];
        idBytes[6] = (byte)((idBytes[6] & 0x0F) | 0x50);
        idBytes[8] = (byte)((idBytes[8] & 0x3F) | 0x80);
        return new(new Guid(idBytes, bigEndian: true));
    }

    public void Validate()
    {
        if (Value == Guid.Empty)
        {
            throw new ArgumentException("A SoundFont entry ID cannot be empty.", nameof(Value));
        }
    }

    public override string ToString() => Value.ToString("N");
}

public readonly record struct InstrumentCatalogProfileId(Guid Value)
{
    public static InstrumentCatalogProfileId Create() => new(Guid.NewGuid());

    public static InstrumentCatalogProfileId Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 32 || !Guid.TryParseExact(value, "N", out Guid parsed))
        {
            throw new FormatException("A catalog profile ID must contain exactly 32 hexadecimal digits.");
        }
        InstrumentCatalogProfileId result = new(parsed);
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (Value == Guid.Empty)
        {
            throw new ArgumentException("A catalog profile ID cannot be empty.", nameof(Value));
        }
    }

    public override string ToString() => Value.ToString("N");
}

public enum InstrumentCatalogSourceKind
{
    BuiltIn,
    User,
    ImportedSf2
}

public readonly record struct InstrumentBankAddress(byte BankMsb, byte BankLsb)
{
    public void Validate()
    {
        if (BankMsb > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(BankMsb));
        }
        if (BankLsb > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(BankLsb));
        }
    }
}

public readonly record struct InstrumentAddress(byte BankMsb, byte BankLsb, byte Program)
{
    public InstrumentBankAddress Bank => new(BankMsb, BankLsb);

    public void Validate()
    {
        Bank.Validate();
        if (Program > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(Program));
        }
    }
}

public sealed record InstrumentCatalogProgram(byte Program, string DisplayName)
{
    public InstrumentCatalogProgram Normalize()
    {
        string displayName = InstrumentCatalogValidation.NormalizeRequiredName(
            DisplayName,
            nameof(DisplayName));
        InstrumentCatalogProgram result = this with { DisplayName = displayName };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (Program > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(Program));
        }
        InstrumentCatalogValidation.ValidateRequiredName(DisplayName, nameof(DisplayName));
    }
}

public sealed record InstrumentCatalogBank(
    byte BankMsb,
    byte BankLsb,
    string? DisplayName,
    IReadOnlyList<InstrumentCatalogProgram> Programs)
{
    public InstrumentBankAddress Address => new(BankMsb, BankLsb);

    public InstrumentCatalogBank Normalize()
    {
        ArgumentNullException.ThrowIfNull(Programs);
        InstrumentCatalogBank result = this with
        {
            DisplayName = InstrumentCatalogValidation.NormalizeOptionalName(DisplayName),
            Programs = Programs.Select(value =>
            {
                ArgumentNullException.ThrowIfNull(value);
                return value.Normalize();
            })
            .OrderBy(value => value.Program)
            .ToArray()
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        Address.Validate();
        InstrumentCatalogValidation.ValidateOptionalName(DisplayName, nameof(DisplayName));
        ArgumentNullException.ThrowIfNull(Programs);
        if (Programs.Count > InstrumentCatalogLimits.MaximumProgramsPerBank)
        {
            throw new ArgumentOutOfRangeException(nameof(Programs));
        }
        HashSet<byte> programs = [];
        foreach (InstrumentCatalogProgram program in Programs)
        {
            ArgumentNullException.ThrowIfNull(program);
            program.Validate();
            if (!programs.Add(program.Program))
            {
                throw new ArgumentException(
                    $"Bank {BankMsb}.{BankLsb} contains duplicate Program {program.Program}.",
                    nameof(Programs));
            }
        }
    }
}

public sealed record InstrumentCatalogProfile(
    InstrumentCatalogProfileId ProfileId,
    string DisplayName,
    bool Enabled,
    InstrumentCatalogSourceKind SourceKind,
    SoundFontEntryId? SourceSoundFontEntryId,
    IReadOnlyList<InstrumentCatalogBank> Banks)
{
    public InstrumentCatalogProfile Normalize()
    {
        ArgumentNullException.ThrowIfNull(Banks);
        InstrumentCatalogProfile result = this with
        {
            DisplayName = InstrumentCatalogValidation.NormalizeRequiredName(
                DisplayName,
                nameof(DisplayName)),
            Banks = Banks.Select(value =>
            {
                ArgumentNullException.ThrowIfNull(value);
                return value.Normalize();
            })
            .OrderBy(value => value.BankMsb)
            .ThenBy(value => value.BankLsb)
            .ToArray()
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        ProfileId.Validate();
        InstrumentCatalogValidation.ValidateRequiredName(DisplayName, nameof(DisplayName));
        if (!Enum.IsDefined(SourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(SourceKind));
        }
        if (SourceKind == InstrumentCatalogSourceKind.ImportedSf2)
        {
            if (!SourceSoundFontEntryId.HasValue)
            {
                throw new ArgumentException(
                    "An imported SF2 catalog profile must reference its SoundFont entry.",
                    nameof(SourceSoundFontEntryId));
            }
            SourceSoundFontEntryId.Value.Validate();
        }
        else if (SourceSoundFontEntryId.HasValue)
        {
            throw new ArgumentException(
                "Only an imported SF2 catalog profile may reference a SoundFont entry.",
                nameof(SourceSoundFontEntryId));
        }

        ArgumentNullException.ThrowIfNull(Banks);
        if (Banks.Count > InstrumentCatalogLimits.MaximumBanksPerProfile)
        {
            throw new ArgumentOutOfRangeException(nameof(Banks));
        }
        HashSet<InstrumentBankAddress> banks = [];
        foreach (InstrumentCatalogBank bank in Banks)
        {
            ArgumentNullException.ThrowIfNull(bank);
            bank.Validate();
            if (!banks.Add(bank.Address))
            {
                throw new ArgumentException(
                    $"Profile '{DisplayName}' contains duplicate Bank {bank.BankMsb}.{bank.BankLsb}.",
                    nameof(Banks));
            }
        }
    }
}

public sealed record InstrumentCatalogOverride(
    byte BankMsb,
    byte BankLsb,
    byte Program,
    string? BankDisplayName,
    string? ProgramDisplayName)
{
    public InstrumentAddress Address => new(BankMsb, BankLsb, Program);

    public InstrumentCatalogOverride Normalize()
    {
        InstrumentCatalogOverride result = this with
        {
            BankDisplayName = InstrumentCatalogValidation.NormalizeOptionalName(BankDisplayName),
            ProgramDisplayName = InstrumentCatalogValidation.NormalizeOptionalName(ProgramDisplayName)
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        Address.Validate();
        InstrumentCatalogValidation.ValidateOptionalName(BankDisplayName, nameof(BankDisplayName));
        InstrumentCatalogValidation.ValidateOptionalName(ProgramDisplayName, nameof(ProgramDisplayName));
        if (BankDisplayName is null && ProgramDisplayName is null)
        {
            throw new ArgumentException(
                "A catalog override must provide a bank name, a program name, or both.");
        }
    }
}

public sealed record InstrumentCatalogState(
    bool GeneralMidiEnabled,
    IReadOnlyList<InstrumentCatalogProfile> Profiles,
    IReadOnlyList<InstrumentCatalogOverride> Overrides)
{
    public static InstrumentCatalogState Default { get; } = new(
        GeneralMidiEnabled: true,
        Array.Empty<InstrumentCatalogProfile>(),
        Array.Empty<InstrumentCatalogOverride>());

    public InstrumentCatalogState Normalize()
    {
        ArgumentNullException.ThrowIfNull(Profiles);
        ArgumentNullException.ThrowIfNull(Overrides);
        InstrumentCatalogState result = this with
        {
            Profiles = Profiles.Select(value =>
            {
                ArgumentNullException.ThrowIfNull(value);
                return value.Normalize();
            }).ToArray(),
            Overrides = Overrides.Select(value =>
            {
                ArgumentNullException.ThrowIfNull(value);
                return value.Normalize();
            })
            .OrderBy(value => value.BankMsb)
            .ThenBy(value => value.BankLsb)
            .ThenBy(value => value.Program)
            .ToArray()
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Profiles);
        ArgumentNullException.ThrowIfNull(Overrides);
        if (Profiles.Count > InstrumentCatalogLimits.MaximumProfiles)
        {
            throw new ArgumentOutOfRangeException(nameof(Profiles));
        }
        if (Overrides.Count > InstrumentCatalogLimits.MaximumOverrides)
        {
            throw new ArgumentOutOfRangeException(nameof(Overrides));
        }

        HashSet<InstrumentCatalogProfileId> profileIds = [];
        HashSet<InstrumentAddress> overrides = [];
        Dictionary<InstrumentBankAddress, string> overrideBankNames = [];
        long totalPrograms = Overrides.Count;
        foreach (InstrumentCatalogProfile profile in Profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            profile.Validate();
            if (!profileIds.Add(profile.ProfileId))
            {
                throw new ArgumentException("Catalog profile IDs must be unique.", nameof(Profiles));
            }
            foreach (InstrumentCatalogBank bank in profile.Banks)
            {
                totalPrograms = checked(totalPrograms + bank.Programs.Count);
            }
        }
        if (totalPrograms > InstrumentCatalogLimits.MaximumTotalPrograms)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Profiles),
                $"Instrument catalogs cannot contain more than {InstrumentCatalogLimits.MaximumTotalPrograms} program entries.");
        }
        foreach (InstrumentCatalogOverride catalogOverride in Overrides)
        {
            ArgumentNullException.ThrowIfNull(catalogOverride);
            catalogOverride.Validate();
            if (!overrides.Add(catalogOverride.Address))
            {
                throw new ArgumentException(
                    $"Duplicate catalog override for {catalogOverride.BankMsb}.{catalogOverride.BankLsb}.{catalogOverride.Program}.",
                    nameof(Overrides));
            }
            if (catalogOverride.BankDisplayName is { } bankName
                && overrideBankNames.TryGetValue(catalogOverride.Address.Bank, out string? existingBankName)
                && !string.Equals(bankName, existingBankName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"User overrides assign conflicting names to Bank {catalogOverride.BankMsb}.{catalogOverride.BankLsb}.",
                    nameof(Overrides));
            }
            else if (catalogOverride.BankDisplayName is not null)
            {
                overrideBankNames[catalogOverride.Address.Bank] = catalogOverride.BankDisplayName;
            }
        }
    }
}

public static class InstrumentCatalogLimits
{
    public const int MaximumProfiles = 256;
    public const int MaximumBanksPerProfile = 16_384;
    public const int MaximumProgramsPerBank = 128;
    public const int MaximumOverrides = 1_000_000;
    public const int MaximumTotalPrograms = 1_000_000;
    public const int MaximumDisplayNameCodeUnits = 256;
    public const int MaximumCatalogFileBytes = 64 * 1024 * 1024;
    public const int MaximumSf2PresetRecords = 1_000_001;
}

internal static class InstrumentCatalogValidation
{
    public static string NormalizeRequiredName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        string normalized = value.Trim().Normalize(NormalizationForm.FormC);
        ValidateRequiredName(normalized, parameterName);
        return normalized;
    }

    public static string? NormalizeOptionalName(string? value)
    {
        if (value is null)
        {
            return null;
        }
        string normalized = value.Trim().Normalize(NormalizationForm.FormC);
        return normalized.Length == 0 ? null : normalized;
    }

    public static void ValidateRequiredName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > InstrumentCatalogLimits.MaximumDisplayNameCodeUnits)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A catalog display name must be trimmed and cannot contain control characters.",
                parameterName);
        }
    }

    public static void ValidateOptionalName(string? value, string parameterName)
    {
        if (value is not null)
        {
            ValidateRequiredName(value, parameterName);
        }
    }
}
