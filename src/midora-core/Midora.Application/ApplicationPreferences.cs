using Midora.Audio;
using Midora.Common;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application;

public enum RecentDirectoryPurpose
{
    OpenProject,
    SaveAndSaveCopy,
    SoundFont,
    MidiExport,
    AudioRender
}

public sealed record RealtimeAudioPreferences(
    string? PlaybackOutputDeviceId,
    int RenderAheadMilliseconds,
    int DeviceBufferRequestMilliseconds,
    int MaximumSampleVoicesPerUnitStream)
{
    public const int DefaultRenderAheadMilliseconds = 100;
    public const int DefaultDeviceBufferRequestMilliseconds = 50;
    public const int DefaultMaximumSampleVoicesPerUnitStream = 500;
    public const int MinimumRenderAheadMilliseconds = 20;
    public const int MaximumRenderAheadMilliseconds = 2_000;
    public const int MinimumDeviceBufferRequestMilliseconds = 5;
    public const int MaximumDeviceBufferRequestMilliseconds = 200;
    public const int MinimumSampleVoicesPerUnitStream = 1;
    public const int MaximumAllowedSampleVoicesPerUnitStream = 16_777_216;

    public static RealtimeAudioPreferences Default { get; } = new(
        null,
        DefaultRenderAheadMilliseconds,
        DefaultDeviceBufferRequestMilliseconds,
        DefaultMaximumSampleVoicesPerUnitStream);

    public void Validate()
    {
        if (PlaybackOutputDeviceId is { Length: 0 })
        {
            throw new ArgumentException(
                "The playback output device ID must be null for System Default or non-empty.",
                nameof(PlaybackOutputDeviceId));
        }
        if (RenderAheadMilliseconds is < MinimumRenderAheadMilliseconds
            or > MaximumRenderAheadMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(RenderAheadMilliseconds));
        }
        if (DeviceBufferRequestMilliseconds is < MinimumDeviceBufferRequestMilliseconds
            or > MaximumDeviceBufferRequestMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(DeviceBufferRequestMilliseconds));
        }
        if (MaximumSampleVoicesPerUnitStream is < MinimumSampleVoicesPerUnitStream
            or > MaximumAllowedSampleVoicesPerUnitStream)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumSampleVoicesPerUnitStream));
        }
    }
}

public sealed record AudioCachePreferences(
    string RootPath,
    long MaximumReusableBytes)
{
    public const long DefaultMaximumReusableBytes = 16L * 1024 * 1024 * 1024;

    public static AudioCachePreferences Default { get; } = new(
        GetDefaultRootPath(),
        DefaultMaximumReusableBytes);

    public static string GetDefaultRootPath() =>
        MidoraProgramData.Current.AudioCacheDirectory;

    public void Validate()
    {
        if (MaximumReusableBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReusableBytes));
        }
        _ = NormalizeRootPath(RootPath);
    }

    public AudioCachePreferences Normalize() => this with
    {
        RootPath = NormalizeRootPath(RootPath)
    };

    internal static string NormalizeRootPath(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath)
            || rootPath.StartsWith("\\\\", StringComparison.Ordinal)
            || rootPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The audio cache root must be a fully-qualified local path; UNC and network paths are not supported.",
                nameof(rootPath));
        }

        string fullPath = Path.GetFullPath(rootPath);
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
        {
            throw new ArgumentException("The audio cache root has no local volume root.", nameof(rootPath));
        }
        return string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }
}

public sealed record ApplicationRecentDirectories(
    string? OpenProject,
    string? SaveAndSaveCopy,
    string? SoundFont,
    string? MidiExport,
    string? AudioRender)
{
    public static ApplicationRecentDirectories Empty { get; } =
        new(null, null, null, null, null);

    public string? Get(RecentDirectoryPurpose purpose) => purpose switch
    {
        RecentDirectoryPurpose.OpenProject => OpenProject,
        RecentDirectoryPurpose.SaveAndSaveCopy => SaveAndSaveCopy,
        RecentDirectoryPurpose.SoundFont => SoundFont,
        RecentDirectoryPurpose.MidiExport => MidiExport,
        RecentDirectoryPurpose.AudioRender => AudioRender,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    internal ApplicationRecentDirectories With(
        RecentDirectoryPurpose purpose,
        string? directory) => purpose switch
        {
            RecentDirectoryPurpose.OpenProject => this with { OpenProject = directory },
            RecentDirectoryPurpose.SaveAndSaveCopy => this with { SaveAndSaveCopy = directory },
            RecentDirectoryPurpose.SoundFont => this with { SoundFont = directory },
            RecentDirectoryPurpose.MidiExport => this with { MidiExport = directory },
            RecentDirectoryPurpose.AudioRender => this with { AudioRender = directory },
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
}

public sealed record PlaybackPreferences(
    double MasterVolumeDecibels,
    bool LimiterEnabled,
    StopCursorBehavior StopCursorBehavior)
{
    public static PlaybackPreferences Default { get; } = new(
        -0.1,
        true,
        StopCursorBehavior.ReturnToPlaybackStart);

    public void Validate()
    {
        if (!double.IsFinite(MasterVolumeDecibels)
            || MasterVolumeDecibels is < -float.MaxValue or > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MasterVolumeDecibels));
        }
        if (!Enum.IsDefined(StopCursorBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(StopCursorBehavior));
        }
    }
}

public sealed record AppearancePreferences(string Language)
{
    public const string EnglishLanguage = "English";
    public static AppearancePreferences Default { get; } = new(EnglishLanguage);
    public bool ShowEventLaneLines { get; init; } = true;

    public void Validate()
    {
        if (!string.Equals(Language, EnglishLanguage, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "English is the only language available in this release.",
                nameof(Language));
        }
    }
}

public sealed record ApplicationSoundFontPreference
{
    public ApplicationSoundFontPreference(
        string path,
        bool enabled,
        SoundFontTarget? target = null)
        : this(SoundFontEntryId.Create(), path, enabled, target)
    {
    }

    public ApplicationSoundFontPreference(
        SoundFontEntryId entryId,
        string path,
        bool enabled,
        SoundFontTarget? target = null)
    {
        EntryId = entryId;
        Path = path;
        Enabled = enabled;
        Target = target;
    }

    public SoundFontEntryId EntryId { get; init; }

    public string Path { get; init; }

    public bool Enabled { get; init; }

    public SoundFontTarget? Target { get; init; }

    public ApplicationSoundFontPreference Normalize()
    {
        Validate();
        return this with { Path = System.IO.Path.GetFullPath(Path) };
    }

    public void Validate()
    {
        EntryId.Validate();
        new SoundFontConfiguration(Path, Target).Validate();
    }

    public SoundFontConfiguration ToConfiguration() =>
        new SoundFontConfiguration(Path, Target).Normalize();
}

public sealed record DesktopUiPreferences(
    double MainWindowWidth,
    double MainWindowHeight,
    double? MainWindowLeft,
    double? MainWindowTop,
    bool MainWindowMaximized,
    double ProjectPanelWidth,
    double BottomPanelHeight,
    bool TimelineSnapEnabled,
    int TimelineGridDivisionsPerQuarter)
{
    public bool ProjectPanelVisible { get; init; } = true;
    public bool BottomPanelVisible { get; init; } = true;
    public bool FollowPlayback { get; init; } = false;

    public static DesktopUiPreferences Default { get; } = new(
        1440,
        900,
        null,
        null,
        false,
        224,
        150,
        true,
        4);

    public void Validate()
    {
        if (!double.IsFinite(MainWindowWidth) || MainWindowWidth is < 1100 or > 32768
            || !double.IsFinite(MainWindowHeight) || MainWindowHeight is < 680 or > 32768
            || MainWindowLeft.HasValue && !double.IsFinite(MainWindowLeft.Value)
            || MainWindowTop.HasValue && !double.IsFinite(MainWindowTop.Value)
            || !double.IsFinite(ProjectPanelWidth) || ProjectPanelWidth is < 170 or > 360
            || !double.IsFinite(BottomPanelHeight) || BottomPanelHeight is < 80 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(DesktopUiPreferences));
        }
        if (TimelineGridDivisionsPerQuarter is not (1 or 2 or 3 or 4 or 6 or 8 or 12 or 16 or 24 or 32 or 48 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(TimelineGridDivisionsPerQuarter));
        }
    }
}

public sealed record InstrumentAuditionPreferences(bool Automatic, int Key, int Velocity, int DurationMilliseconds)
{
    public static InstrumentAuditionPreferences Default { get; } = new(true, 60, 100, 500);
    public void Validate()
    {
        if (Key is < 0 or > 127 || Velocity is < 1 or > 127 || DurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(InstrumentAuditionPreferences));
    }
}

public sealed record ApplicationPreferences(
    RealtimeAudioPreferences RealtimeAudio,
    AudioCachePreferences AudioCache,
    ApplicationRecentDirectories RecentDirectories)
{
    public DesktopUiPreferences DesktopUi { get; init; } = DesktopUiPreferences.Default;
    public IReadOnlyList<ApplicationSoundFontPreference> SoundFonts { get; init; } =
        Array.Empty<ApplicationSoundFontPreference>();
    public PlaybackPreferences Playback { get; init; } = PlaybackPreferences.Default;
    public AppearancePreferences Appearance { get; init; } = AppearancePreferences.Default;
    public InstrumentAuditionPreferences InstrumentAudition { get; init; } = InstrumentAuditionPreferences.Default;

    public static ApplicationPreferences Default { get; } =
        new(
            RealtimeAudioPreferences.Default,
            AudioCachePreferences.Default,
            ApplicationRecentDirectories.Empty);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(RealtimeAudio);
        ArgumentNullException.ThrowIfNull(AudioCache);
        ArgumentNullException.ThrowIfNull(RecentDirectories);
        ArgumentNullException.ThrowIfNull(DesktopUi);
        ArgumentNullException.ThrowIfNull(Playback);
        ArgumentNullException.ThrowIfNull(Appearance);
        RealtimeAudio.Validate();
        AudioCache.Validate();
        DesktopUi.Validate();
        Playback.Validate();
        Appearance.Validate();
        ArgumentNullException.ThrowIfNull(InstrumentAudition);
        InstrumentAudition.Validate();
        ValidateDirectory(RecentDirectories.OpenProject);
        ValidateDirectory(RecentDirectories.SaveAndSaveCopy);
        ValidateDirectory(RecentDirectories.SoundFont);
        ValidateDirectory(RecentDirectories.MidiExport);
        ValidateDirectory(RecentDirectories.AudioRender);
        ArgumentNullException.ThrowIfNull(SoundFonts);
        if (SoundFonts.Count > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SoundFonts),
                "At most 256 SoundFonts can be configured.");
        }
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<SoundFontEntryId> entryIds = [];
        foreach (ApplicationSoundFontPreference soundFont in SoundFonts)
        {
            ArgumentNullException.ThrowIfNull(soundFont);
            soundFont.Validate();
            if (!entryIds.Add(soundFont.EntryId))
            {
                throw new ArgumentException(
                    "The Application SoundFont list contains a duplicate entry ID.",
                    nameof(SoundFonts));
            }
            if (!paths.Add(Path.GetFullPath(soundFont.Path)))
            {
                throw new ArgumentException(
                    "The Application SoundFont list contains a duplicate path.",
                    nameof(SoundFonts));
            }
        }
    }

    internal static string? NormalizeDirectory(string? directory)
    {
        if (directory is null)
        {
            return null;
        }
        ValidateDirectory(directory);
        string fullPath = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(fullPath)!;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void ValidateDirectory(string? directory)
    {
        if (directory is null)
        {
            return;
        }
        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException(
                "A recent directory must be null or a fully-qualified non-empty path.",
                nameof(directory));
        }
        _ = Path.GetFullPath(directory);
    }

    public string[] GetEnabledSoundFontPaths() => SoundFonts
        .Where(value => value.Enabled)
        .Select(value => Path.GetFullPath(value.Path))
        .ToArray();

    public SoundFontConfiguration[] GetEnabledSoundFontConfigurations() => SoundFonts
        .Where(value => value.Enabled)
        .Select(value => value.ToConfiguration())
        .ToArray();
}

public enum ApplicationPreferenceUpdateStatus
{
    Applied,
    RejectedPlaybackNotStopped,
    RejectedApplicationBusy,
    RejectedInvalidValue,
    FailedUsingDefaults
}

public sealed record ApplicationPreferenceNotice(string Code, string Message, Exception? Error = null);

public sealed record ApplicationPreferenceUpdateResult(
    ApplicationPreferenceUpdateStatus Status,
    ApplicationPreferenceNotice? Notice = null)
{
    public bool Succeeded => Status == ApplicationPreferenceUpdateStatus.Applied;
}

public sealed class ApplicationPreferencesService
{
    private readonly object _sync = new();
    private readonly ApplicationPreferencesStore _store;
    private readonly ApplicationTaskCoordinator _tasks;
    private readonly ProjectCompilationSession _session;
    private ApplicationPreferences _current;

    public ApplicationPreferencesService(
        ApplicationPreferencesStore store,
        ApplicationTaskCoordinator tasks,
        ProjectCompilationSession session)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ApplicationPreferencesLoadResult loaded = _store.Load();
        _current = loaded.Preferences;
        StartupNotice = loaded.Notice;
        _session.ConfigureAudioCache(
            _current.AudioCache.RootPath,
            _current.AudioCache.MaximumReusableBytes);
    }

    public ApplicationPreferenceNotice? StartupNotice { get; }

    public ApplicationPreferences Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler? RealtimeAudioPreferencesChanged;
    public event EventHandler? AudioCachePreferencesChanged;
    public event EventHandler? PlaybackPreferencesChanged;
    public event EventHandler? AppearancePreferencesChanged;

    public ApplicationPreferenceUpdateResult UpdatePlayback(
        PlaybackPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            preferences.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with { Playback = preferences },
            realtimeMayChange: false,
            audioCacheMayChange: false,
            playbackMayChange: true,
            appearanceMayChange: false,
            requiresPlaybackStopped: true);
    }

    public ApplicationPreferenceUpdateResult UpdateAppearance(
        AppearancePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            preferences.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with { Appearance = preferences },
            realtimeMayChange: false,
            audioCacheMayChange: false,
            playbackMayChange: false,
            appearanceMayChange: true,
            requiresPlaybackStopped: true);
    }

    public ApplicationPreferenceUpdateResult UpdateRealtimeAudio(
        RealtimeAudioPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            preferences.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with { RealtimeAudio = preferences },
            realtimeMayChange: true,
            audioCacheMayChange: false,
            playbackMayChange: false,
            appearanceMayChange: false,
            requiresPlaybackStopped: true);
    }

    public ApplicationPreferenceUpdateResult UpdateAudioCache(
        AudioCachePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        AudioCachePreferences normalized;
        try
        {
            normalized = preferences.Normalize();
            normalized.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with { AudioCache = normalized },
            realtimeMayChange: false,
            audioCacheMayChange: true,
            playbackMayChange: false,
            appearanceMayChange: false,
            requiresPlaybackStopped: true);
    }

    public ApplicationPreferenceUpdateResult UpdateRecentDirectory(
        RecentDirectoryPurpose purpose,
        string? directory)
    {
        string? normalized;
        try
        {
            normalized = ApplicationPreferences.NormalizeDirectory(directory);
        }
        catch (ArgumentException exception)
        {
            return new(
                ApplicationPreferenceUpdateStatus.RejectedInvalidValue,
                new ApplicationPreferenceNotice(
                    "PreferenceValueInvalid",
                    exception.Message,
                    exception));
        }

        return Persist(
            current => current with
            {
                RecentDirectories = current.RecentDirectories.With(purpose, normalized)
            },
            realtimeMayChange: false,
            audioCacheMayChange: false,
            playbackMayChange: false,
            appearanceMayChange: false,
            requiresPlaybackStopped: false);
    }

    public ApplicationPreferenceUpdateResult ResetToDefaults()
    {
        return Persist(
            _ => ApplicationPreferences.Default,
            realtimeMayChange: true,
            audioCacheMayChange: true,
            playbackMayChange: true,
            appearanceMayChange: true,
            requiresPlaybackStopped: true);
    }

    private ApplicationPreferenceUpdateResult Persist(
        Func<ApplicationPreferences, ApplicationPreferences> update,
        bool realtimeMayChange,
        bool audioCacheMayChange,
        bool playbackMayChange,
        bool appearanceMayChange,
        bool requiresPlaybackStopped)
    {
        IDisposable? admission = _tasks.TryAcquirePreferenceUpdateLock(
            requiresPlaybackStopped,
            out ApplicationPreferenceAdmissionFailure admissionFailure);
        if (admission is null)
        {
            return new(admissionFailure switch
            {
                ApplicationPreferenceAdmissionFailure.PlaybackNotStopped =>
                    ApplicationPreferenceUpdateStatus.RejectedPlaybackNotStopped,
                _ => ApplicationPreferenceUpdateStatus.RejectedApplicationBusy
            });
        }

        bool realtimeChanged;
        bool audioCacheChanged;
        bool playbackChanged;
        bool appearanceChanged;
        ApplicationPreferencesSaveResult saved;
        try
        {
            lock (_sync)
            {
                ApplicationPreferences candidate = update(_current);
                candidate.Validate();
                saved = _store.Save(candidate);
                if (saved.Succeeded)
                {
                    realtimeChanged = realtimeMayChange
                        && !Equals(_current.RealtimeAudio, candidate.RealtimeAudio);
                    audioCacheChanged = audioCacheMayChange
                        && !Equals(_current.AudioCache, candidate.AudioCache);
                    playbackChanged = playbackMayChange
                        && !Equals(_current.Playback, candidate.Playback);
                    appearanceChanged = appearanceMayChange
                        && !Equals(_current.Appearance, candidate.Appearance);
                    _current = candidate;
                }
                else
                {
                    realtimeChanged = !Equals(
                        _current.RealtimeAudio,
                        ApplicationPreferences.Default.RealtimeAudio);
                    audioCacheChanged = !Equals(
                        _current.AudioCache,
                        ApplicationPreferences.Default.AudioCache);
                    playbackChanged = !Equals(
                        _current.Playback,
                        ApplicationPreferences.Default.Playback);
                    appearanceChanged = !Equals(
                        _current.Appearance,
                        ApplicationPreferences.Default.Appearance);
                    _current = ApplicationPreferences.Default;
                }
            }

            if (realtimeChanged)
            {
                if (!audioCacheChanged)
                {
                    _ = _session.ResetAudioCacheGenerations();
                }
                RealtimeAudioPreferencesChanged?.Invoke(this, EventArgs.Empty);
            }
            if (audioCacheChanged)
            {
                AudioCachePreferences cache = Current.AudioCache;
                _session.ConfigureAudioCache(cache.RootPath, cache.MaximumReusableBytes);
                AudioCachePreferencesChanged?.Invoke(this, EventArgs.Empty);
            }
            if (playbackChanged)
            {
                PlaybackPreferencesChanged?.Invoke(this, EventArgs.Empty);
            }
            if (appearanceChanged)
            {
                AppearancePreferencesChanged?.Invoke(this, EventArgs.Empty);
            }
            return saved.Succeeded
                ? new(ApplicationPreferenceUpdateStatus.Applied)
                : new(ApplicationPreferenceUpdateStatus.FailedUsingDefaults, saved.Notice);
        }
        finally
        {
            admission.Dispose();
        }
    }
}
