using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ApplicationPreferencesStoreTests
{
    [Fact]
    public void InstrumentAuditionRoundTripsIndependentlyAndOldPreferencesUseDefaults()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        var store = new ApplicationPreferencesStore(path);
        var expected = ApplicationPreferences.Default with { InstrumentAudition = new(false, 127, 1, 900) };
        Assert.True(store.Save(expected).Succeeded);
        var loaded = store.Load();
        Assert.Null(loaded.Notice); Assert.Equal(expected.InstrumentAudition, loaded.Preferences.InstrumentAudition);
        Assert.Equal(expected.RealtimeAudio, loaded.Preferences.RealtimeAudio);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(json.Remove("instrumentAudition"));
        File.WriteAllText(path, json.ToJsonString());
        loaded = store.Load(); Assert.Null(loaded.Notice);
        Assert.Equal(new InstrumentAuditionPreferences(true, 60, 100, 500), loaded.Preferences.InstrumentAudition);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Save(expected with { InstrumentAudition = new(true, 128, 100, 500) }));
    }

    [Fact]
    public void MissingFileUsesSpecifiedDefaultsWithoutNotice()
    {
        using TemporaryDirectory directory = new();
        ApplicationPreferencesLoadResult result = new ApplicationPreferencesStore(
            Path.Combine(directory.Path, "preferences.json")).Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Null(result.Notice);
        Assert.Null(result.Preferences.RealtimeAudio.PlaybackOutputDeviceId);
        Assert.Equal(100, result.Preferences.RealtimeAudio.RenderAheadMilliseconds);
        Assert.Equal(50, result.Preferences.RealtimeAudio.DeviceBufferRequestMilliseconds);
        Assert.Equal(500, result.Preferences.RealtimeAudio.MaximumSampleVoicesPerUnitStream);
        Assert.Equal(
            AudioCachePreferences.DefaultMaximumReusableBytes,
            result.Preferences.AudioCache.MaximumReusableBytes);
        Assert.Equal(PlaybackPreferences.Default, result.Preferences.Playback);
        Assert.Equal(AppearancePreferences.Default, result.Preferences.Appearance);
        Assert.Empty(result.Preferences.SoundFonts);
        Assert.False(result.Preferences.DesktopUi.FollowPlayback);
    }

    [Fact]
    public void FollowPlaybackDefaultsOffButKeepsAnExplicitEnabledPreference()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        ApplicationPreferences explicitlyEnabled = ApplicationPreferences.Default with
        {
            DesktopUi = ApplicationPreferences.Default.DesktopUi with
            {
                FollowPlayback = true
            }
        };

        Assert.True(store.Save(explicitlyEnabled).Succeeded);
        Assert.True(store.Load().Preferences.DesktopUi.FollowPlayback);

        JsonObject root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        Assert.True(root["desktopUi"]!.AsObject().Remove("followPlayback"));
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.False(store.Load().Preferences.DesktopUi.FollowPlayback);
    }

    [Fact]
    public void RoundTripIsDeterministicAndKeepsPickerPurposesSeparate()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        ApplicationPreferences preferences = new(
            new RealtimeAudioPreferences("endpoint-id", 2_000, 200, 16_777_216),
            new AudioCachePreferences(Path.Combine(directory.Path, "cache"), 0),
            new ApplicationRecentDirectories(
                Path.Combine(directory.Path, "open"),
                Path.Combine(directory.Path, "save"),
                Path.Combine(directory.Path, "sf2"),
                Path.Combine(directory.Path, "midi"),
                Path.Combine(directory.Path, "audio")))
        {
            Playback = new PlaybackPreferences(
                -6.5,
                false,
                StopCursorBehavior.StayAtStoppedTick),
            Appearance = new AppearancePreferences("English"),
            SoundFonts =
            [
                new(
                    Path.Combine(directory.Path, "first.sf2"),
                    true,
                    new(1, 2, 3)),
                new(Path.Combine(directory.Path, "second.sf2"), false),
                new(
                    Path.Combine(directory.Path, "third.sfz"),
                    true,
                    new(4, 5, 6))
            ]
        };

        Assert.True(store.Save(preferences).Succeeded);
        byte[] first = File.ReadAllBytes(path);
        string persistedJson = Encoding.UTF8.GetString(first);
        Assert.DoesNotContain("audioCacheRootPath", persistedJson, StringComparison.Ordinal);
        Assert.DoesNotContain(preferences.AudioCache.RootPath, persistedJson, StringComparison.Ordinal);
        Assert.True(store.Save(preferences).Succeeded);
        byte[] second = File.ReadAllBytes(path);
        ApplicationPreferencesLoadResult loaded = store.Load();

        Assert.Equal(first, second);
        Assert.Null(loaded.Notice);
        Assert.Equal(preferences.RealtimeAudio, loaded.Preferences.RealtimeAudio);
        Assert.Equal(
            new AudioCachePreferences(
                Midora.Common.MidoraProgramData.Current.AudioCacheDirectory,
                preferences.AudioCache.MaximumReusableBytes),
            loaded.Preferences.AudioCache);
        Assert.Equal(preferences.Playback, loaded.Preferences.Playback);
        Assert.Equal(preferences.Appearance, loaded.Preferences.Appearance);
        Assert.Equal(preferences.RecentDirectories, loaded.Preferences.RecentDirectories);
        Assert.Equal(preferences.SoundFonts, loaded.Preferences.SoundFonts);
        Assert.Equal(
            [
                Path.Combine(directory.Path, "first.sf2"),
                Path.Combine(directory.Path, "third.sfz")
            ],
            loaded.Preferences.GetEnabledSoundFontPaths());
        Assert.Equal(
            [new Midora.Audio.SoundFontTarget(1, 2, 3), new(4, 5, 6)],
            loaded.Preferences.GetEnabledSoundFontConfigurations()
                .Select(value => value.Target));
        Assert.NotEqual(
            loaded.Preferences.RecentDirectories.MidiExport,
            loaded.Preferences.RecentDirectories.AudioRender);
    }

    [Fact]
    public void LegacyInspectorDesktopFieldsAreReadButNotWrittenAgain()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        Assert.True(store.Save(ApplicationPreferences.Default).Succeeded);
        string current = File.ReadAllText(path, Encoding.UTF8);
        const string anchor = "    \"bottomPanelHeight\"";
        string legacy = current.Replace(
            anchor,
            "    \"inspectorWidth\": 312,\n"
            + "    \"inspectorVisible\": false,\n"
            + anchor,
            StringComparison.Ordinal);
        Assert.NotEqual(current, legacy);
        File.WriteAllText(path, legacy, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ApplicationPreferencesLoadResult loaded = store.Load();

        Assert.Null(loaded.Notice);
        Assert.Equal(ApplicationPreferences.Default, loaded.Preferences);
        Assert.True(store.Save(loaded.Preferences).Succeeded);
        string rewritten = File.ReadAllText(path, Encoding.UTF8);
        Assert.DoesNotContain("inspectorWidth", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("inspectorVisible", rewritten, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(20, 5, 1)]
    [InlineData(2_000, 200, 16_777_216)]
    public void RealtimeAudioBoundaryValuesAreAccepted(
        int renderAhead,
        int deviceRequest,
        int sampleVoices)
    {
        RealtimeAudioPreferences preferences = new(
            null,
            renderAhead,
            deviceRequest,
            sampleVoices);

        preferences.Validate();
    }

    [Theory]
    [InlineData(19, 50, 500)]
    [InlineData(2_001, 50, 500)]
    [InlineData(100, 4, 500)]
    [InlineData(100, 201, 500)]
    [InlineData(100, 50, 0)]
    [InlineData(100, 50, 16_777_217)]
    public void RealtimeAudioOutOfRangeValuesAreRejectedWithoutClamp(
        int renderAhead,
        int deviceRequest,
        int sampleVoices)
    {
        RealtimeAudioPreferences preferences = new(
            null,
            renderAhead,
            deviceRequest,
            sampleVoices);

        Assert.Throws<ArgumentOutOfRangeException>(preferences.Validate);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":1,\"unknown\":true}")]
    [InlineData("{\"schemaVersion\":1,\"playbackOutputDeviceId\":null,\"renderAheadMilliseconds\":19,\"deviceBufferRequestMilliseconds\":50,\"realtimeMaximumSampleVoicesPerUnitStream\":500,\"audioCacheRootPath\":\"C:\\\\cache\",\"maximumReusableAudioCacheBytes\":17179869184,\"recentDirectories\":{}}")]
    [InlineData("not-json")]
    public void UnsupportedCorruptOrInvalidFileUsesDefaultsAndNotice(string json)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ApplicationPreferencesLoadResult result = new ApplicationPreferencesStore(path).Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Equal("PreferenceReadFailed", result.Notice?.Code);
        Assert.NotNull(result.Notice?.Error);
    }

    [Fact]
    public void OversizedFileUsesDefaultsInsteadOfAllocatingUnboundedInput()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        using (FileStream stream = File.Create(path))
        {
            stream.SetLength((1024 * 1024) + 1);
        }

        ApplicationPreferencesLoadResult result = new ApplicationPreferencesStore(path).Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Equal("PreferenceReadFailed", result.Notice?.Code);
    }

    [Fact]
    public void FailedReplacementPreservesPreviouslyPublishedPreferences()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        ApplicationPreferences original = new(
            new RealtimeAudioPreferences("original", 100, 50, 500),
            new AudioCachePreferences(Path.Combine(directory.Path, "cache"), 1024),
            ApplicationRecentDirectories.Empty);
        ApplicationPreferences replacement = original with
        {
            RealtimeAudio = original.RealtimeAudio with { PlaybackOutputDeviceId = "new" }
        };
        Assert.True(store.Save(original).Succeeded);

        ApplicationPreferencesSaveResult failed;
        using (FileStream locked = new(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            failed = store.Save(replacement);
        }

        Assert.False(failed.Succeeded);
        Assert.Equal("PreferenceWriteFailed", failed.Notice?.Code);
        Assert.Equal(
            original with
            {
                AudioCache = original.AudioCache with
                {
                    RootPath = Midora.Common.MidoraProgramData.Current.AudioCacheDirectory
                }
            },
            store.Load().Preferences);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public void RelativeRecentDirectoryIsRejectedWithoutSilentRewrite()
    {
        using TemporaryDirectory directory = new();
        ApplicationPreferences invalid = new(
            RealtimeAudioPreferences.Default,
            new AudioCachePreferences(Path.Combine(directory.Path, "cache"), 1024),
            ApplicationRecentDirectories.Empty with { MidiExport = "relative" });
        ApplicationPreferencesStore store = new(
            Path.Combine(directory.Path, "preferences.json"));

        Assert.Throws<ArgumentException>(() => store.Save(invalid));
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void ApplicationSoundFontListRejectsRelativeAndUncPaths()
    {
        ApplicationPreferences relative = ApplicationPreferences.Default with
        {
            SoundFonts = [new("relative.sf2", true)]
        };
        ApplicationPreferences unc = ApplicationPreferences.Default with
        {
            SoundFonts = [new(@"\\server\share\default.sf2", true)]
        };

        Assert.Throws<ArgumentException>(relative.Validate);
        Assert.Throws<ArgumentException>(unc.Validate);
    }

    [Fact]
    public void SfzRequiresCompleteTargetWhileSf2MayUseOriginalMapping()
    {
        ApplicationPreferences sf2 = ApplicationPreferences.Default with
        {
            SoundFonts = [new(Path.GetFullPath("original.sf2"), true)]
        };
        ApplicationPreferences sfzWithoutTarget = ApplicationPreferences.Default with
        {
            SoundFonts = [new(Path.GetFullPath("instrument.sfz"), true)]
        };
        ApplicationPreferences sfzWithTarget = ApplicationPreferences.Default with
        {
            SoundFonts = [new(Path.GetFullPath("instrument.sfz"), true, new(0, 0, 0))]
        };

        sf2.Validate();
        Assert.Throws<ArgumentException>(sfzWithoutTarget.Validate);
        sfzWithTarget.Validate();
    }

    [Fact]
    public void Schema2SoundFontsReceiveStableIdsAndNextSaveWritesSchema3()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        ApplicationPreferences original = ApplicationPreferences.Default with
        {
            SoundFonts = [new(Path.Combine(directory.Path, "legacy.sf2"), true)]
        };
        Assert.True(store.Save(original).Succeeded);

        JsonObject root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        root["schemaVersion"] = 2;
        JsonObject legacySoundFont = root["soundFonts"]!.AsArray()[0]!.AsObject();
        Assert.True(legacySoundFont.Remove("entryId"));
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        ApplicationPreferencesLoadResult first = store.Load();
        ApplicationPreferencesLoadResult second = store.Load();

        Assert.Null(first.Notice);
        Assert.Null(second.Notice);
        SoundFontEntryId migratedId = Assert.Single(first.Preferences.SoundFonts).EntryId;
        Assert.NotEqual(default, migratedId);
        Assert.Equal(migratedId, Assert.Single(second.Preferences.SoundFonts).EntryId);
        Assert.True(store.Save(first.Preferences).Succeeded);
        JsonObject rewritten = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        Assert.Equal(3, rewritten["schemaVersion"]!.GetValue<int>());
        Assert.Equal(
            migratedId.ToString(),
            rewritten["soundFonts"]!.AsArray()[0]!["entryId"]!.GetValue<string>());
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejectedInsteadOfLastValueWinning()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "preferences.json");
        ApplicationPreferencesStore store = new(path);
        Assert.True(store.Save(ApplicationPreferences.Default).Succeeded);
        string valid = File.ReadAllText(path, Encoding.UTF8);
        string duplicate = valid.Replace(
            "\"schemaVersion\": 3",
            "\"schemaVersion\": 3,\n  \"schemaVersion\": 3",
            StringComparison.Ordinal);
        Assert.NotEqual(valid, duplicate);
        File.WriteAllText(path, duplicate, new UTF8Encoding(false));

        ApplicationPreferencesLoadResult result = store.Load();

        Assert.Equal(ApplicationPreferences.Default, result.Preferences);
        Assert.Equal("PreferenceReadFailed", result.Notice?.Code);
        Assert.IsType<InvalidDataException>(result.Notice?.Error);
    }

    [Fact]
    public void DuplicateSoundFontEntryIdsAreRejected()
    {
        SoundFontEntryId id = SoundFontEntryId.Create();
        ApplicationPreferences invalid = ApplicationPreferences.Default with
        {
            SoundFonts =
            [
                new(id, Path.GetFullPath("first.sf2"), true),
                new(id, Path.GetFullPath("second.sf2"), true)
            ]
        };

        Assert.Throws<ArgumentException>(invalid.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public void AudioCacheQuotaBoundaryValuesAreAccepted(long maximumReusableBytes)
    {
        using TemporaryDirectory directory = new();
        new AudioCachePreferences(directory.Path, maximumReusableBytes).Validate();
    }

    [Fact]
    public void AudioCacheRejectsRelativeUncAndNegativeQuota()
    {
        Assert.Throws<ArgumentException>(() => new AudioCachePreferences("relative", 0).Validate());
        Assert.Throws<ArgumentException>(() =>
            new AudioCachePreferences(@"\\server\share\Midora", 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AudioCachePreferences(@"C:\Midora\AudioCache", -1).Validate());
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-preference-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
