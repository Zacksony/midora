using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Midora.Application;

namespace Midora.Desktop;

public partial class InstrumentCatalogDialog : Window
{
    private readonly InstrumentCatalogDialogDraft _draft;
    private readonly ApplicationSoundFontPreference[] _soundFonts;
    private readonly Func<InstrumentCatalogState, string?>? _submit;
    private bool _initializingCatalog = true;
    private bool _suppressCatalogNavigation;
    private bool _addingBank;
    private bool _addingProgram;
    private bool _addingOverride;
    private InstrumentCatalogProfileDraft? _activeProfile;
    private InstrumentCatalogBankDraft? _activeBank;
    private InstrumentCatalogProgramDraft? _activeProgram;
    private InstrumentCatalogOverrideDraft? _activeOverride;
    private TabItem? _activeCatalogTab;
    private CancellationTokenSource? _scanCancellation;
    private Sf2PresetScanResult? _scanResult;
    private ApplicationSoundFontPreference? _scannedSoundFont;
    private InstrumentCatalogProfileId? _rescanProfileId;
    private readonly ObservableCollection<Sf2BankProjectionDraft> _scanBanks = [];
    private InstrumentCatalogImportCandidate? _importCandidate;
    private InstrumentCatalogMergePreview? _importMergePreview;
    private readonly ObservableCollection<InstrumentCatalogImportTarget> _importTargets = [];
    private InstrumentCatalogResolver? _effectiveResolver;

    public InstrumentCatalogDialog(
        InstrumentCatalogState initial,
        IReadOnlyList<ApplicationSoundFontPreference> soundFonts)
        : this(initial, soundFonts, submit: null)
    {
    }

    internal InstrumentCatalogDialog(
        InstrumentCatalogState initial,
        IReadOnlyList<ApplicationSoundFontPreference> soundFonts,
        Func<InstrumentCatalogState, string?>? submit)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(soundFonts);
        _soundFonts = soundFonts.Select(value =>
        {
            ArgumentNullException.ThrowIfNull(value);
            return value.Normalize();
        }).ToArray();
        _submit = submit;
        _draft = new(initial, _soundFonts);
        InitializeComponent();
        _activeCatalogTab = CatalogTabs.SelectedItem as TabItem;
        _initializingCatalog = false;
        GeneralMidiEnabledBox.IsChecked = _draft.GeneralMidiEnabled;
        ProfileList.ItemsSource = _draft.Profiles;
        OverrideList.ItemsSource = _draft.Overrides;
        ScanBankList.ItemsSource = _scanBanks;
        ImportTargetBox.ItemsSource = _importTargets;
        if (_draft.Profiles.Count != 0)
        {
            ProfileList.SelectedIndex = 0;
        }
        if (_draft.Overrides.Count != 0)
        {
            OverrideList.SelectedIndex = 0;
        }
        else
        {
            BeginNewOverride();
        }
        RefreshSelectedProgramResolution();
    }

    public InstrumentCatalogState? Result { get; private set; }

    internal InstrumentCatalogDialogDraft Draft => _draft;

    private InstrumentCatalogProfileDraft? SelectedProfile =>
        ProfileList.SelectedItem as InstrumentCatalogProfileDraft;

    private InstrumentCatalogBankDraft? SelectedBank =>
        BankList.SelectedItem as InstrumentCatalogBankDraft;

    private InstrumentCatalogProgramDraft? SelectedProgram =>
        ProgramList.SelectedItem as InstrumentCatalogProgramDraft;

    private InstrumentCatalogOverrideDraft? SelectedOverride =>
        OverrideList.SelectedItem as InstrumentCatalogOverrideDraft;

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
    {
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "creating another Profile"))
        {
            return;
        }
        HideValidation();
        InstrumentCatalogProfileDraft profile = InstrumentCatalogProfileDraft.CreateNew(
            GetUniqueProfileName("New Profile"));
        _draft.Profiles.Add(profile);
        SelectProfileForEditing(profile);
        ProfileList.ScrollIntoView(profile);
        InvalidateCatalogResolution();
    }

    private void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to rename.");
            return;
        }
        TextInputDialog dialog = new(
            "Rename Catalog Profile",
            "Profile name",
            profile.DisplayName);
        if (IsVisible)
        {
            dialog.Owner = this;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            profile.DisplayName = NormalizeRequiredName(dialog.Value, "Profile name");
            InvalidateCatalogResolution();
            HideValidation();
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
        }
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to delete.");
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "deleting this Profile"))
        {
            return;
        }
        int index = _draft.Profiles.IndexOf(profile);
        InstrumentCatalogProfileDraft? next;
        _suppressCatalogNavigation = true;
        try
        {
            _draft.Profiles.RemoveAt(index);
            ProfileList.SelectedIndex = _draft.Profiles.Count == 0
                ? -1
                : Math.Min(index, _draft.Profiles.Count - 1);
            next = SelectedProfile;
        }
        finally
        {
            _suppressCatalogNavigation = false;
        }
        LoadProfileEditor(next);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnMoveProfileUpClick(object sender, RoutedEventArgs e) => MoveSelectedProfile(-1);

    private void OnMoveProfileDownClick(object sender, RoutedEventArgs e) => MoveSelectedProfile(1);

    private void MoveSelectedProfile(int delta)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to move.");
            return;
        }
        int source = _draft.Profiles.IndexOf(profile);
        int target = source + delta;
        if (target < 0 || target >= _draft.Profiles.Count)
        {
            return;
        }
        _draft.Profiles.Move(source, target);
        ProfileList.SelectedItem = profile;
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnCatalogResolutionInputsChanged(object sender, RoutedEventArgs e) =>
        InvalidateCatalogResolution();

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatalogNavigation)
        {
            return;
        }
        InstrumentCatalogProfileDraft? requested = SelectedProfile;
        if (ReferenceEquals(requested, _activeProfile))
        {
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "selecting another Profile"))
        {
            RestoreSelection(ProfileList, _activeProfile);
            return;
        }
        LoadProfileEditor(requested);
    }

    private void LoadProfileEditor(InstrumentCatalogProfileDraft? profile)
    {
        _activeProfile = profile;
        _suppressCatalogNavigation = true;
        try
        {
            BankList.ItemsSource = profile?.Banks;
            BankList.SelectedIndex = profile?.Banks.Count > 0 ? 0 : -1;
        }
        finally
        {
            _suppressCatalogNavigation = false;
        }
        LoadBankEditor(SelectedBank);
        RefreshSelectedProgramResolution();
    }

    private void OnNewBankClick(object sender, RoutedEventArgs e)
    {
        if (TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "starting a new Bank"))
        {
            BeginNewBank();
            HideValidation();
        }
    }

    private void BeginNewBank()
    {
        _activeBank = null;
        _addingBank = true;
        RestoreSelection(BankList, null);
        BankMsbBox.Text = "0";
        BankLsbBox.Text = "0";
        BankNameBox.Text = string.Empty;
        ApplyBankButton.Content = "Add Bank";
        bool wasSuppressed = _suppressCatalogNavigation;
        _suppressCatalogNavigation = true;
        try
        {
            ProgramList.ItemsSource = null;
        }
        finally
        {
            _suppressCatalogNavigation = wasSuppressed;
        }
        BeginNewProgram();
    }

    private void OnBankSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatalogNavigation)
        {
            return;
        }
        InstrumentCatalogBankDraft? requested = SelectedBank;
        if (ReferenceEquals(requested, _activeBank) && (!_addingBank || requested is null))
        {
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "selecting another Bank"))
        {
            RestoreSelection(BankList, _activeBank);
            return;
        }
        LoadBankEditor(requested);
    }

    private void LoadBankEditor(InstrumentCatalogBankDraft? bank)
    {
        if (bank is null)
        {
            BeginNewBank();
            return;
        }
        _activeBank = bank;
        _addingBank = false;
        BankMsbBox.Text = bank.BankMsb.ToString(CultureInfo.InvariantCulture);
        BankLsbBox.Text = bank.BankLsb.ToString(CultureInfo.InvariantCulture);
        BankNameBox.Text = bank.DisplayName ?? string.Empty;
        ApplyBankButton.Content = "Apply Bank";
        _suppressCatalogNavigation = true;
        try
        {
            ProgramList.ItemsSource = bank.Programs;
            ProgramList.SelectedIndex = bank.Programs.Count > 0 ? 0 : -1;
        }
        finally
        {
            _suppressCatalogNavigation = false;
        }
        if (SelectedProgram is { } program)
        {
            LoadProgramEditor(program);
        }
        else
        {
            BeginNewProgram();
        }
        RefreshSelectedProgramResolution();
    }

    private void OnApplyBankClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select or create a Profile before adding a Bank.");
            return;
        }
        if (!TryMidiByte(BankMsbBox.Text, "Bank MSB", out byte msb)
            || !TryMidiByte(BankLsbBox.Text, "Bank LSB", out byte lsb))
        {
            return;
        }
        string? name = NormalizeOptionalName(BankNameBox.Text);
        bool wasAdding = _addingBank;
        InstrumentCatalogBankDraft? edited = wasAdding ? null : _activeBank;
        if (profile.Banks.Any(value => !ReferenceEquals(value, edited)
            && value.BankMsb == msb
            && value.BankLsb == lsb))
        {
            ShowValidation($"Bank MSB {msb} / LSB {lsb} already exists in this Profile.");
            return;
        }
        if (edited is null)
        {
            edited = new(msb, lsb, name, []);
            profile.Banks.Add(edited);
        }
        else
        {
            edited.Update(msb, lsb, name);
        }
        Reposition(
            profile.Banks,
            edited,
            static (left, right) =>
            {
                int msb = left.BankMsb.CompareTo(right.BankMsb);
                return msb != 0 ? msb : left.BankLsb.CompareTo(right.BankLsb);
            });
        _activeBank = edited;
        _addingBank = false;
        RestoreSelection(BankList, edited);
        BankMsbBox.Text = edited.BankMsb.ToString(CultureInfo.InvariantCulture);
        BankLsbBox.Text = edited.BankLsb.ToString(CultureInfo.InvariantCulture);
        BankNameBox.Text = edited.DisplayName ?? string.Empty;
        ApplyBankButton.Content = "Apply Bank";
        if (wasAdding)
        {
            ProgramList.ItemsSource = edited.Programs;
        }
        BankList.ScrollIntoView(edited);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnDeleteBankClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile || SelectedBank is not { } bank)
        {
            ShowValidation("Select a Bank to delete.");
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "deleting this Bank"))
        {
            return;
        }
        int index = profile.Banks.IndexOf(bank);
        InstrumentCatalogBankDraft? next;
        _suppressCatalogNavigation = true;
        try
        {
            profile.Banks.RemoveAt(index);
            BankList.SelectedIndex = profile.Banks.Count == 0
                ? -1
                : Math.Min(index, profile.Banks.Count - 1);
            next = SelectedBank;
        }
        finally
        {
            _suppressCatalogNavigation = false;
        }
        LoadBankEditor(next);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnNewProgramClick(object sender, RoutedEventArgs e)
    {
        if (TryLeavePendingCatalogEditors(PendingCatalogEditor.Program, "starting a new Program"))
        {
            BeginNewProgram();
            HideValidation();
        }
    }

    private void BeginNewProgram()
    {
        _activeProgram = null;
        _addingProgram = true;
        RestoreSelection(ProgramList, null);
        ProgramNumberBox.Text = "0";
        ProgramNameBox.Text = string.Empty;
        ApplyProgramButton.Content = "Add Program";
        RefreshSelectedProgramResolution();
    }

    private void OnProgramSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatalogNavigation)
        {
            return;
        }
        InstrumentCatalogProgramDraft? requested = SelectedProgram;
        if (ReferenceEquals(requested, _activeProgram) && (!_addingProgram || requested is null))
        {
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Program, "selecting another Program"))
        {
            RestoreSelection(ProgramList, _activeProgram);
            return;
        }
        LoadProgramEditor(requested);
    }

    private void LoadProgramEditor(InstrumentCatalogProgramDraft? program)
    {
        if (program is null)
        {
            BeginNewProgram();
            return;
        }
        _activeProgram = program;
        _addingProgram = false;
        ProgramNumberBox.Text = program.Program.ToString(CultureInfo.InvariantCulture);
        ProgramNameBox.Text = program.DisplayName;
        ApplyProgramButton.Content = "Apply Program";
        RefreshSelectedProgramResolution();
    }

    private void OnApplyProgramClick(object sender, RoutedEventArgs e)
    {
        if (SelectedBank is not { } bank)
        {
            ShowValidation("Select or add a Bank before adding a Program.");
            return;
        }
        if (!TryMidiByte(ProgramNumberBox.Text, "Program", out byte programNumber))
        {
            return;
        }
        string name;
        try
        {
            name = NormalizeRequiredName(ProgramNameBox.Text, "Program display name");
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
            return;
        }
        InstrumentCatalogProgramDraft? edited = _addingProgram ? null : _activeProgram;
        if (bank.Programs.Any(value => !ReferenceEquals(value, edited)
            && value.Program == programNumber))
        {
            ShowValidation($"Program {programNumber} already exists in this Bank.");
            return;
        }
        if (edited is null)
        {
            edited = new(programNumber, name);
            bank.Programs.Add(edited);
        }
        else
        {
            edited.Update(programNumber, name);
        }
        Reposition(
            bank.Programs,
            edited,
            static (left, right) => left.Program.CompareTo(right.Program));
        _activeProgram = edited;
        _addingProgram = false;
        RestoreSelection(ProgramList, edited);
        ProgramNumberBox.Text = edited.Program.ToString(CultureInfo.InvariantCulture);
        ProgramNameBox.Text = edited.DisplayName;
        ApplyProgramButton.Content = "Apply Program";
        ProgramList.ScrollIntoView(edited);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnDeleteProgramClick(object sender, RoutedEventArgs e)
    {
        if (SelectedBank is not { } bank || SelectedProgram is not { } program)
        {
            ShowValidation("Select a Program to delete.");
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Program, "deleting this Program"))
        {
            return;
        }
        int index = bank.Programs.IndexOf(program);
        InstrumentCatalogProgramDraft? next;
        _suppressCatalogNavigation = true;
        try
        {
            bank.Programs.RemoveAt(index);
            ProgramList.SelectedIndex = bank.Programs.Count == 0
                ? -1
                : Math.Min(index, bank.Programs.Count - 1);
            next = SelectedProgram;
        }
        finally
        {
            _suppressCatalogNavigation = false;
        }
        LoadProgramEditor(next);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnNewOverrideClick(object sender, RoutedEventArgs e)
    {
        if (TryLeavePendingCatalogEditors(PendingCatalogEditor.Override, "starting a new User Override"))
        {
            BeginNewOverride();
            HideValidation();
        }
    }

    private void BeginNewOverride()
    {
        _activeOverride = null;
        _addingOverride = true;
        RestoreSelection(OverrideList, null);
        OverrideMsbBox.Text = "0";
        OverrideLsbBox.Text = "0";
        OverrideProgramBox.Text = "0";
        OverrideBankNameBox.Text = string.Empty;
        OverrideProgramNameBox.Text = string.Empty;
    }

    private void OnOverrideSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCatalogNavigation)
        {
            return;
        }
        InstrumentCatalogOverrideDraft? requested = SelectedOverride;
        if (ReferenceEquals(requested, _activeOverride) && (!_addingOverride || requested is null))
        {
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Override,
                "selecting another User Override"))
        {
            RestoreSelection(OverrideList, _activeOverride);
            return;
        }
        LoadOverrideEditor(requested);
    }

    private void LoadOverrideEditor(InstrumentCatalogOverrideDraft? catalogOverride)
    {
        if (catalogOverride is null)
        {
            BeginNewOverride();
            return;
        }
        _activeOverride = catalogOverride;
        _addingOverride = false;
        OverrideMsbBox.Text = catalogOverride.BankMsb.ToString(CultureInfo.InvariantCulture);
        OverrideLsbBox.Text = catalogOverride.BankLsb.ToString(CultureInfo.InvariantCulture);
        OverrideProgramBox.Text = catalogOverride.Program.ToString(CultureInfo.InvariantCulture);
        OverrideBankNameBox.Text = catalogOverride.BankDisplayName ?? string.Empty;
        OverrideProgramNameBox.Text = catalogOverride.ProgramDisplayName ?? string.Empty;
    }

    private void OnApplyOverrideClick(object sender, RoutedEventArgs e)
    {
        if (!TryMidiByte(OverrideMsbBox.Text, "Bank MSB", out byte msb)
            || !TryMidiByte(OverrideLsbBox.Text, "Bank LSB", out byte lsb)
            || !TryMidiByte(OverrideProgramBox.Text, "Program", out byte program))
        {
            return;
        }
        string? bankName = NormalizeOptionalName(OverrideBankNameBox.Text);
        string? programName = NormalizeOptionalName(OverrideProgramNameBox.Text);
        InstrumentCatalogOverride normalized;
        try
        {
            normalized = new InstrumentCatalogOverride(msb, lsb, program, bankName, programName).Normalize();
        }
        catch (ArgumentException exception)
        {
            ShowValidation(exception.Message);
            return;
        }
        InstrumentCatalogOverrideDraft? edited = _addingOverride ? null : _activeOverride;
        if (_draft.Overrides.Any(value => !ReferenceEquals(value, edited)
            && value.BankMsb == msb
            && value.BankLsb == lsb
            && value.Program == program))
        {
            ShowValidation("An override already exists for that exact Bank and Program address.");
            return;
        }
        string? conflictingBankName = _draft.Overrides
            .Where(value => !ReferenceEquals(value, edited)
                && value.BankMsb == msb
                && value.BankLsb == lsb
                && value.BankDisplayName is not null)
            .Select(value => value.BankDisplayName)
            .FirstOrDefault(value => !string.Equals(value, bankName, StringComparison.Ordinal));
        if (bankName is not null && conflictingBankName is not null)
        {
            ShowValidation($"This Bank is already named '{conflictingBankName}' by another override.");
            return;
        }
        if (edited is null)
        {
            edited = new(normalized);
            _draft.Overrides.Add(edited);
        }
        else
        {
            edited.Update(normalized);
        }
        Reposition(
            _draft.Overrides,
            edited,
            static (left, right) =>
            {
                int msb = left.BankMsb.CompareTo(right.BankMsb);
                if (msb != 0) return msb;
                int lsb = left.BankLsb.CompareTo(right.BankLsb);
                return lsb != 0 ? lsb : left.Program.CompareTo(right.Program);
            });
        _activeOverride = edited;
        _addingOverride = false;
        RestoreSelection(OverrideList, edited);
        OverrideMsbBox.Text = edited.BankMsb.ToString(CultureInfo.InvariantCulture);
        OverrideLsbBox.Text = edited.BankLsb.ToString(CultureInfo.InvariantCulture);
        OverrideProgramBox.Text = edited.Program.ToString(CultureInfo.InvariantCulture);
        OverrideBankNameBox.Text = edited.BankDisplayName ?? string.Empty;
        OverrideProgramNameBox.Text = edited.ProgramDisplayName ?? string.Empty;
        OverrideList.ScrollIntoView(edited);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void OnDeleteOverrideClick(object sender, RoutedEventArgs e)
    {
        if (SelectedOverride is not { } catalogOverride)
        {
            ShowValidation("Select an override to delete.");
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Override,
                "deleting this User Override"))
        {
            return;
        }
        int index = _draft.Overrides.IndexOf(catalogOverride);
        InstrumentCatalogOverrideDraft? next;
        _suppressCatalogNavigation = true;
        try
        {
            _draft.Overrides.RemoveAt(index);
            OverrideList.SelectedIndex = _draft.Overrides.Count == 0
                ? -1
                : Math.Min(index, _draft.Overrides.Count - 1);
            next = SelectedOverride;
        }
        finally
        {
            _suppressCatalogNavigation = false;
        }
        LoadOverrideEditor(next);
        InvalidateCatalogResolution();
        HideValidation();
    }

    private void RefreshSelectedProgramResolution()
    {
        if (!IsInitialized || ResolvedProgramText is null)
        {
            return;
        }
        if (SelectedBank is not { } bank || SelectedProgram is not { } program)
        {
            ResolvedProgramText.Text = "Select a Program to preview its effective name and source.";
            return;
        }
        try
        {
            _draft.GeneralMidiEnabled = GeneralMidiEnabledBox.IsChecked == true;
            InstrumentAddress address = new(bank.BankMsb, bank.BankLsb, program.Program);
            _effectiveResolver ??= new(_draft.BuildResolutionSlice(address), _soundFonts);
            ResolvedProgramText.Text = _effectiveResolver.ResolveProgram(address).DisplayText;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or OverflowException)
        {
            ResolvedProgramText.Text = $"Resolution unavailable while the Catalog draft is invalid: {exception.Message}";
        }
    }

    private void InvalidateCatalogResolution()
    {
        _effectiveResolver = null;
        RefreshSelectedProgramResolution();
    }

    private void OnExportProfileClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is not { } profile)
        {
            ShowValidation("Select a Profile to export.");
            return;
        }
        SaveFileDialog dialog = new()
        {
            Title = "Export Instrument Catalog Profile",
            Filter = "Midora Instrument Catalog (*.midora-catalog.json)|*.midora-catalog.json|JSON (*.json)|*.json",
            FileName = SanitizeExportFileName(profile.DisplayName) + ".midora-catalog.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        InstrumentCatalogExchangeSaveResult result = InstrumentCatalogImportExport.Export(
            dialog.FileName,
            profile.ToModel());
        if (!result.Succeeded)
        {
            ShowValidation(result.Notice?.Error?.Message ?? result.Notice?.Message ?? "Catalog export failed.");
            return;
        }
        HideValidation();
    }

    private void OnImportProfileClick(object sender, RoutedEventArgs e)
    {
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "importing a Catalog Profile"))
        {
            return;
        }
        OpenFileDialog dialog = new()
        {
            Title = "Import Instrument Catalog Profile",
            Filter = "Midora Instrument Catalog (*.midora-catalog.json)|*.midora-catalog.json|JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            _importCandidate = InstrumentCatalogImportExport.Import(dialog.FileName);
            _importTargets.Clear();
            _importTargets.Add(new(null, "New Profile"));
            foreach (InstrumentCatalogProfileDraft profile in _draft.Profiles)
            {
                _importTargets.Add(new(profile, profile.DisplayName));
            }
            ImportSourceText.Text = $"{_importCandidate.DisplayName} · {_importCandidate.SourceDescription} · {_importCandidate.Banks.Count:N0} bank(s)";
            ImportTargetBox.SelectedItem = SelectedProfile is { } selected
                ? _importTargets.First(value => ReferenceEquals(value.Profile, selected))
                : _importTargets[0];
            SetCatalogOverlayState(ImportOverlay, visible: true);
            UpdateImportPreview();
            ImportTargetBox.Focus();
            HideValidation();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or System.Text.Json.JsonException
            or OverflowException)
        {
            ShowValidation(exception.Message);
        }
    }

    private void OnImportTargetChanged(object sender, SelectionChangedEventArgs e) => UpdateImportPreview();

    private void UpdateImportPreview()
    {
        _importMergePreview = null;
        ImportConflictList.ItemsSource = null;
        if (_importCandidate is null
            || ImportTargetBox.SelectedItem is not InstrumentCatalogImportTarget target)
        {
            ImportSummaryText.Text = string.Empty;
            MergeImportedProfileButton.IsEnabled = false;
            return;
        }
        if (target.Profile is null)
        {
            ImportSummaryText.Text = "Replace Profile will add this exchange document as a new User Profile. Merge requires an existing target.";
            MergeImportedProfileButton.IsEnabled = false;
            return;
        }
        try
        {
            _importMergePreview = InstrumentCatalogImportExport.PreviewMerge(
                target.Profile.ToModel(),
                _importCandidate);
            InstrumentCatalogMergePreview preview = _importMergePreview;
            ImportSummaryText.Text =
                $"Preview: {preview.AddedBanks:N0} bank(s) and {preview.AddedPrograms:N0} program(s) added; "
                + $"{preview.UnchangedPrograms:N0} imported program(s) unchanged; "
                + $"{preview.ReplacedProgramNames:N0} program name(s) overwritten. "
                + $"{preview.Conflicts.Count:N0} visible conflict(s).";
            ImportConflictList.ItemsSource = preview.Conflicts.Select(FormatConflict).ToArray();
            MergeImportedProfileButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or OverflowException)
        {
            ImportSummaryText.Text = exception.Message;
            MergeImportedProfileButton.IsEnabled = false;
        }
    }

    private void OnReplaceImportedProfileClick(object sender, RoutedEventArgs e)
    {
        if (_importCandidate is null
            || ImportTargetBox.SelectedItem is not InstrumentCatalogImportTarget target)
        {
            return;
        }
        InstrumentCatalogProfile profile = target.Profile is null
            ? _importCandidate.CreateUserProfile()
            : _importCandidate.CreateUserProfile(target.Profile.ProfileId, target.Profile.Enabled);
        InstrumentCatalogProfileDraft replacement = new(profile, _soundFonts);
        if (target.Profile is null)
        {
            _draft.Profiles.Add(replacement);
        }
        else
        {
            int index = _draft.Profiles.IndexOf(target.Profile);
            _draft.Profiles[index] = replacement;
        }
        CloseImportOverlay();
        ProfileList.SelectedItem = replacement;
        ProfileList.ScrollIntoView(replacement);
        InvalidateCatalogResolution();
    }

    private void OnMergeImportedProfileClick(object sender, RoutedEventArgs e)
    {
        if (_importMergePreview is null
            || ImportTargetBox.SelectedItem is not InstrumentCatalogImportTarget { Profile: { } target })
        {
            return;
        }
        InstrumentCatalogProfileDraft replacement = new(_importMergePreview.MergedProfile, _soundFonts);
        int index = _draft.Profiles.IndexOf(target);
        _draft.Profiles[index] = replacement;
        CloseImportOverlay();
        ProfileList.SelectedItem = replacement;
        ProfileList.ScrollIntoView(replacement);
        InvalidateCatalogResolution();
    }

    private void OnCancelImportClick(object sender, RoutedEventArgs e) => CloseImportOverlay();

    private void CloseImportOverlay()
    {
        SetCatalogOverlayState(ImportOverlay, visible: false);
        _importCandidate = null;
        _importMergePreview = null;
        _importTargets.Clear();
        ImportConflictList.ItemsSource = null;
    }

    private void OnScanSf2Click(object sender, RoutedEventArgs e)
    {
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "scanning another SF2"))
        {
            return;
        }
        ApplicationSoundFontPreference? selected = SelectConfiguredSf2();
        if (selected is not null)
        {
            _ = BeginSf2ScanAsync(selected, replacementProfileId: null);
        }
    }

    private void OnRescanSf2Click(object sender, RoutedEventArgs e)
    {
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.Bank | PendingCatalogEditor.Program,
                "rescanning this SF2 Profile"))
        {
            return;
        }
        if (SelectedProfile is not
            {
                SourceKind: InstrumentCatalogSourceKind.ImportedSf2,
                SourceSoundFontEntryId: { } sourceId
            } profile)
        {
            ShowValidation("Select an Imported SF2 Profile to rescan.");
            return;
        }
        ApplicationSoundFontPreference? source = _soundFonts.FirstOrDefault(value => value.EntryId == sourceId);
        if (source is null)
        {
            ShowValidation("The SoundFont associated with this Profile is no longer configured.");
            return;
        }
        _ = BeginSf2ScanAsync(source, profile.ProfileId);
    }

    private ApplicationSoundFontPreference? SelectConfiguredSf2()
    {
        ApplicationSoundFontPreference[] candidates = _soundFonts
            .Where(value => string.Equals(
                Path.GetExtension(value.Path),
                ".sf2",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            ShowValidation("No SF2 file is configured in Application Preferences.");
            return null;
        }
        if (candidates.Length == 1)
        {
            return candidates[0];
        }
        SelectionDialog dialog = CreateSf2SelectionDialog(candidates);
        if (IsVisible)
        {
            dialog.Owner = this;
        }
        return dialog.ShowDialog() == true
            ? dialog.SelectedValue as ApplicationSoundFontPreference
            : null;
    }

    internal static SelectionDialog CreateSf2SelectionDialog(IReadOnlyList<ApplicationSoundFontPreference> candidates) => new(
            "Scan SF2 Presets",
            "Select a configured SF2. Scanning reads only preset metadata and does not change audio settings.",
            candidates.Select(value => new SelectionDialogItem(
                value,
                Path.GetFileName(value.Path),
                $"{(value.Enabled ? "Enabled" : "Disabled")} · {value.Path}")),
            useSingleItemWheel: true);

    private async Task BeginSf2ScanAsync(
        ApplicationSoundFontPreference soundFont,
        InstrumentCatalogProfileId? replacementProfileId)
    {
        CancelActiveScan();
        CancellationTokenSource scanCancellation = new();
        _scanCancellation = scanCancellation;
        _scanResult = null;
        _scannedSoundFont = soundFont;
        _rescanProfileId = replacementProfileId;
        _scanBanks.Clear();
        ScanSourceText.Text = soundFont.Path;
        ScanProfileNameBox.Text = replacementProfileId.HasValue
            ? _draft.Profiles.First(value => value.ProfileId == replacementProfileId.Value).DisplayName
            : Path.GetFileNameWithoutExtension(soundFont.Path);
        ScanMappingPanel.Visibility = Visibility.Collapsed;
        ApplyScanButton.IsEnabled = false;
        ApplyScanButton.IsDefault = false;
        ScanStatusText.Text = "Scanning RIFF sfbk/pdta/phdr metadata…";
        SetCatalogOverlayState(ScanOverlay, visible: true);
        ScanProfileNameBox.Focus();
        try
        {
            Sf2PresetScanResult result = await Task.Run(
                () => Sf2PresetMetadataScanner.Scan(
                    soundFont.Path,
                    scanCancellation.Token),
                scanCancellation.Token);
            if (scanCancellation.IsCancellationRequested
                || !ReferenceEquals(_scanCancellation, scanCancellation))
            {
                return;
            }
            _scanResult = result;
            foreach (IGrouping<ushort, Sf2PresetMetadata> bank in result.Presets
                .GroupBy(value => value.RawBank)
                .OrderBy(value => value.Key))
            {
                _scanBanks.Add(new(bank.Key, bank.Count()));
            }
            ScanMappingPanel.Visibility = Visibility.Visible;
            ApplyScanButton.IsEnabled = true;
            ApplyScanButton.IsDefault = true;
            ApplyScanButton.Content = replacementProfileId.HasValue
                ? "Replace Snapshot"
                : "Import Snapshot";
            int extendedBanks = _scanBanks.Count(value => value.RawBank > 127);
            ScanStatusText.Text =
                $"{result.Presets.Count:N0} playable preset(s) in {_scanBanks.Count:N0} raw bank(s). "
                + (extendedBanks == 0
                    ? "All raw banks have the default MSB=raw bank / LSB=0 projection."
                    : $"{extendedBanks:N0} raw bank(s) above 127 require an explicit MIDI MSB/LSB mapping before import.");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                CloseScanOverlay();
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException
            or OverflowException)
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                CloseScanOverlay();
                ShowValidation(exception.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
            }
            scanCancellation.Dispose();
        }
    }

    private void OnApplyScanClick(object sender, RoutedEventArgs e)
    {
        if (_scanResult is null || _scannedSoundFont is null)
        {
            return;
        }
        string profileName;
        try
        {
            profileName = NormalizeRequiredName(ScanProfileNameBox.Text, "Profile name");
        }
        catch (ArgumentException exception)
        {
            ScanStatusText.Text = exception.Message;
            return;
        }
        List<Sf2BankProjectionRule> rules = new(_scanBanks.Count);
        foreach (Sf2BankProjectionDraft bank in _scanBanks)
        {
            if (!TryParseMidiByte(bank.TargetMsbText, out byte msb)
                || !TryParseMidiByte(bank.TargetLsbText, out byte lsb))
            {
                ScanStatusText.Text =
                    $"Raw bank {bank.RawBank} requires target Bank MSB and LSB values from 0 through 127.";
                return;
            }
            rules.Add(new(bank.RawBank, msb, lsb));
        }
        try
        {
            InstrumentCatalogProfileDraft? existing = _rescanProfileId.HasValue
                ? _draft.Profiles.FirstOrDefault(value => value.ProfileId == _rescanProfileId.Value)
                : null;
            InstrumentCatalogProfile profile = Sf2PresetMetadataScanner.CreateImportedProfile(
                _scanResult,
                _scannedSoundFont.EntryId,
                profileName,
                rules,
                _rescanProfileId,
                existing?.Enabled ?? true);
            InstrumentCatalogProfileDraft replacement = new(profile, _soundFonts);
            if (existing is null)
            {
                _draft.Profiles.Add(replacement);
            }
            else
            {
                _draft.Profiles[_draft.Profiles.IndexOf(existing)] = replacement;
            }
            CloseScanOverlay();
            ProfileList.SelectedItem = replacement;
            ProfileList.ScrollIntoView(replacement);
            InvalidateCatalogResolution();
            HideValidation();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or OverflowException)
        {
            ScanStatusText.Text = exception.Message;
        }
    }

    private void OnCancelScanClick(object sender, RoutedEventArgs e)
    {
        CancelActiveScan();
        CloseScanOverlay();
    }

    private void CancelActiveScan() => _scanCancellation?.Cancel();

    private void CloseScanOverlay()
    {
        SetCatalogOverlayState(ScanOverlay, visible: false);
        _scanResult = null;
        _scannedSoundFont = null;
        _rescanProfileId = null;
        _scanBanks.Clear();
        ApplyScanButton.IsDefault = false;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        HideValidation();
        if (HasPendingCatalogItemEdits())
        {
            ShowValidation(
                "Apply or restore the pending Bank, Program, or User Override field edits before saving the Catalog.");
            return;
        }
        try
        {
            _draft.GeneralMidiEnabled = GeneralMidiEnabledBox.IsChecked == true;
            InstrumentCatalogState candidate = _draft.Build();
            string? submitError = _submit?.Invoke(candidate);
            if (submitError is not null)
            {
                ShowValidation(submitError);
                return;
            }
            Result = candidate;
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException)
        {
            ShowValidation(exception.Message);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CancelActiveScan();
        DialogResult = false;
    }

    private void OnClosed(object? sender, EventArgs e) => CancelActiveScan();

    private void SetCatalogOverlayState(UIElement overlay, bool visible)
    {
        overlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        bool editorEnabled = !visible;
        CatalogTitleBar.IsEnabled = editorEnabled;
        CatalogCloseButton.IsEnabled = editorEnabled;
        CatalogHeader.IsEnabled = editorEnabled;
        CatalogTabs.IsEnabled = editorEnabled;
        CatalogFooter.IsEnabled = editorEnabled;
        ValidationBorder.IsEnabled = editorEnabled;
        CatalogOkButton.IsDefault = editorEnabled;
        if (!visible)
        {
            CatalogTabs.Focus();
        }
    }

    private void OnCatalogTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingCatalog
            || _suppressCatalogNavigation
            || !ReferenceEquals(e.OriginalSource, CatalogTabs))
        {
            return;
        }
        TabItem? requested = CatalogTabs.SelectedItem as TabItem;
        if (ReferenceEquals(requested, _activeCatalogTab))
        {
            return;
        }
        if (!TryLeavePendingCatalogEditors(PendingCatalogEditor.All, "switching Catalog tabs"))
        {
            RestoreCatalogTab();
            return;
        }
        _activeCatalogTab = requested;
    }

    private bool HasPendingCatalogItemEdits() =>
        HasPendingBankEdits()
        || HasPendingProgramEdits()
        || HasPendingOverrideEdits();

    private bool HasPendingBankEdits() => HasPendingBankEdits(
        _addingBank,
        _activeBank,
        BankMsbBox.Text,
        BankLsbBox.Text,
        BankNameBox.Text);

    internal static bool HasPendingBankEdits(
        bool adding,
        InstrumentCatalogBankDraft? active,
        string msbText,
        string lsbText,
        string nameText) =>
        adding
            ? !string.Equals(msbText, "0", StringComparison.Ordinal)
                || !string.Equals(lsbText, "0", StringComparison.Ordinal)
                || nameText.Length != 0
            : active is not null
                && (!string.Equals(
                        msbText,
                        active.BankMsb.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    || !string.Equals(
                        lsbText,
                        active.BankLsb.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    || !string.Equals(nameText, active.DisplayName ?? string.Empty, StringComparison.Ordinal));

    private bool HasPendingProgramEdits() => HasPendingProgramEdits(
        _addingProgram,
        _activeProgram,
        ProgramNumberBox.Text,
        ProgramNameBox.Text);

    internal static bool HasPendingProgramEdits(
        bool adding,
        InstrumentCatalogProgramDraft? active,
        string programText,
        string nameText) =>
        adding
            ? !string.Equals(programText, "0", StringComparison.Ordinal)
                || nameText.Length != 0
            : active is not null
                && (!string.Equals(
                        programText,
                        active.Program.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    || !string.Equals(nameText, active.DisplayName, StringComparison.Ordinal));

    private bool HasPendingOverrideEdits() => HasPendingOverrideEdits(
        _addingOverride,
        _activeOverride,
        OverrideMsbBox.Text,
        OverrideLsbBox.Text,
        OverrideProgramBox.Text,
        OverrideBankNameBox.Text,
        OverrideProgramNameBox.Text);

    internal static bool HasPendingOverrideEdits(
        bool adding,
        InstrumentCatalogOverrideDraft? active,
        string msbText,
        string lsbText,
        string programText,
        string bankNameText,
        string programNameText) =>
        adding
            ? !string.Equals(msbText, "0", StringComparison.Ordinal)
                || !string.Equals(lsbText, "0", StringComparison.Ordinal)
                || !string.Equals(programText, "0", StringComparison.Ordinal)
                || bankNameText.Length != 0
                || programNameText.Length != 0
            : active is not null
                && (!string.Equals(
                        msbText,
                        active.BankMsb.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    || !string.Equals(
                        lsbText,
                        active.BankLsb.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    || !string.Equals(
                        programText,
                        active.Program.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal)
                    || !string.Equals(bankNameText, active.BankDisplayName ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(programNameText, active.ProgramDisplayName ?? string.Empty, StringComparison.Ordinal));

    private bool TryLeavePendingCatalogEditors(PendingCatalogEditor editors, string action)
    {
        if (editors.HasFlag(PendingCatalogEditor.Bank) && HasPendingBankEdits())
        {
            return BlockCatalogNavigation("Bank", action, BankMsbBox);
        }
        if (editors.HasFlag(PendingCatalogEditor.Program) && HasPendingProgramEdits())
        {
            return BlockCatalogNavigation("Program", action, ProgramNumberBox);
        }
        if (editors.HasFlag(PendingCatalogEditor.Override) && HasPendingOverrideEdits())
        {
            return BlockCatalogNavigation("User Override", action, OverrideMsbBox);
        }
        return true;
    }

    private bool BlockCatalogNavigation(string editorName, string action, TextBox focusTarget)
    {
        ShowValidation($"Apply or restore the pending {editorName} fields before {action}.");
        focusTarget.Focus();
        focusTarget.SelectAll();
        return false;
    }

    private void SelectProfileForEditing(InstrumentCatalogProfileDraft? profile)
    {
        RestoreSelection(ProfileList, profile);
        LoadProfileEditor(profile);
    }

    private void RestoreSelection(ListBox list, object? item)
    {
        bool wasSuppressed = _suppressCatalogNavigation;
        _suppressCatalogNavigation = true;
        try
        {
            list.SelectedItem = item;
        }
        finally
        {
            _suppressCatalogNavigation = wasSuppressed;
        }
    }

    private void RestoreCatalogTab()
    {
        bool wasSuppressed = _suppressCatalogNavigation;
        _suppressCatalogNavigation = true;
        try
        {
            CatalogTabs.SelectedItem = _activeCatalogTab;
        }
        finally
        {
            _suppressCatalogNavigation = wasSuppressed;
        }
    }

    [Flags]
    private enum PendingCatalogEditor
    {
        Bank = 1,
        Program = 2,
        Override = 4,
        All = Bank | Program | Override
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }
        if (ImportOverlay.Visibility == Visibility.Visible)
        {
            CloseImportOverlay();
            e.Handled = true;
        }
        else if (ScanOverlay.Visibility == Visibility.Visible)
        {
            CancelActiveScan();
            CloseScanOverlay();
            e.Handled = true;
        }
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private bool TryMidiByte(string text, string label, out byte value)
    {
        if (TryParseMidiByte(text, out value))
        {
            return true;
        }
        ShowValidation($"{label} must be an integer from 0 through 127.");
        return false;
    }

    private static bool TryParseMidiByte(string text, out byte value) =>
        byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
        && value <= 127;

    private static string NormalizeRequiredName(string value, string label)
    {
        string result = (value ?? string.Empty).Trim().Normalize();
        if (result.Length == 0 || result.Length > InstrumentCatalogLimits.MaximumDisplayNameCodeUnits)
        {
            throw new ArgumentException(
                $"{label} must contain 1 through {InstrumentCatalogLimits.MaximumDisplayNameCodeUnits} characters.");
        }
        if (result.Any(char.IsControl))
        {
            throw new ArgumentException($"{label} cannot contain control characters.");
        }
        return result;
    }

    private static string? NormalizeOptionalName(string value)
    {
        string result = (value ?? string.Empty).Trim().Normalize();
        if (result.Length == 0)
        {
            return null;
        }
        return NormalizeRequiredName(result, "Display name");
    }

    private string GetUniqueProfileName(string baseName)
    {
        string result = baseName;
        int suffix = 2;
        HashSet<string> names = _draft.Profiles
            .Select(value => value.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains(result))
        {
            result = $"{baseName} {suffix++}";
        }
        return result;
    }

    private static string FormatConflict(InstrumentCatalogMergeConflict conflict) =>
        conflict.Kind == InstrumentCatalogMergeConflictKind.BankDisplayName
            ? $"Bank {conflict.Address.BankMsb}.{conflict.Address.BankLsb}: '{conflict.ExistingValue}' → '{conflict.ImportedValue}'"
            : $"Bank {conflict.Address.BankMsb}.{conflict.Address.BankLsb} / Program {conflict.Address.Program}: '{conflict.ExistingValue}' → '{conflict.ImportedValue}'";

    private static string SanitizeExportFileName(string value)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return result.Length == 0 ? "Instrument Catalog" : result;
    }

    private static void Reposition<T>(
        ObservableCollection<T> values,
        T edited,
        Comparison<T> comparison)
        where T : class
    {
        int source = values.IndexOf(edited);
        if (source < 0)
        {
            throw new InvalidOperationException("The edited catalog item is not in its collection.");
        }
        int target = values.Count(value =>
            !ReferenceEquals(value, edited)
            && comparison(value, edited) < 0);
        if (source != target)
        {
            values.Move(source, target);
        }
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationBorder.Visibility = Visibility.Visible;
    }

    private void HideValidation()
    {
        ValidationText.Text = string.Empty;
        ValidationBorder.Visibility = Visibility.Collapsed;
    }
}

internal sealed class InstrumentCatalogDialogDraft
{
    private readonly ApplicationSoundFontPreference[] _soundFonts;

    public InstrumentCatalogDialogDraft(
        InstrumentCatalogState initial,
        IReadOnlyList<ApplicationSoundFontPreference> soundFonts)
    {
        _soundFonts = soundFonts.ToArray();
        GeneralMidiEnabled = initial.GeneralMidiEnabled;
        foreach (InstrumentCatalogProfile profile in initial.Profiles)
        {
            if (profile.SourceKind == InstrumentCatalogSourceKind.BuiltIn)
            {
                continue;
            }
            Profiles.Add(new(profile, _soundFonts));
        }
        foreach (InstrumentCatalogOverride catalogOverride in initial.Overrides)
        {
            Overrides.Add(new(catalogOverride));
        }
    }

    public bool GeneralMidiEnabled { get; set; }
    public ObservableCollection<InstrumentCatalogProfileDraft> Profiles { get; } = [];
    public ObservableCollection<InstrumentCatalogOverrideDraft> Overrides { get; } = [];

    public InstrumentCatalogState Build() => new InstrumentCatalogState(
        GeneralMidiEnabled,
        Profiles.Select(value => value.ToModel()).ToArray(),
        Overrides.Select(value => value.ToModel()).ToArray()).Normalize();

    public InstrumentCatalogState BuildResolutionSlice(InstrumentAddress address)
    {
        address.Validate();
        InstrumentCatalogProfile[] profiles = Profiles
            .Select(value => value.ToResolutionSlice(address))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
        InstrumentCatalogOverride[] overrides = Overrides
            .Where(value => value.BankMsb == address.BankMsb
                && value.BankLsb == address.BankLsb
                && value.Program == address.Program)
            .Select(value => value.ToModel())
            .ToArray();
        return new(GeneralMidiEnabled, profiles, overrides);
    }
}

internal sealed class InstrumentCatalogProfileDraft : INotifyPropertyChanged
{
    private string _displayName;
    private bool _enabled;

    public InstrumentCatalogProfileDraft(
        InstrumentCatalogProfile model,
        IReadOnlyList<ApplicationSoundFontPreference> soundFonts)
    {
        ProfileId = model.ProfileId;
        _displayName = model.DisplayName;
        _enabled = model.Enabled;
        SourceKind = model.SourceKind;
        SourceSoundFontEntryId = model.SourceSoundFontEntryId;
        SourceLabel = SourceKind switch
        {
            InstrumentCatalogSourceKind.User => "User Profile",
            InstrumentCatalogSourceKind.ImportedSf2 => soundFonts
                .FirstOrDefault(value => value.EntryId == SourceSoundFontEntryId) is { } soundFont
                    ? $"Imported SF2 · {Path.GetFileName(soundFont.Path)}"
                    : "Imported SF2 · missing SoundFont",
            _ => "Built-in"
        };
        foreach (InstrumentCatalogBank bank in model.Banks)
        {
            Banks.Add(new(bank));
        }
    }

    public static InstrumentCatalogProfileDraft CreateNew(string name) => new(
        new InstrumentCatalogProfile(
            InstrumentCatalogProfileId.Create(),
            name,
            true,
            InstrumentCatalogSourceKind.User,
            null,
            []),
        []);

    public InstrumentCatalogProfileId ProfileId { get; }
    public InstrumentCatalogSourceKind SourceKind { get; }
    public SoundFontEntryId? SourceSoundFontEntryId { get; }
    public string SourceLabel { get; }
    public ObservableCollection<InstrumentCatalogBankDraft> Banks { get; } = [];

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (string.Equals(_displayName, value, StringComparison.Ordinal)) return;
            _displayName = value;
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
    }

    public InstrumentCatalogProfile ToModel() => new InstrumentCatalogProfile(
        ProfileId,
        DisplayName,
        Enabled,
        SourceKind,
        SourceSoundFontEntryId,
        Banks.Select(value => value.ToModel()).ToArray()).Normalize();

    public InstrumentCatalogProfile? ToResolutionSlice(InstrumentAddress address)
    {
        InstrumentCatalogBankDraft? bank = FindBank(address.Bank);
        InstrumentCatalogProgramDraft? program = bank?.FindProgram(address.Program);
        if (bank is null || program is null)
        {
            return null;
        }
        return new InstrumentCatalogProfile(
            ProfileId,
            DisplayName,
            Enabled,
            SourceKind,
            SourceSoundFontEntryId,
            [new InstrumentCatalogBank(
                bank.BankMsb,
                bank.BankLsb,
                bank.DisplayName,
                [program.ToModel()])]);
    }

    private InstrumentCatalogBankDraft? FindBank(InstrumentBankAddress address)
    {
        int low = 0;
        int high = Banks.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            InstrumentCatalogBankDraft candidate = Banks[middle];
            int comparison = candidate.BankMsb.CompareTo(address.BankMsb);
            if (comparison == 0)
            {
                comparison = candidate.BankLsb.CompareTo(address.BankLsb);
            }
            if (comparison == 0) return candidate;
            if (comparison < 0) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class InstrumentCatalogBankDraft : INotifyPropertyChanged
{
    public InstrumentCatalogBankDraft(
        byte bankMsb,
        byte bankLsb,
        string? displayName,
        IEnumerable<InstrumentCatalogProgramDraft> programs)
    {
        BankMsb = bankMsb;
        BankLsb = bankLsb;
        DisplayName = displayName;
        foreach (InstrumentCatalogProgramDraft program in programs)
        {
            Programs.Add(program);
        }
    }

    public InstrumentCatalogBankDraft(InstrumentCatalogBank model)
        : this(
            model.BankMsb,
            model.BankLsb,
            model.DisplayName,
            model.Programs.Select(value => new InstrumentCatalogProgramDraft(value)))
    {
    }

    public byte BankMsb { get; private set; }
    public byte BankLsb { get; private set; }
    public string? DisplayName { get; private set; }
    public ObservableCollection<InstrumentCatalogProgramDraft> Programs { get; } = [];
    public string DisplayText =>
        $"MSB {BankMsb} / LSB {BankLsb} · {DisplayName ?? "Unnamed Bank"} · {Programs.Count:N0} program(s)";

    public void Update(byte bankMsb, byte bankLsb, string? displayName)
    {
        BankMsb = bankMsb;
        BankLsb = bankLsb;
        DisplayName = displayName;
        PropertyChanged?.Invoke(this, new(nameof(DisplayText)));
    }

    public InstrumentCatalogBank ToModel() => new InstrumentCatalogBank(
        BankMsb,
        BankLsb,
        DisplayName,
        Programs.Select(value => value.ToModel()).ToArray()).Normalize();

    public InstrumentCatalogProgramDraft? FindProgram(byte program)
    {
        int low = 0;
        int high = Programs.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            InstrumentCatalogProgramDraft candidate = Programs[middle];
            if (candidate.Program == program) return candidate;
            if (candidate.Program < program) low = middle + 1;
            else high = middle - 1;
        }
        return null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class InstrumentCatalogProgramDraft : INotifyPropertyChanged
{
    public InstrumentCatalogProgramDraft(byte program, string displayName)
    {
        Program = program;
        DisplayName = displayName;
    }

    public InstrumentCatalogProgramDraft(InstrumentCatalogProgram model)
        : this(model.Program, model.DisplayName)
    {
    }

    public byte Program { get; private set; }
    public string DisplayName { get; private set; }
    public string DisplayText => $"Program {Program} · {DisplayName}";

    public void Update(byte program, string displayName)
    {
        Program = program;
        DisplayName = displayName;
        PropertyChanged?.Invoke(this, new(nameof(DisplayText)));
    }

    public InstrumentCatalogProgram ToModel() => new InstrumentCatalogProgram(Program, DisplayName).Normalize();

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class InstrumentCatalogOverrideDraft : INotifyPropertyChanged
{
    public InstrumentCatalogOverrideDraft(InstrumentCatalogOverride model)
    {
        Update(model.Normalize());
    }

    public byte BankMsb { get; private set; }
    public byte BankLsb { get; private set; }
    public byte Program { get; private set; }
    public string? BankDisplayName { get; private set; }
    public string? ProgramDisplayName { get; private set; }
    public string DisplayText =>
        $"MSB {BankMsb} / LSB {BankLsb} / Program {Program} · {ProgramDisplayName ?? BankDisplayName ?? "Unnamed"}";

    public void Update(InstrumentCatalogOverride model)
    {
        BankMsb = model.BankMsb;
        BankLsb = model.BankLsb;
        Program = model.Program;
        BankDisplayName = model.BankDisplayName;
        ProgramDisplayName = model.ProgramDisplayName;
        PropertyChanged?.Invoke(this, new(nameof(DisplayText)));
    }

    public InstrumentCatalogOverride ToModel() => new InstrumentCatalogOverride(
        BankMsb,
        BankLsb,
        Program,
        BankDisplayName,
        ProgramDisplayName).Normalize();

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class Sf2BankProjectionDraft
{
    public Sf2BankProjectionDraft(ushort rawBank, int presetCount)
    {
        RawBank = rawBank;
        PresetCount = presetCount;
        TargetMsbText = rawBank <= 127
            ? rawBank.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        TargetLsbText = rawBank <= 127 ? "0" : string.Empty;
    }

    public ushort RawBank { get; }
    public int PresetCount { get; }
    public string PresetCountText => PresetCount == 1 ? "1 preset" : $"{PresetCount:N0} presets";
    public string TargetMsbText { get; set; }
    public string TargetLsbText { get; set; }
}

internal sealed record InstrumentCatalogImportTarget(
    InstrumentCatalogProfileDraft? Profile,
    string DisplayName);
