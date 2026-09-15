using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Application;
using Midora.Common;
using Midora.Compiler;

namespace Midora.Desktop;

public sealed record NoteSplitPreset(
    int SchemaVersion,
    string ToolId,
    int ToolVersion,
    string ExpressionProfileId,
    int ExpressionProfileVersion,
    string Name,
    NoteSplitMode Mode,
    long FixedPieceLengthTicks,
    int MaximumPieceCount,
    string Expression,
    int MaximumCuts)
{
    [JsonRequired]
    public int NumericContractVersion { get; init; } =
        NoteSplitPresetStore.CurrentNumericContractVersion;
}

public sealed record NoteSplitPresetInfo(string Path, NoteSplitPreset Preset)
{
    public string Name => Preset.Name;

    public string Preview => Preset.Mode switch
    {
        NoteSplitMode.FixedPieceLength => $"Fixed Piece Length\n{Preset.FixedPieceLengthTicks} Ticks",
        NoteSplitMode.MaximumPieceCount => $"Maximum Piece Count\n{Preset.MaximumPieceCount} pieces",
        NoteSplitMode.Expression => $"Expression\n{Preset.Expression}\nMaximum Cuts: {Preset.MaximumCuts}",
        _ => "Unsupported preset"
    };
}

public sealed class NoteSplitPresetStore
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentToolId = "midora.tool.note-split";
    public const int CurrentToolVersion = 1;
    public const int CurrentNumericContractVersion = 1;
    public const int MaximumAllowedCuts = 16_777_216;
    public const int MaximumPresetFileBytes = 65_536;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string _directory;

    public NoteSplitPresetStore(string? presetsDirectory = null)
    {
        string root = presetsDirectory ?? MidoraProgramData.Current.PresetsDirectory;
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("The preset root must be an absolute path.", nameof(presetsDirectory));
        _directory = Path.Combine(Path.GetFullPath(root), "NoteSplitPresets");
    }

    public string DirectoryPath => _directory;

    public IReadOnlyList<NoteSplitPresetInfo> Load()
    {
        if (!Directory.Exists(_directory)) return [];
        List<NoteSplitPresetInfo> result = [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                NoteSplitPreset preset = ReadAndValidateJson(path);
                result.Add(new(path, NormalizeAndValidate(preset)));
            }
            catch (Exception exception) when (IsIsolatedPresetFailure(exception))
            {
                // Presets are independent. A damaged, inaccessible, or obsolete file
                // is omitted without hiding other valid presets.
            }
        }
        return result.OrderBy(static value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public NoteSplitPresetInfo Save(NoteSplitPreset preset)
    {
        NoteSplitPreset normalized = NormalizeAndValidate(preset);
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, normalized.Name + ".json");
        if (Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly)
            .Any(existing => string.Equals(
                Path.GetFileNameWithoutExtension(existing),
                normalized.Name,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException($"A Note Split preset named '{normalized.Name}' already exists.");
        }

        string temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
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

    public void Delete(NoteSplitPresetInfo preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        string expectedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_directory));
        string path = Path.GetFullPath(preset.Path);
        string? parent = Path.GetDirectoryName(path);
        if (parent is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                expectedRoot,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The preset path is outside the Note Split preset directory.");
        }
        File.Delete(path);
    }

    public static NoteSplitPreset NormalizeAndValidate(NoteSplitPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("The Note Split preset schema version is unsupported.");
        if (!string.Equals(preset.ToolId, CurrentToolId, StringComparison.Ordinal)
            || preset.ToolVersion != CurrentToolVersion)
        {
            throw new InvalidDataException("The Note Split preset tool identity is unsupported.");
        }
        if (preset.NumericContractVersion != CurrentNumericContractVersion)
            throw new InvalidDataException("The Note Split preset numeric contract version is unsupported.");
        NumericExpressionProfile profile = NumericExpressionProfiles.NoteSplit;
        if (!string.Equals(preset.ExpressionProfileId, profile.Id, StringComparison.Ordinal)
            || preset.ExpressionProfileVersion != profile.Version)
        {
            throw new InvalidDataException("The Note Split expression profile is unsupported.");
        }
        if (!Enum.IsDefined(preset.Mode))
            throw new InvalidDataException("The Note Split preset mode is invalid.");
        string name = ValidateName(preset.Name);
        if (preset.FixedPieceLengthTicks < 1)
            throw new InvalidDataException("Fixed Piece Length must be a positive Int64 Tick value.");
        if (preset.MaximumPieceCount < 1)
            throw new InvalidDataException("Maximum Piece Count must be a positive Int32 value.");
        if (preset.MaximumCuts is < 1 or > MaximumAllowedCuts)
            throw new InvalidDataException($"Maximum Cuts must be within 1–{MaximumAllowedCuts:N0}.");

        string expressionSource = preset.Expression ?? string.Empty;
        if (expressionSource.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("A Note Split preset expression must contain exactly one logical line.");
        string expression = expressionSource.Trim();
        if (preset.Mode == NoteSplitMode.Expression)
        {
            using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile(expression);
        }
        else if (expression.Length > 0)
        {
            using NoteSplitExpressionProgram program = NoteSplitExpressionProgram.Compile(expression);
        }

        return preset with { Name = name, Expression = expression };
    }

    private static string ValidateName(string? value)
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
        string stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9')
        {
            throw new ArgumentException("The preset name is reserved by Windows.", nameof(value));
        }
        return name;
    }

    private static bool IsIsolatedPresetFailure(Exception exception) => exception is
        JsonException or IOException or UnauthorizedAccessException or
        InvalidDataException or ArgumentException or OverflowException;

    private static NoteSplitPreset ReadAndValidateJson(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumPresetFileBytes)
        {
            throw new InvalidDataException(
                $"The Note Split preset exceeds the {MaximumPresetFileBytes:N0}-byte size limit.");
        }

        byte[] utf8 = new byte[checked((int)stream.Length)];
        stream.ReadExactly(utf8);
        using (JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64
        }))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The Note Split preset JSON root must be an object.");

            HashSet<string> propertyNames = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                    throw new InvalidDataException(
                        $"The Note Split preset contains duplicate property '{property.Name}'.");
            }
        }

        return JsonSerializer.Deserialize<NoteSplitPreset>(utf8, JsonOptions)
            ?? throw new InvalidDataException("The Note Split preset JSON is empty.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
