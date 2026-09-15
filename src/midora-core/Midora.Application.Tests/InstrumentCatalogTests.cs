namespace Midora.Application.Tests;

public sealed class InstrumentCatalogTests
{
    [Fact]
    public void GeneralMidiCatalogContainsEveryProgramExactlyOnce()
    {
        Assert.Equal(128, GeneralMidiInstrumentCatalog.Programs.Count);
        Assert.Equal("Acoustic Grand Piano", GeneralMidiInstrumentCatalog.Programs[0]);
        Assert.Equal("Gunshot", GeneralMidiInstrumentCatalog.Programs[127]);
        Assert.Equal(
            128,
            GeneralMidiInstrumentCatalog.CreateReadOnlyProfile().Banks.Single().Programs.Count);
    }

    [Fact]
    public void ResolverUsesTheSpecifiedDeterministicPrecedence()
    {
        SoundFontEntryId highPrioritySoundFont = SoundFontEntryId.Create();
        SoundFontEntryId lowPrioritySoundFont = SoundFontEntryId.Create();
        InstrumentCatalogState state = new(
            GeneralMidiEnabled: true,
            Profiles:
            [
                Profile("First User", InstrumentCatalogSourceKind.User, null, "First user name"),
                Profile("Second User", InstrumentCatalogSourceKind.User, null, "Second user name"),
                Profile("High SF", InstrumentCatalogSourceKind.ImportedSf2, highPrioritySoundFont, "High SF name"),
                Profile("Low SF", InstrumentCatalogSourceKind.ImportedSf2, lowPrioritySoundFont, "Low SF name")
            ],
            Overrides: [new(0, 0, 1, null, "Override name")]);
        ApplicationSoundFontPreference[] soundFonts =
        [
            new(highPrioritySoundFont, Path.GetFullPath("high.sf2"), true),
            new(lowPrioritySoundFont, Path.GetFullPath("low.sf2"), true)
        ];

        InstrumentCatalogResolver resolver = new(state, soundFonts);

        Assert.Equal("Override name", resolver.ResolveProgram(new(0, 0, 1)).DisplayName);
        Assert.Equal(
            InstrumentCatalogResolutionSource.UserOverride,
            resolver.ResolveProgram(new(0, 0, 1)).Source);
        Assert.Equal("High SF name", resolver.ResolveProgram(new(0, 0, 2)).DisplayName);
        Assert.Equal("Imported: High SF", resolver.ResolveProgram(new(0, 0, 2)).SourceDisplayName);
        Assert.Equal("Acoustic Grand Piano", resolver.ResolveProgram(new(0, 0, 0)).DisplayName);
        Assert.Equal(
            "Bank MSB 1 / LSB 0 / Program 8",
            resolver.ResolveProgram(new(1, 0, 8)).DisplayName);
    }

    [Fact]
    public void DisabledAndOrphanedImportedProfilesDoNotAffectPlaybackContextNames()
    {
        SoundFontEntryId configured = SoundFontEntryId.Create();
        SoundFontEntryId orphan = SoundFontEntryId.Create();
        InstrumentCatalogState state = new(
            GeneralMidiEnabled: false,
            Profiles:
            [
                Profile("Disabled", InstrumentCatalogSourceKind.ImportedSf2, configured, "Disabled name") with
                {
                    Enabled = false
                },
                Profile("Orphan", InstrumentCatalogSourceKind.ImportedSf2, orphan, "Orphan name"),
                Profile("User", InstrumentCatalogSourceKind.User, null, "User name")
            ],
            Overrides: []);
        ApplicationSoundFontPreference[] soundFonts =
        [new(configured, Path.GetFullPath("configured.sf2"), true)];

        InstrumentCatalogResolver resolver = new(state, soundFonts);

        Assert.Equal("User name", resolver.ResolveProgram(new(0, 0, 2)).DisplayName);
        Assert.Equal(
            InstrumentCatalogResolutionSource.UserProfile,
            resolver.ResolveProgram(new(0, 0, 2)).Source);
    }

    [Fact]
    public void ResolverAcceptsDraftSoundFontOrderWithoutReadingAudioConfigurationFields()
    {
        SoundFontEntryId first = SoundFontEntryId.Create();
        SoundFontEntryId second = SoundFontEntryId.Create();
        InstrumentCatalogState state = new(
            GeneralMidiEnabled: false,
            Profiles:
            [
                Profile("First", InstrumentCatalogSourceKind.ImportedSf2, first, "First name"),
                Profile("Second", InstrumentCatalogSourceKind.ImportedSf2, second, "Second name")
            ],
            Overrides: []);

        InstrumentCatalogResolver firstResolver = new(
            state,
            [
                new InstrumentCatalogSoundFontEntry(first, Enabled: true),
                new InstrumentCatalogSoundFontEntry(second, Enabled: true)
            ]);
        InstrumentCatalogResolver secondResolver = new(
            state,
            [
                new InstrumentCatalogSoundFontEntry(first, Enabled: false),
                new InstrumentCatalogSoundFontEntry(second, Enabled: true)
            ]);

        Assert.Equal("First name", firstResolver.ResolveProgram(new(0, 0, 2)).DisplayName);
        Assert.Equal("Second name", secondResolver.ResolveProgram(new(0, 0, 2)).DisplayName);
    }

    [Fact]
    public void ResolverIndexesOnlyProfilesAndReadsProgramTablesOnDemand()
    {
        CountingReadOnlyList<InstrumentCatalogProgram> programs = new(
            Enumerable.Range(0, 128)
                .Select(value => new InstrumentCatalogProgram((byte)value, $"Program {value}"))
                .ToArray());
        InstrumentCatalogState state = new(
            GeneralMidiEnabled: false,
            Profiles:
            [
                new InstrumentCatalogProfile(
                    InstrumentCatalogProfileId.Create(),
                    "Large Profile",
                    true,
                    InstrumentCatalogSourceKind.User,
                    null,
                    [new InstrumentCatalogBank(0, 0, "Bank", programs)])
            ],
            Overrides: []);

        InstrumentCatalogResolver resolver = new(
            state,
            Array.Empty<InstrumentCatalogSoundFontEntry>());

        Assert.Equal(0, programs.ReadCount);
        Assert.Equal("Program 96", resolver.ResolveProgram(new(0, 0, 96)).DisplayName);
        Assert.InRange(programs.ReadCount, 1, 8);
    }

    [Fact]
    public void InvalidIdentityAndAddressCollisionsAreRejected()
    {
        InstrumentCatalogProfile duplicateBankProfile = new(
            InstrumentCatalogProfileId.Create(),
            "Profile",
            true,
            InstrumentCatalogSourceKind.User,
            null,
            [
                new(0, 0, null, [new(0, "One")]),
                new(0, 0, null, [new(1, "Two")])
            ]);
        InstrumentCatalogProfileId duplicateId = InstrumentCatalogProfileId.Create();
        InstrumentCatalogState duplicateProfiles = new(
            true,
            [
                Profile("One", InstrumentCatalogSourceKind.User, null, "One") with { ProfileId = duplicateId },
                Profile("Two", InstrumentCatalogSourceKind.User, null, "Two") with { ProfileId = duplicateId }
            ],
            []);
        InstrumentCatalogState conflictingBankOverrideNames = new(
            true,
            [],
            [
                new(1, 2, 3, "Bank A", null),
                new(1, 2, 4, "Bank B", null)
            ]);

        Assert.Throws<ArgumentException>(duplicateBankProfile.Validate);
        Assert.Throws<ArgumentException>(duplicateProfiles.Validate);
        Assert.Throws<ArgumentException>(conflictingBankOverrideNames.Validate);
    }

    private static InstrumentCatalogProfile Profile(
        string name,
        InstrumentCatalogSourceKind sourceKind,
        SoundFontEntryId? soundFontEntryId,
        string programName) => new(
            InstrumentCatalogProfileId.Create(),
            name,
            true,
            sourceKind,
            soundFontEntryId,
            [new InstrumentCatalogBank(0, 0, "Bank", [new(2, programName)])]);

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        public int ReadCount { get; private set; }
        public int Count => values.Count;

        public T this[int index]
        {
            get
            {
                ReadCount++;
                return values[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
