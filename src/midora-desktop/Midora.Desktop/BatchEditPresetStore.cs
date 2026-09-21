using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Common;
using Midora.Application;
using Midora.Compiler;

namespace Midora.Desktop;

public enum BatchEditPresetKind
{
    Note,
    Event
}

public sealed record BatchEditPreset(
    int SchemaVersion,
    string Name,
    BatchEditPresetKind Kind,
    string Velocity,
    string PointValue,
    string KeyNumber,
    string Gate,
    string Tick)
{
    public string? ExpressionProfileId { get; init; }
    public int ExpressionProfileVersion { get; init; }
    public int NumericContractVersion { get; init; }
}

public sealed record BatchEditPresetInfo(string Path, BatchEditPreset Preset)
{
    public string Name => Preset.Name;
    public string Preview => Preset.Kind == BatchEditPresetKind.Note
        ? $"Velocity: {Display(Preset.Velocity)}\nKey Number: {Display(Preset.KeyNumber)}\nGate: {Display(Preset.Gate)}\nTick: {Display(Preset.Tick)}"
        : $"Point Value: {Display(Preset.PointValue)}\nTick: {Display(Preset.Tick)}";

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "(unchanged)" : value;
}

public sealed class BatchEditPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
    private readonly string _root;
    public int OmittedPresetCount { get; private set; }

    public BatchEditPresetStore(string? localApplicationData = null)
    {
        _root = localApplicationData is null
            ? MidoraProgramData.Current.PresetsDirectory
            : Path.GetFullPath(Path.Combine(localApplicationData, "Midora", "Presets"));
    }

    public IReadOnlyList<BatchEditPresetInfo> Load(BatchEditPresetKind kind)
    {
        OmittedPresetCount = 0;
        string directory = GetDirectory(kind);
        if (!Directory.Exists(directory)) return [];
        List<BatchEditPresetInfo> result = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                BatchEditPreset preset = ToolPresetJson.Read<BatchEditPreset>(path, JsonOptions);
                NumericExpressionProfile profile = kind == BatchEditPresetKind.Note ? NumericExpressionProfiles.BatchNote : NumericExpressionProfiles.BatchEvent;
                bool legacyNote = kind == BatchEditPresetKind.Note && preset.SchemaVersion == 1;
                if (preset.Kind != kind || string.IsNullOrWhiteSpace(preset.Name)
                    || !legacyNote && (preset.SchemaVersion != 2
                        || preset.ExpressionProfileId != profile.Id || preset.ExpressionProfileVersion != profile.Version
                        || preset.NumericContractVersion != (kind == BatchEditPresetKind.Note ? 1 : MidiEditingValueDomain.NumericContractVersion)))
                {
                    OmittedPresetCount++;
                    continue;
                }
                ValidateExpressions(preset);
                result.Add(new(path, preset));
            }
            catch (Exception exception) when (TimelineGenerationPresetStore.IsPresetFailure(exception))
            {
                OmittedPresetCount++;
                // Leave obsolete, malformed, or unavailable independent files untouched.
            }
        }
        return result.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public BatchEditPresetInfo Save(BatchEditPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string name = ValidateName(preset.Name);
        NumericExpressionProfile profile = preset.Kind == BatchEditPresetKind.Note ? NumericExpressionProfiles.BatchNote : NumericExpressionProfiles.BatchEvent;
        BatchEditPreset normalized = preset with
        {
            SchemaVersion = 2, Name = name, ExpressionProfileId = profile.Id, ExpressionProfileVersion = profile.Version,
            NumericContractVersion = preset.Kind == BatchEditPresetKind.Note ? 1 : MidiEditingValueDomain.NumericContractVersion
        };
        ValidateExpressions(normalized);
        string directory = GetDirectory(preset.Kind);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".json");
        if (Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Any(existing => string.Equals(
                Path.GetFileNameWithoutExtension(existing),
                name,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"A preset named '{name}' already exists.");
        }
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                if (stream.Length > ToolPresetJson.MaximumBytes) throw new InvalidDataException("The preset exceeds its file size limit.");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new(path, normalized);
    }

    public void Delete(BatchEditPresetInfo preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string expectedRoot = Path.GetFullPath(GetDirectory(preset.Preset.Kind))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(preset.Path);
        if (!path.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The preset path is outside the Midora preset directory.");
        }
        File.Delete(path);
    }

    private string GetDirectory(BatchEditPresetKind kind) => Path.Combine(
        _root,
        kind switch
        {
            BatchEditPresetKind.Note => "NoteBatchPresets",
            BatchEditPresetKind.Event => "EventBatchPresets",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        });

    private static void ValidateExpressions(BatchEditPreset preset)
    {
        using var program = BatchEditExpressionProgram.Compile(preset.Kind == BatchEditPresetKind.Note
            ? new Dictionary<BatchEditField, string?> { [BatchEditField.Velocity] = preset.Velocity,
                [BatchEditField.KeyNumber] = preset.KeyNumber, [BatchEditField.Gate] = preset.Gate, [BatchEditField.Tick] = preset.Tick }
            : new Dictionary<BatchEditField, string?> { [BatchEditField.PointValue] = preset.PointValue, [BatchEditField.Tick] = preset.Tick });
    }

    private static string ValidateName(string value)
    {
        string name = (value ?? string.Empty).Trim();
        if (name.Length is 0 or > 100
            || name is "." or ".."
            || name.EndsWith(' ')
            || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "Preset names must contain 1–100 filename-safe characters and cannot end with a space or period.",
                nameof(value));
        }
        return name;
    }
}
