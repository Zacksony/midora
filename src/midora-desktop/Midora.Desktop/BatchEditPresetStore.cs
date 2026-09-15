using System.IO;
using System.Text.Json;
using Midora.Common;

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
    string Tick);

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
        WriteIndented = true
    };
    private readonly string _root;

    public BatchEditPresetStore(string? localApplicationData = null)
    {
        _root = localApplicationData is null
            ? MidoraProgramData.Current.PresetsDirectory
            : Path.GetFullPath(Path.Combine(localApplicationData, "Midora", "Presets"));
    }

    public IReadOnlyList<BatchEditPresetInfo> Load(BatchEditPresetKind kind)
    {
        string directory = GetDirectory(kind);
        if (!Directory.Exists(directory)) return [];
        List<BatchEditPresetInfo> result = [];
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                BatchEditPreset preset = JsonSerializer.Deserialize<BatchEditPreset>(
                    File.ReadAllText(path),
                    JsonOptions)
                    ?? throw new InvalidDataException("The preset JSON is empty.");
                if (preset.SchemaVersion != 1 || preset.Kind != kind
                    || string.IsNullOrWhiteSpace(preset.Name))
                {
                    continue;
                }
                result.Add(new(path, preset));
            }
            catch (JsonException)
            {
                // A damaged independent preset must not prevent other presets from loading.
            }
            catch (IOException)
            {
                // A concurrently unavailable preset remains untouched and is omitted this time.
            }
        }
        return result.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public BatchEditPresetInfo Save(BatchEditPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string name = ValidateName(preset.Name);
        BatchEditPreset normalized = preset with { SchemaVersion = 1, Name = name };
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, JsonOptions));
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
