using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Application;
using Midora.Common;
using Midora.Compiler;

namespace Midora.Desktop;

public sealed record NoteGenerationPresetFields(
    double InitialVelocity, double InitialKey, double InitialGate, double InitialTick,
    string VelocityExpression, string KeyExpression, string GateExpression, string TickExpression);

public sealed record EventGenerationPresetFields(
    double InitialValue, double InitialTick, string ValueExpression, string TickExpression);

/// <summary>Portable tool configuration; deliberately has no owner, target, or absolute Base Tick.</summary>
public sealed record TimelineGenerationPreset(
    int SchemaVersion, string ToolId, int ToolVersion,
    string ExpressionProfileId, int ExpressionProfileVersion, int NumericContractVersion,
    string Name, int MaximumCandidates, long? MaximumRelativeStartTick,
    bool CreateFirstFromInitialValues,
    NoteGenerationPresetFields? Note, EventGenerationPresetFields? Event)
{
    public static TimelineGenerationPreset FromNotes(string name, NoteGenerationOptions options) => new(
        1, TimelineGenerationPresetStore.NoteToolId, 1,
        NumericExpressionProfiles.GenerateNote.Id, NumericExpressionProfiles.GenerateNote.Version, 1,
        name, options.MaximumCandidates, options.MaximumRelativeStartTick, options.CreateFirstFromInitialValues,
        new(options.InitialVelocity, options.InitialKey, options.InitialGate, options.InitialTick,
            options.VelocityExpression ?? string.Empty, options.KeyExpression ?? string.Empty,
            options.GateExpression ?? string.Empty, options.TickExpression ?? string.Empty), null);

    public static TimelineGenerationPreset FromEvents(string name, EventGenerationOptions options) => new(
        1, TimelineGenerationPresetStore.EventToolId, 1,
        NumericExpressionProfiles.GenerateEvent.Id, NumericExpressionProfiles.GenerateEvent.Version, MidiEditingValueDomain.NumericContractVersion,
        name, options.MaximumCandidates, options.MaximumRelativeStartTick, options.CreateFirstFromInitialValues,
        null, new(options.InitialValue, options.InitialTick,
            options.ValueExpression ?? string.Empty, options.TickExpression ?? string.Empty));

    public NoteGenerationOptions ToNoteOptions(long baseTick)
    {
        if (Note is null || Event is not null) throw new InvalidDataException("This is not a Note Generation preset.");
        return new()
        {
            BaseTick = baseTick, MaximumCandidates = MaximumCandidates,
            MaximumRelativeStartTick = MaximumRelativeStartTick,
            CreateFirstFromInitialValues = CreateFirstFromInitialValues,
            InitialVelocity = Note.InitialVelocity, InitialKey = Note.InitialKey,
            InitialGate = Note.InitialGate, InitialTick = Note.InitialTick,
            VelocityExpression = Note.VelocityExpression, KeyExpression = Note.KeyExpression,
            GateExpression = Note.GateExpression, TickExpression = Note.TickExpression
        };
    }

    public EventGenerationOptions ToEventOptions(long baseTick)
    {
        if (Event is null || Note is not null) throw new InvalidDataException("This is not an Event Generation preset.");
        return new()
        {
            BaseTick = baseTick, MaximumCandidates = MaximumCandidates,
            MaximumRelativeStartTick = MaximumRelativeStartTick,
            CreateFirstFromInitialValues = CreateFirstFromInitialValues,
            InitialValue = Event.InitialValue, InitialTick = Event.InitialTick,
            ValueExpression = Event.ValueExpression, TickExpression = Event.TickExpression
        };
    }
}

public sealed record TimelineGenerationPresetInfo(string Path, TimelineGenerationPreset Preset)
{
    public string Name => Preset.Name;
    public string Preview
    {
        get
        {
            string fields = Preset.Note is { } note
                ? $"Velocity: {note.VelocityExpression}\n  Initial: {Format(note.InitialVelocity)}\nKey: {note.KeyExpression}\n  Initial: {Format(note.InitialKey)}\nGate: {note.GateExpression}\n  Initial: {Format(note.InitialGate)}\nTick: {note.TickExpression}\n  Initial: {Format(note.InitialTick)}"
                : Preset.Event is { } point
                    ? $"Value: {point.ValueExpression}\n  Initial: {Format(point.InitialValue)}\nTick: {point.TickExpression}\n  Initial: {Format(point.InitialTick)}"
                    : string.Empty;
            return $"{fields}\n\nMaximum Candidates: {Preset.MaximumCandidates:N0}\nMaximum Relative Start Tick: {Preset.MaximumRelativeStartTick?.ToString(CultureInfo.InvariantCulture) ?? "None"}\nCreate initial object: {(Preset.CreateFirstFromInitialValues ? "Yes" : "No")}";
        }
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

public sealed class TimelineGenerationPresetStore
{
    public const string NoteToolId = "midora.tool.generate-note";
    public const string EventToolId = "midora.tool.generate-event";
    public const int CurrentSchemaVersion = 1;
    public const int CurrentToolVersion = 1;
    public const int CurrentNumericContractVersion = 1;
    public const int MaximumPresetFileBytes = 262_144;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16
    };
    private readonly bool _notes;
    private readonly string _directory;

    public TimelineGenerationPresetStore(bool notes, string? presetsDirectory = null)
    {
        _notes = notes;
        string root = presetsDirectory ?? MidoraProgramData.Current.PresetsDirectory;
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("The preset root must be an absolute path.", nameof(presetsDirectory));
        _directory = Path.Combine(Path.GetFullPath(root), notes ? "NoteGenerationPresets" : "EventGenerationPresets");
    }

    public string DirectoryPath => _directory;
    public bool Notes => _notes;
    public int OmittedPresetCount { get; private set; }

    public IReadOnlyList<TimelineGenerationPresetInfo> Load()
    {
        OmittedPresetCount = 0;
        if (!Directory.Exists(_directory)) return [];
        List<TimelineGenerationPresetInfo> result = [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            try { result.Add(new(path, NormalizeAndValidate(ToolPresetJson.Read<TimelineGenerationPreset>(path, JsonOptions), _notes))); }
            catch (Exception exception) when (IsPresetFailure(exception))
            {
                OmittedPresetCount++;
                // One damaged or obsolete file does not hide the other independent presets.
            }
        }
        return result.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public TimelineGenerationPresetInfo Save(TimelineGenerationPreset preset)
    {
        TimelineGenerationPreset normalized = NormalizeAndValidate(preset, _notes);
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, normalized.Name + ".json");
        if (Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly).Any(existing =>
            string.Equals(Path.GetFileNameWithoutExtension(existing).Normalize(NormalizationForm.FormC), normalized.Name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException($"A preset named '{normalized.Name}' already exists.");
        string temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, normalized, JsonOptions);
                if (stream.Length > MaximumPresetFileBytes) throw new InvalidDataException("The preset exceeds its file size limit.");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(path, normalized);
    }

    public void Delete(TimelineGenerationPresetInfo preset)
    {
        string path = Path.GetFullPath(preset.Path);
        if (!string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(_directory), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The preset path is outside this tool's preset directory.");
        File.Delete(path);
    }

    public static TimelineGenerationPreset NormalizeAndValidate(TimelineGenerationPreset preset, bool notes)
    {
        ArgumentNullException.ThrowIfNull(preset);
        NumericExpressionProfile profile = notes ? NumericExpressionProfiles.GenerateNote : NumericExpressionProfiles.GenerateEvent;
        if (preset.SchemaVersion != CurrentSchemaVersion || preset.ToolVersion != CurrentToolVersion
            || preset.NumericContractVersion != (notes ? CurrentNumericContractVersion : MidiEditingValueDomain.NumericContractVersion)
            || preset.ToolId != (notes ? NoteToolId : EventToolId)
            || preset.ExpressionProfileId != profile.Id || preset.ExpressionProfileVersion != profile.Version)
            throw new InvalidDataException("The generation preset schema, tool, expression profile, or numeric contract version is unsupported.");

        string name = ValidateName(preset.Name);
        TimelineGenerationPreset normalized;
        if (notes)
        {
            if (preset.Note is not { } value || preset.Event is not null) throw new InvalidDataException("Expected Note Generation fields only.");
            normalized = preset with { Name = name, Note = value with
            {
                VelocityExpression = NormalizeExpression(value.VelocityExpression), KeyExpression = NormalizeExpression(value.KeyExpression),
                GateExpression = NormalizeExpression(value.GateExpression), TickExpression = NormalizeExpression(value.TickExpression)
            }};
            normalized.ToNoteOptions(0).Validate();
        }
        else
        {
            if (preset.Event is not { } value || preset.Note is not null) throw new InvalidDataException("Expected Event Generation fields only.");
            normalized = preset with { Name = name, Event = value with
            {
                ValueExpression = NormalizeExpression(value.ValueExpression), TickExpression = NormalizeExpression(value.TickExpression)
            }};
            normalized.ToEventOptions(0).Validate();
        }
        return normalized;
    }

    private static string NormalizeExpression(string? expression)
    {
        if (expression is null || expression.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("Preset expressions must be non-null, single logical lines; an empty string means identity.");
        return expression.Trim();
    }

    private static string ValidateName(string? value)
    {
        string name = (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormC);
        if (name.Length is 0 or > 100 || name is "." or ".." || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Preset names must contain 1–100 filename-safe characters and cannot end with a period.");
        string stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && stem[3] is >= '1' and <= '9') throw new ArgumentException("The preset name is reserved by Windows.");
        return name;
    }



    internal static bool IsPresetFailure(Exception exception) => exception is
        JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException;
}
