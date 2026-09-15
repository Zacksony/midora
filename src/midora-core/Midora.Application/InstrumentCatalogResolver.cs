namespace Midora.Application;

public enum InstrumentCatalogResolutionSource
{
    NumericFallback,
    GeneralMidi,
    BuiltIn,
    UserProfile,
    ImportedSoundFont,
    UserOverride
}

public sealed record ResolvedInstrumentCatalogName(
    string DisplayName,
    string? SourceDisplayName,
    InstrumentCatalogResolutionSource Source)
{
    public string DisplayText => SourceDisplayName is null
        ? DisplayName
        : $"{DisplayName} ({SourceDisplayName})";
}

public readonly record struct InstrumentCatalogSoundFontEntry(
    SoundFontEntryId EntryId,
    bool Enabled)
{
    public void Validate() => EntryId.Validate();
}

public sealed class InstrumentCatalogResolver
{
    private readonly bool _generalMidiEnabled;
    private readonly IReadOnlyList<InstrumentCatalogOverride> _overrides;
    private readonly InstrumentCatalogProfile[] _importedProfiles;
    private readonly InstrumentCatalogProfile[] _userProfiles;
    private readonly InstrumentCatalogProfile[] _builtInProfiles;

    public InstrumentCatalogResolver(
        InstrumentCatalogState state,
        IReadOnlyList<ApplicationSoundFontPreference> soundFonts)
        : this(state, CreateSoundFontEntries(soundFonts))
    {
    }

    public InstrumentCatalogResolver(
        InstrumentCatalogState state,
        IReadOnlyList<InstrumentCatalogSoundFontEntry> soundFonts)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(soundFonts);
        ArgumentNullException.ThrowIfNull(state.Profiles);
        ArgumentNullException.ThrowIfNull(state.Overrides);
        _generalMidiEnabled = state.GeneralMidiEnabled;
        _overrides = state.Overrides;
        _builtInProfiles = state.Profiles
            .Where(value => value.Enabled
                && value.SourceKind == InstrumentCatalogSourceKind.BuiltIn)
            .ToArray();
        _userProfiles = state.Profiles
            .Where(value => value.Enabled
                && value.SourceKind == InstrumentCatalogSourceKind.User)
            .ToArray();

        Dictionary<SoundFontEntryId, List<InstrumentCatalogProfile>> importedProfiles = state.Profiles
            .Where(value => value.Enabled
                && value.SourceKind == InstrumentCatalogSourceKind.ImportedSf2
                && value.SourceSoundFontEntryId.HasValue)
            .GroupBy(value => value.SourceSoundFontEntryId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        List<InstrumentCatalogProfile> orderedImportedProfiles = [];
        for (int index = 0; index < soundFonts.Count; index++)
        {
            InstrumentCatalogSoundFontEntry soundFont = soundFonts[index];
            soundFont.Validate();
            if (soundFont.Enabled
                && importedProfiles.TryGetValue(
                    soundFont.EntryId,
                    out List<InstrumentCatalogProfile>? profiles))
            {
                orderedImportedProfiles.AddRange(profiles);
            }
        }
        _importedProfiles = orderedImportedProfiles.ToArray();
    }

    private static InstrumentCatalogSoundFontEntry[] CreateSoundFontEntries(
        IReadOnlyList<ApplicationSoundFontPreference> soundFonts)
    {
        ArgumentNullException.ThrowIfNull(soundFonts);
        InstrumentCatalogSoundFontEntry[] result = new InstrumentCatalogSoundFontEntry[soundFonts.Count];
        for (int index = 0; index < soundFonts.Count; index++)
        {
            ApplicationSoundFontPreference soundFont = soundFonts[index]
                ?? throw new ArgumentException("The SoundFont list contains null.", nameof(soundFonts));
            soundFont.Validate();
            result[index] = new(soundFont.EntryId, soundFont.Enabled);
        }
        return result;
    }

    public ResolvedInstrumentCatalogName ResolveProgram(InstrumentAddress address)
    {
        address.Validate();
        if (FindOverride(address) is { ProgramDisplayName: { } overrideName })
        {
            return new(
                overrideName,
                "User Override",
                InstrumentCatalogResolutionSource.UserOverride);
        }
        if (ResolveProfileProgram(
                _importedProfiles,
                address,
                InstrumentCatalogResolutionSource.ImportedSoundFont,
                static profile => $"Imported: {profile.DisplayName}") is { } imported)
        {
            return imported;
        }
        if (ResolveProfileProgram(
                _userProfiles,
                address,
                InstrumentCatalogResolutionSource.UserProfile,
                static profile => profile.DisplayName) is { } user)
        {
            return user;
        }
        if (ResolveProfileProgram(
                _builtInProfiles,
                address,
                InstrumentCatalogResolutionSource.BuiltIn,
                static profile => profile.DisplayName) is { } builtIn)
        {
            return builtIn;
        }
        if (_generalMidiEnabled && address.BankMsb == 0 && address.BankLsb == 0)
        {
            return new(
                GeneralMidiInstrumentCatalog.Programs[address.Program],
                GeneralMidiInstrumentCatalog.SourceDisplayName,
                InstrumentCatalogResolutionSource.GeneralMidi);
        }
        return new(
            $"Bank MSB {address.BankMsb} / LSB {address.BankLsb} / Program {address.Program}",
            SourceDisplayName: null,
            InstrumentCatalogResolutionSource.NumericFallback);
    }

    /// <summary>
    /// Fixed 16K address bitmap: union known banks without enumerating the
    /// 2-million Bank/Program product or building a second catalog graph.
    /// Numeric fields remain able to select addresses absent from this list.
    /// </summary>
    public IReadOnlyList<InstrumentBankAddress> GetKnownBanks()
    {
        System.Collections.BitArray present = new(128 * 128);
        if (_generalMidiEnabled) present[0] = true;
        foreach (var value in _overrides)
            present[value.Address.BankMsb * 128 + value.Address.BankLsb] = true;
        foreach (var profiles in new[] { _importedProfiles, _userProfiles, _builtInProfiles })
            foreach (var profile in profiles)
                foreach (var bank in profile.Banks)
                    present[bank.BankMsb * 128 + bank.BankLsb] = true;
        List<InstrumentBankAddress> result = [];
        for (int index = 0; index < present.Length; index++)
            if (present[index]) result.Add(new((byte)(index / 128), (byte)(index % 128)));
        return result;
    }

    public ResolvedInstrumentCatalogName ResolveBank(InstrumentBankAddress address)
    {
        address.Validate();
        if (FindBankOverride(address) is { BankDisplayName: { } overrideName })
        {
            return new(
                overrideName,
                "User Override",
                InstrumentCatalogResolutionSource.UserOverride);
        }
        if (ResolveProfileBank(
                _importedProfiles,
                address,
                InstrumentCatalogResolutionSource.ImportedSoundFont,
                static profile => $"Imported: {profile.DisplayName}") is { } imported)
        {
            return imported;
        }
        if (ResolveProfileBank(
                _userProfiles,
                address,
                InstrumentCatalogResolutionSource.UserProfile,
                static profile => profile.DisplayName) is { } user)
        {
            return user;
        }
        if (ResolveProfileBank(
                _builtInProfiles,
                address,
                InstrumentCatalogResolutionSource.BuiltIn,
                static profile => profile.DisplayName) is { } builtIn)
        {
            return builtIn;
        }
        if (_generalMidiEnabled && address.BankMsb == 0 && address.BankLsb == 0)
        {
            return new(
                "General MIDI",
                GeneralMidiInstrumentCatalog.SourceDisplayName,
                InstrumentCatalogResolutionSource.GeneralMidi);
        }
        return new(
            $"Bank MSB {address.BankMsb} / LSB {address.BankLsb}",
            SourceDisplayName: null,
            InstrumentCatalogResolutionSource.NumericFallback);
    }

    private InstrumentCatalogOverride? FindOverride(InstrumentAddress address)
    {
        int low = 0;
        int high = _overrides.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            InstrumentCatalogOverride candidate = _overrides[middle];
            int comparison = Compare(candidate.Address, address);
            if (comparison == 0) return candidate;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    private InstrumentCatalogOverride? FindBankOverride(InstrumentBankAddress address)
    {
        int low = 0;
        int high = _overrides.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            InstrumentBankAddress candidate = _overrides[middle].Address.Bank;
            if (Compare(candidate, address) < 0) low = middle + 1;
            else high = middle;
        }
        for (int index = low; index < _overrides.Count; index++)
        {
            InstrumentCatalogOverride candidate = _overrides[index];
            if (candidate.Address.Bank != address) break;
            if (candidate.BankDisplayName is not null) return candidate;
        }
        return null;
    }

    private static ResolvedInstrumentCatalogName? ResolveProfileProgram(
        IReadOnlyList<InstrumentCatalogProfile> profiles,
        InstrumentAddress address,
        InstrumentCatalogResolutionSource source,
        Func<InstrumentCatalogProfile, string> sourceDisplayName)
    {
        foreach (InstrumentCatalogProfile profile in profiles)
        {
            InstrumentCatalogBank? bank = FindBank(profile.Banks, address.Bank);
            InstrumentCatalogProgram? program = bank is null
                ? null
                : FindProgram(bank.Programs, address.Program);
            if (program is not null)
            {
                return new(program.DisplayName, sourceDisplayName(profile), source);
            }
        }
        return null;
    }

    private static ResolvedInstrumentCatalogName? ResolveProfileBank(
        IReadOnlyList<InstrumentCatalogProfile> profiles,
        InstrumentBankAddress address,
        InstrumentCatalogResolutionSource source,
        Func<InstrumentCatalogProfile, string> sourceDisplayName)
    {
        foreach (InstrumentCatalogProfile profile in profiles)
        {
            InstrumentCatalogBank? bank = FindBank(profile.Banks, address);
            if (bank?.DisplayName is { } bankName)
            {
                return new(bankName, sourceDisplayName(profile), source);
            }
        }
        return null;
    }

    private static InstrumentCatalogBank? FindBank(
        IReadOnlyList<InstrumentCatalogBank> banks,
        InstrumentBankAddress address)
    {
        int low = 0;
        int high = banks.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            InstrumentCatalogBank candidate = banks[middle];
            int comparison = Compare(candidate.Address, address);
            if (comparison == 0) return candidate;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    private static InstrumentCatalogProgram? FindProgram(
        IReadOnlyList<InstrumentCatalogProgram> programs,
        byte program)
    {
        int low = 0;
        int high = programs.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            InstrumentCatalogProgram candidate = programs[middle];
            if (candidate.Program == program) return candidate;
            if (candidate.Program < program) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    private static int Compare(InstrumentAddress left, InstrumentAddress right)
    {
        int bank = Compare(left.Bank, right.Bank);
        return bank != 0 ? bank : left.Program.CompareTo(right.Program);
    }

    private static int Compare(InstrumentBankAddress left, InstrumentBankAddress right)
    {
        int msb = left.BankMsb.CompareTo(right.BankMsb);
        return msb != 0 ? msb : left.BankLsb.CompareTo(right.BankLsb);
    }
}
