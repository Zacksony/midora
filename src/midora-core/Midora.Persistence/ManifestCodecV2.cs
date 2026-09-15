using System.Text.Json;

namespace Midora.Persistence;

internal static class ManifestCodecV2
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

    public static ManifestJsonV2 Parse(ReadOnlySpan<byte> utf8)
    {
        StrictJsonV1.ValidateInput(utf8);
        ManifestJsonV2 result = JsonSerializer.Deserialize(
            utf8,
            MidoraJsonSerializerContextV2.Default.ManifestJsonV2)
            ?? throw new InvalidDataException("manifest.json cannot be null.");
        Validate(result, allowUnknownFileKinds: true);
        return result;
    }

    public static byte[] Serialize(ManifestJsonV2 manifest)
    {
        Validate(manifest, allowUnknownFileKinds: false);
        ManifestJsonV2 canonical = new()
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
            MidoraJsonSerializerContextV2.Default.ManifestJsonV2);
    }

    public static void Validate(ManifestJsonV2 manifest, bool allowUnknownFileKinds = false)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Magic != Magic
            || manifest.FileFormatVersion != PersistenceContractV2.FileFormatVersion
            || manifest.MinimumReadableVersion != PersistenceContractV2.FileFormatVersion
            || manifest.ManifestSchemaVersion != PersistenceContractV2.ManifestSchemaVersion)
        {
            throw new InvalidDataException("manifest.json magic or v2 version fields are invalid.");
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

            bool eventInstrument = file.Kind == "event-instrument-pb";
            bool reusedStructural = ReusedStructuralKinds.Contains(file.Kind);
            bool unknown = !eventInstrument && !reusedStructural;
            bool schemaInvalid = eventInstrument
                ? file.SchemaVersion != PersistenceContractV2.EventInstrumentSchemaVersion
                : reusedStructural
                    ? file.SchemaVersion != PersistenceContractV2.ReusedComponentSchemaVersion
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
    }
}
