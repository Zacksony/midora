using System.Text.Json;

namespace Midora.Persistence;

internal static class ManifestCodecV4
{
    private const string Magic = "midora-project";
    private static readonly HashSet<string> ReusedStructuralKinds = new(StringComparer.Ordinal)
    {
        "core-json",
        "settings-json",
        "conductor-json",
        "event-instrument-usage-pb",
        "logical-track-pb",
        "midi-channel-root-pb",
        "pure-midi-track-pb",
        "pure-midi-content-pack",
        "instrument-changes-pb"
    };

    public static ManifestJsonV4 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ManifestJsonV4 result = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV4.Default.ManifestJsonV4)
            ?? throw new InvalidDataException("manifest.json cannot be null.");
        Validate(result, allowUnknownFileKinds: true);
        return result;
    }

    public static byte[] Serialize(ManifestJsonV4 manifest)
    {
        Validate(manifest, allowUnknownFileKinds: false);
        ManifestJsonV4 canonical = new()
        {
            Magic = manifest.Magic,
            FileFormatVersion = manifest.FileFormatVersion,
            MinimumReadableVersion = manifest.MinimumReadableVersion,
            ManifestSchemaVersion = manifest.ManifestSchemaVersion,
            CreatedWithSoftwareVersion = manifest.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = manifest.LastSavedWithSoftwareVersion,
            Files = manifest.Files
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .Select(file => new ManifestFileEntryJsonV1
                {
                    Path = file.Path,
                    Kind = file.Kind,
                    SchemaVersion = file.SchemaVersion,
                    Sha256 = file.Sha256
                })
                .ToArray()
        };
        return StrictJsonV1.SerializeWithFinalLf(
            canonical,
            MidoraJsonSerializerContextV4.Default.ManifestJsonV4);
    }

    public static void Validate(ManifestJsonV4 manifest, bool allowUnknownFileKinds = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Magic != Magic
            || manifest.FileFormatVersion != PersistenceContractV4.FileFormatVersion
            || manifest.MinimumReadableVersion != PersistenceContractV4.FileFormatVersion
            || manifest.ManifestSchemaVersion != PersistenceContractV4.ManifestSchemaVersion)
        {
            throw new InvalidDataException("manifest.json magic or v4 version fields are invalid.");
        }
        PersistenceValueValidationV1.ValidateShortText(
            manifest.CreatedWithSoftwareVersion,
            nameof(manifest.CreatedWithSoftwareVersion),
            allowEmpty: false);
        PersistenceValueValidationV1.ValidateShortText(
            manifest.LastSavedWithSoftwareVersion,
            nameof(manifest.LastSavedWithSoftwareVersion),
            allowEmpty: false);
        if (manifest.Files is null)
        {
            throw new InvalidDataException("manifest.json files cannot be null.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (ManifestFileEntryJsonV1 file in manifest.Files)
        {
            if (file is null)
            {
                throw new InvalidDataException("manifest.json files cannot contain null entries.");
            }
            PersistenceValueValidationV1.ValidateRelativePath(file.Path, nameof(file.Path));
            if (!paths.Add(file.Path))
            {
                throw new InvalidDataException($"manifest.json contains duplicate path '{file.Path}'.");
            }
            PersistenceValueValidationV1.ValidateShortText(file.Kind, nameof(file.Kind), allowEmpty: false);

            bool presentation = file.Kind == "project-presentation-json";
            bool eventInstrument = file.Kind == "event-instrument-pb";
            bool reusedStructural = ReusedStructuralKinds.Contains(file.Kind);
            bool unknown = !presentation && !eventInstrument && !reusedStructural;
            bool schemaInvalid = presentation
                // Presentation has an independent reader and recovery boundary. An
                // unsupported future view version must not prevent music loading.
                ? !file.SchemaVersion.HasValue || file.SchemaVersion <= 0
                : eventInstrument
                    ? file.SchemaVersion != PersistenceContractV4.EventInstrumentSchemaVersion
                    : reusedStructural
                        ? file.SchemaVersion != PersistenceContractV4.ReusedComponentSchemaVersion
                        : !file.SchemaVersion.HasValue || file.SchemaVersion <= 0;
            if (schemaInvalid || unknown && !allowUnknownFileKinds)
            {
                throw new InvalidDataException(
                    $"manifest.json file kind/schemaVersion is invalid for '{file.Path}'.");
            }
            if (file.Sha256.Length != 64 || file.Sha256.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            {
                throw new InvalidDataException(
                    $"manifest.json SHA-256 is not canonical for '{file.Path}'.");
            }
        }

        var associationEntries = manifest.Files.Where(value => value.Kind == "instrument-changes-pb").ToArray();
        if (associationEntries.Length != 1 || associationEntries[0].Path != PersistenceContractV4.InstrumentChangesPath)
            throw new InvalidDataException("Format 4 requires exactly one canonical Instrument Changes entry.");

        ManifestFileEntryJsonV1[] presentationEntries = manifest.Files
            .Where(value => value.Kind == "project-presentation-json")
            .ToArray();
        if (presentationEntries.Length != 1
            || !string.Equals(
                presentationEntries[0].Path,
                MidoraPackagePathsV1.ProjectPresentation,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Format 4 requires exactly one canonical Project presentation entry.");
        }
    }
}
