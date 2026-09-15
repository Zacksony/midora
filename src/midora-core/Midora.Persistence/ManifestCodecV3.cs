using System.Text.Json;

namespace Midora.Persistence;

internal static class ManifestCodecV3
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
        "pure-midi-content-pack"
    };

    public static ManifestJsonV3 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ManifestJsonV3 result = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV3.Default.ManifestJsonV3)
            ?? throw new InvalidDataException("manifest.json cannot be null.");
        Validate(result, allowUnknownFileKinds: true);
        return result;
    }

    public static byte[] Serialize(ManifestJsonV3 manifest)
    {
        Validate(manifest, allowUnknownFileKinds: false);
        ManifestJsonV3 canonical = new()
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
            MidoraJsonSerializerContextV3.Default.ManifestJsonV3);
    }

    public static void Validate(ManifestJsonV3 manifest, bool allowUnknownFileKinds = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Magic != Magic
            || manifest.FileFormatVersion != PersistenceContractV3.FileFormatVersion
            || manifest.MinimumReadableVersion != PersistenceContractV3.FileFormatVersion
            || manifest.ManifestSchemaVersion != PersistenceContractV3.ManifestSchemaVersion)
        {
            throw new InvalidDataException("manifest.json magic or v3 version fields are invalid.");
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
                    ? file.SchemaVersion != PersistenceContractV3.EventInstrumentSchemaVersion
                    : reusedStructural
                        ? file.SchemaVersion != PersistenceContractV3.ReusedComponentSchemaVersion
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
                "Format 3 requires exactly one canonical Project presentation entry.");
        }
    }
}
