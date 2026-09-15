using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Common;
using Midora.Domain;

namespace Midora.Application;

public sealed record ApplicationPreferencesLoadResult(
    ApplicationPreferences Preferences,
    ApplicationPreferenceNotice? Notice);

public sealed record ApplicationPreferencesSaveResult(
    bool Succeeded,
    ApplicationPreferenceNotice? Notice);

public sealed class ApplicationPreferencesStore
{
    public const int CurrentSchemaVersion = 3;
    private const int PreviousSchemaVersion = 2;
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string _filePath;

    public ApplicationPreferencesStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? GetDefaultFilePath());
    }

    public string FilePath => _filePath;

    public static string GetDefaultFilePath() =>
        MidoraProgramData.Current.PreferencesFilePath;

    public ApplicationPreferencesLoadResult Load()
    {
        try
        {
            byte[] json;
            using (FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        $"The Application Preferences file exceeds {MaximumFileBytes} bytes.");
                }
                json = new byte[checked((int)stream.Length)];
                stream.ReadExactly(json);
            }
            StrictApplicationJson.RejectDuplicateProperties(json);
            ApplicationPreferencesJsonV1 dto = JsonSerializer.Deserialize(
                json,
                ApplicationPreferencesJsonContextV1.Default.ApplicationPreferencesJsonV1)
                ?? throw new InvalidDataException("The Application Preferences JSON is null.");
            if (dto.SchemaVersion is not (PreviousSchemaVersion or CurrentSchemaVersion))
            {
                throw new InvalidDataException(
                    $"Unsupported Application Preferences schema version {dto.SchemaVersion}.");
            }
            ApplicationRecentDirectoriesJsonV1 recentDirectories = dto.RecentDirectories
                ?? throw new InvalidDataException(
                    "Application Preferences recentDirectories is required.");

            DesktopUiPreferencesJsonV1? desktop = dto.DesktopUi;
            ApplicationPreferences preferences = new(
                new RealtimeAudioPreferences(
                    dto.PlaybackOutputDeviceId,
                    dto.RenderAheadMilliseconds,
                    dto.DeviceBufferRequestMilliseconds,
                    dto.RealtimeMaximumSampleVoicesPerUnitStream),
                new AudioCachePreferences(
                    MidoraProgramData.Current.AudioCacheDirectory,
                    dto.MaximumReusableAudioCacheBytes).Normalize(),
                new ApplicationRecentDirectories(
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.OpenProject),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.SaveAndSaveCopy),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.SoundFont),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.MidiExport),
                    ApplicationPreferences.NormalizeDirectory(recentDirectories.AudioRender)))
            {
                SoundFonts = (dto.SoundFonts
                        ?? throw new InvalidDataException(
                            "Application Preferences soundFonts is required."))
                    .Select(value => new ApplicationSoundFontPreference(
                        ReadSoundFontEntryId(value, dto.SchemaVersion),
                        value.Path,
                        value.Enabled,
                        ReadTarget(value)).Normalize())
                    .ToArray(),
                DesktopUi = desktop is null
                    ? DesktopUiPreferences.Default
                    : new DesktopUiPreferences(
                        desktop.MainWindowWidth,
                        desktop.MainWindowHeight,
                        desktop.MainWindowLeft,
                        desktop.MainWindowTop,
                        desktop.MainWindowMaximized,
                        desktop.ProjectPanelWidth,
                        desktop.BottomPanelHeight,
                        desktop.TimelineSnapEnabled,
                        desktop.TimelineGridDivisionsPerQuarter)
                    {
                        ProjectPanelVisible = desktop.ProjectPanelVisible ?? true,
                        BottomPanelVisible = desktop.BottomPanelVisible ?? true,
                        FollowPlayback = desktop.FollowPlayback
                            ?? DesktopUiPreferences.Default.FollowPlayback
                    },
                Playback = new PlaybackPreferences(
                    dto.MasterVolumeDecibels ?? PlaybackPreferences.Default.MasterVolumeDecibels,
                    dto.LimiterEnabled ?? PlaybackPreferences.Default.LimiterEnabled,
                    ParseStopCursorBehavior(dto.StopCursorBehavior)),
                Appearance = new AppearancePreferences(
                    dto.Language ?? AppearancePreferences.Default.Language),
                InstrumentAudition = dto.InstrumentAudition ?? InstrumentAuditionPreferences.Default
            };
            preferences.Validate();
            return new(preferences, null);
        }
        catch (FileNotFoundException)
        {
            return new(ApplicationPreferences.Default, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(ApplicationPreferences.Default, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new(
                ApplicationPreferences.Default,
                new ApplicationPreferenceNotice(
                    "PreferenceReadFailed",
                    "Application Preferences could not be read; safe defaults are in use.",
                    exception));
        }
    }

    public ApplicationPreferencesSaveResult Save(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException(
                    "The Application Preferences path has no parent directory.");
            Directory.CreateDirectory(directory);
            ApplicationPreferencesJsonV1 dto = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                PlaybackOutputDeviceId = preferences.RealtimeAudio.PlaybackOutputDeviceId,
                RenderAheadMilliseconds = preferences.RealtimeAudio.RenderAheadMilliseconds,
                DeviceBufferRequestMilliseconds =
                    preferences.RealtimeAudio.DeviceBufferRequestMilliseconds,
                RealtimeMaximumSampleVoicesPerUnitStream =
                    preferences.RealtimeAudio.MaximumSampleVoicesPerUnitStream,
                MaximumReusableAudioCacheBytes = preferences.AudioCache.MaximumReusableBytes,
                MasterVolumeDecibels = preferences.Playback.MasterVolumeDecibels,
                LimiterEnabled = preferences.Playback.LimiterEnabled,
                StopCursorBehavior = preferences.Playback.StopCursorBehavior.ToString(),
                Language = preferences.Appearance.Language,
                InstrumentAudition = preferences.InstrumentAudition,
                SoundFonts = preferences.SoundFonts
                    .Select(value => new ApplicationSoundFontPreferenceJsonV1
                    {
                        EntryId = value.EntryId.ToString(),
                        Path = Path.GetFullPath(value.Path),
                        Enabled = value.Enabled,
                        TargetBankMsb = value.Target?.BankMsb,
                        TargetBankLsb = value.Target?.BankLsb,
                        TargetProgram = value.Target?.Program
                    })
                    .ToList(),
                RecentDirectories = new ApplicationRecentDirectoriesJsonV1
                {
                    OpenProject = preferences.RecentDirectories.OpenProject,
                    SaveAndSaveCopy = preferences.RecentDirectories.SaveAndSaveCopy,
                    SoundFont = preferences.RecentDirectories.SoundFont,
                    MidiExport = preferences.RecentDirectories.MidiExport,
                    AudioRender = preferences.RecentDirectories.AudioRender
                },
                DesktopUi = new DesktopUiPreferencesJsonV1
                {
                    MainWindowWidth = preferences.DesktopUi.MainWindowWidth,
                    MainWindowHeight = preferences.DesktopUi.MainWindowHeight,
                    MainWindowLeft = preferences.DesktopUi.MainWindowLeft,
                    MainWindowTop = preferences.DesktopUi.MainWindowTop,
                    MainWindowMaximized = preferences.DesktopUi.MainWindowMaximized,
                    ProjectPanelWidth = preferences.DesktopUi.ProjectPanelWidth,
                    BottomPanelHeight = preferences.DesktopUi.BottomPanelHeight,
                    TimelineSnapEnabled = preferences.DesktopUi.TimelineSnapEnabled,
                    TimelineGridDivisionsPerQuarter = preferences.DesktopUi.TimelineGridDivisionsPerQuarter,
                    ProjectPanelVisible = preferences.DesktopUi.ProjectPanelVisible,
                    BottomPanelVisible = preferences.DesktopUi.BottomPanelVisible,
                    FollowPlayback = preferences.DesktopUi.FollowPlayback
                }
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                dto,
                ApplicationPreferencesJsonContextV1.Default.ApplicationPreferencesJsonV1);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
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
                    "PreferenceWriteFailed",
                    "Application Preferences could not be saved; safe defaults are in use.",
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
                    // A failed best-effort cleanup does not replace the original write error.
                }
            }
        }
    }

    private static Midora.Audio.SoundFontTarget? ReadTarget(
        ApplicationSoundFontPreferenceJsonV1 value)
    {
        bool hasMsb = value.TargetBankMsb.HasValue;
        bool hasLsb = value.TargetBankLsb.HasValue;
        bool hasProgram = value.TargetProgram.HasValue;
        if (!hasMsb && !hasLsb && !hasProgram)
        {
            return null;
        }
        if (!hasMsb || !hasLsb || !hasProgram)
        {
            throw new InvalidDataException(
                "A SoundFont target mapping must specify Bank MSB, Bank LSB, and Program together.");
        }
        return new(
            value.TargetBankMsb!.Value,
            value.TargetBankLsb!.Value,
            value.TargetProgram!.Value);
    }

    private static SoundFontEntryId ReadSoundFontEntryId(
        ApplicationSoundFontPreferenceJsonV1 value,
        int schemaVersion)
    {
        if (schemaVersion == PreviousSchemaVersion)
        {
            if (value.EntryId is not null)
            {
                throw new InvalidDataException(
                    "Application Preferences schema 2 cannot contain a SoundFont entry ID.");
            }
            return SoundFontEntryId.CreateForSchema2Migration(value.Path);
        }
        if (value.EntryId is null)
        {
            throw new InvalidDataException(
                "Application Preferences schema 3 requires every SoundFont entry ID.");
        }
        try
        {
            return SoundFontEntryId.Parse(value.EntryId);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new InvalidDataException("A SoundFont entry ID is invalid.", exception);
        }
    }

    private static StopCursorBehavior ParseStopCursorBehavior(string? value)
    {
        if (value is null)
        {
            return PlaybackPreferences.Default.StopCursorBehavior;
        }
        if (!Enum.TryParse(value, ignoreCase: false, out StopCursorBehavior result)
            || !Enum.IsDefined(result))
        {
            throw new InvalidDataException(
                $"Unknown stop cursor behavior '{value}'.");
        }
        return result;
    }
}

internal sealed class ApplicationPreferencesJsonV1
{
    [JsonPropertyOrder(20)]
    public InstrumentAuditionPreferences? InstrumentAudition { get; set; }

    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; }

    [JsonPropertyOrder(1)]
    public string? PlaybackOutputDeviceId { get; set; }

    [JsonPropertyOrder(2)]
    public int RenderAheadMilliseconds { get; set; }

    [JsonPropertyOrder(3)]
    public int DeviceBufferRequestMilliseconds { get; set; }

    [JsonPropertyOrder(4)]
    public int RealtimeMaximumSampleVoicesPerUnitStream { get; set; }

    [JsonPropertyOrder(5)]
    public required long MaximumReusableAudioCacheBytes { get; set; }

    [JsonPropertyOrder(6)]
    public ApplicationRecentDirectoriesJsonV1? RecentDirectories { get; set; }

    [JsonPropertyOrder(7)]
    public DesktopUiPreferencesJsonV1? DesktopUi { get; set; }

    [JsonPropertyOrder(8)]
    public List<ApplicationSoundFontPreferenceJsonV1>? SoundFonts { get; set; }

    [JsonPropertyOrder(9)]
    public double? MasterVolumeDecibels { get; set; }

    [JsonPropertyOrder(10)]
    public bool? LimiterEnabled { get; set; }

    [JsonPropertyOrder(11)]
    public string? StopCursorBehavior { get; set; }

    [JsonPropertyOrder(12)]
    public string? Language { get; set; }
}

internal sealed class ApplicationSoundFontPreferenceJsonV1
{
    [JsonPropertyOrder(0)]
    public string? EntryId { get; set; }

    [JsonPropertyOrder(1)]
    public required string Path { get; set; }

    [JsonPropertyOrder(2)]
    public bool Enabled { get; set; }

    [JsonPropertyOrder(3)]
    public byte? TargetBankMsb { get; set; }

    [JsonPropertyOrder(4)]
    public byte? TargetBankLsb { get; set; }

    [JsonPropertyOrder(5)]
    public byte? TargetProgram { get; set; }
}

internal sealed class DesktopUiPreferencesJsonV1
{
    [JsonPropertyOrder(0)] public double MainWindowWidth { get; set; }
    [JsonPropertyOrder(1)] public double MainWindowHeight { get; set; }
    [JsonPropertyOrder(2)] public double? MainWindowLeft { get; set; }
    [JsonPropertyOrder(3)] public double? MainWindowTop { get; set; }
    [JsonPropertyOrder(4)] public bool MainWindowMaximized { get; set; }
    [JsonPropertyOrder(5)] public double ProjectPanelWidth { get; set; }
    [JsonPropertyOrder(6)] public double BottomPanelHeight { get; set; }
    [JsonPropertyOrder(7)] public bool TimelineSnapEnabled { get; set; }
    [JsonPropertyOrder(8)] public int TimelineGridDivisionsPerQuarter { get; set; }
    [JsonPropertyOrder(9)] public bool? ProjectPanelVisible { get; set; }
    [JsonPropertyOrder(10)] public bool? BottomPanelVisible { get; set; }
    [JsonPropertyOrder(11)] public bool? FollowPlayback { get; set; }
    [JsonPropertyName("inspectorWidth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonPropertyOrder(12)]
    public double LegacyInspectorWidth { get; set; }
    [JsonPropertyName("inspectorVisible")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(13)]
    public bool? LegacyInspectorVisible { get; set; }
}

internal sealed class ApplicationRecentDirectoriesJsonV1
{
    [JsonPropertyOrder(0)]
    public string? OpenProject { get; set; }

    [JsonPropertyOrder(1)]
    public string? SaveAndSaveCopy { get; set; }

    [JsonPropertyOrder(2)]
    public string? SoundFont { get; set; }

    [JsonPropertyOrder(3)]
    public string? MidiExport { get; set; }

    [JsonPropertyOrder(4)]
    public string? AudioRender { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApplicationPreferencesJsonV1))]
internal sealed partial class ApplicationPreferencesJsonContextV1 : JsonSerializerContext;
