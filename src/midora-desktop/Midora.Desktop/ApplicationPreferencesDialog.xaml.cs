using System.Globalization;
using System.IO;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Midora.Application;
using Midora.Audio;
using Midora.Common;
using Midora.Domain;

namespace Midora.Desktop;

internal enum ApplicationPreferencesPage { Audio, SoundFonts, Appearance }

public partial class ApplicationPreferencesDialog : Window
{
    private const decimal BytesPerGibibyte = 1024m * 1024m * 1024m;
    private readonly ApplicationPreferences _initial;
    private readonly InstrumentCatalogState _instrumentCatalog;
    private readonly Func<ApplicationPreferences, string?>? _submit;
    private readonly ObservableCollection<SoundFontDraftItem> _soundFonts = [];
    private InstrumentCatalogResolver? _soundFontCatalogResolver;
    private bool _suppressSoundFontCatalogRefresh;

    public ApplicationPreferencesDialog(ApplicationPreferences initial)
        : this(initial, InstrumentCatalogState.Default, submit: null)
    {
    }

    public ApplicationPreferencesDialog(
        ApplicationPreferences initial,
        InstrumentCatalogState instrumentCatalog)
        : this(initial, instrumentCatalog, submit: null)
    {
    }

    internal ApplicationPreferencesDialog(
        ApplicationPreferences initial,
        InstrumentCatalogState instrumentCatalog,
        Func<ApplicationPreferences, string?>? submit,
        ApplicationPreferencesPage initialPage = ApplicationPreferencesPage.Audio)
    {
        _initial = initial ?? throw new ArgumentNullException(nameof(initial));
        _instrumentCatalog = instrumentCatalog
            ?? throw new ArgumentNullException(nameof(instrumentCatalog));
        _submit = submit;
        InitializeComponent();
        StopCursorBox.ItemsSource = Enum.GetValues<StopCursorBehavior>();
        LanguageBox.ItemsSource = new[] { AppearancePreferences.EnglishLanguage };
        SoundFontListBox.ItemsSource = _soundFonts;
        _soundFonts.CollectionChanged += OnSoundFontsCollectionChanged;
        Populate(initial);
        PreferencesTabs.SelectedIndex = (int)initialPage;
        Loaded += OnLoaded;
    }

    public ApplicationPreferences? Result { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        string? selectedId = (DeviceBox.SelectedItem as DeviceChoice)?.Id;
        DeviceStatusText.Text = "Reading enabled output devices from the formal audio worker…";
        try
        {
            IReadOnlyList<FormalAudioOutputDevice> devices =
                await FormalAudioOutputDeviceEnumerator.EnumerateAsync();
            List<DeviceChoice> choices = [DeviceChoice.SystemDefault];
            choices.AddRange(devices.Select(device => new DeviceChoice(
                device.Id,
                $"{device.Name}{(device.IsSystemDefault ? " — System Default" : string.Empty)} · {device.SampleRate:N0} Hz · {device.ChannelCount} ch")));
            if (selectedId is not null && choices.All(choice => choice.Id != selectedId))
            {
                choices.Add(new(selectedId, "Stored endpoint (currently unavailable)"));
            }
            DeviceBox.ItemsSource = choices;
            DeviceBox.SelectedItem = choices.First(choice => choice.Id == selectedId);
            DeviceStatusText.Text = devices.Count == 0
                ? "No enabled output device was reported. System Default remains selected but playback will be unavailable."
                : $"{devices.Count} enabled output device(s). Input, loopback, disabled, unplugged, and not-present endpoints are excluded by the worker.";
        }
        catch (Exception exception)
        {
            DeviceStatusText.Text = $"Device enumeration is unavailable: {exception.Message}";
        }
    }

    private void Populate(ApplicationPreferences preferences)
    {
        PlaybackMasterVolumeBox.Text = preferences.Playback.MasterVolumeDecibels
            .ToString(CultureInfo.InvariantCulture);
        PlaybackLimiterBox.IsChecked = preferences.Playback.LimiterEnabled;
        StopCursorBox.SelectedItem = preferences.Playback.StopCursorBehavior;
        LanguageBox.SelectedItem = preferences.Appearance.Language;
        EventLaneLinesBox.IsChecked = preferences.Appearance.ShowEventLaneLines;
        RealtimeAudioPreferences realtime = preferences.RealtimeAudio;
        DeviceChoice current = realtime.PlaybackOutputDeviceId is null
            ? DeviceChoice.SystemDefault
            : new(realtime.PlaybackOutputDeviceId, "Stored endpoint");
        DeviceBox.ItemsSource = new[] { current };
        DeviceBox.SelectedItem = current;
        RenderAheadBox.Text = realtime.RenderAheadMilliseconds.ToString(CultureInfo.InvariantCulture);
        DeviceRequestBox.Text = realtime.DeviceBufferRequestMilliseconds.ToString(CultureInfo.InvariantCulture);
        VoicesBox.Text = realtime.MaximumSampleVoicesPerUnitStream.ToString(CultureInfo.InvariantCulture);
        CacheQuotaBox.Text = (preferences.AudioCache.MaximumReusableBytes / BytesPerGibibyte)
            .ToString("0.###", CultureInfo.InvariantCulture);
        _suppressSoundFontCatalogRefresh = true;
        try
        {
            foreach (SoundFontDraftItem item in _soundFonts)
            {
                item.PropertyChanged -= OnSoundFontDraftItemPropertyChanged;
            }
            _soundFonts.Clear();
            foreach (ApplicationSoundFontPreference soundFont in preferences.SoundFonts)
            {
                AddSoundFontDraftItem(new(
                    soundFont.EntryId,
                    soundFont.Path,
                    soundFont.Enabled,
                    soundFont.Target));
            }
        }
        finally
        {
            _suppressSoundFontCatalogRefresh = false;
        }
        UpdateSelectedSoundFontCount();
        RefreshSoundFontCatalogNames(rebuildResolver: true);
    }

    private void OnAddSoundFontsClick(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Add SoundFonts",
            Filter = "SoundFonts (*.sf2;*.sfz)|*.sf2;*.sfz|SoundFont 2 (*.sf2)|*.sf2|SFZ Instrument (*.sfz)|*.sfz|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
            InitialDirectory = _soundFonts.Count == 0
                ? null
                : Path.GetDirectoryName(_soundFonts[^1].Path)
        };
        if (dialog.ShowDialog(this) == true)
        {
            _suppressSoundFontCatalogRefresh = true;
            try
            {
                foreach (string selected in dialog.FileNames)
                {
                    string path = Path.GetFullPath(selected);
                    if (_soundFonts.Any(value => string.Equals(
                            value.Path,
                            path,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    AddSoundFontDraftItem(new(
                        SoundFontEntryId.Create(),
                        path,
                        enabled: true,
                        target: null));
                }
            }
            finally
            {
                _suppressSoundFontCatalogRefresh = false;
            }
            UpdateSelectedSoundFontCount();
            RefreshSoundFontCatalogNames(rebuildResolver: true);
            SoundFontListBox.SelectedItem = _soundFonts.LastOrDefault();
        }
    }

    private void OnRemoveSoundFontClick(object sender, RoutedEventArgs e)
    {
        if (SoundFontListBox.SelectedItem is not SoundFontDraftItem selected)
        {
            return;
        }
        int index = _soundFonts.IndexOf(selected);
        selected.PropertyChanged -= OnSoundFontDraftItemPropertyChanged;
        _soundFonts.RemoveAt(index);
        if (_soundFonts.Count != 0)
        {
            SoundFontListBox.SelectedIndex = Math.Min(index, _soundFonts.Count - 1);
        }
    }

    private void OnMoveSoundFontUpClick(object sender, RoutedEventArgs e) =>
        MoveSelectedSoundFont(-1);

    private void OnMoveSoundFontDownClick(object sender, RoutedEventArgs e) =>
        MoveSelectedSoundFont(1);

    private void OnSoundFontsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSelectedSoundFontCount();
        RefreshSoundFontCatalogNames(rebuildResolver: true);
    }

    private void OnSoundFontDraftItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(
                e.PropertyName,
                nameof(SoundFontDraftItem.ResolvedTargetText),
                StringComparison.Ordinal))
        {
            return;
        }
        if (string.Equals(e.PropertyName, nameof(SoundFontDraftItem.Enabled), StringComparison.Ordinal))
        {
            UpdateSelectedSoundFontCount();
            RefreshSoundFontCatalogNames(rebuildResolver: true);
            return;
        }
        RefreshSoundFontCatalogNames();
    }

    private void AddSoundFontDraftItem(SoundFontDraftItem item)
    {
        item.PropertyChanged += OnSoundFontDraftItemPropertyChanged;
        _soundFonts.Add(item);
    }

    private void UpdateSelectedSoundFontCount()
    {
        int count = _soundFonts.Count(item => item.Enabled);
        SoundFontCountText.Text = count == 1
            ? "1 SoundFont selected"
            : $"{count} SoundFonts selected";
    }

    private void RefreshSoundFontCatalogNames(bool rebuildResolver = false)
    {
        if (_suppressSoundFontCatalogRefresh)
        {
            return;
        }
        if (rebuildResolver || _soundFontCatalogResolver is null)
        {
            InstrumentCatalogSoundFontEntry[] order = _soundFonts
                .Select(value => new InstrumentCatalogSoundFontEntry(value.EntryId, value.Enabled))
                .ToArray();
            _soundFontCatalogResolver = new(_instrumentCatalog, order);
        }
        _suppressSoundFontCatalogRefresh = true;
        try
        {
            foreach (SoundFontDraftItem item in _soundFonts)
            {
                item.UpdateResolvedTargetText(_soundFontCatalogResolver);
            }
        }
        finally
        {
            _suppressSoundFontCatalogRefresh = false;
        }
    }

    private void OnSoundFontListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollViewer? scrollViewer = FindVisualDescendant<ScrollViewer>(SoundFontListBox);
        if (scrollViewer is null)
        {
            return;
        }
        const double pixelsPerWheelNotch = 24d;
        double notchCount = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        scrollViewer.ScrollToVerticalOffset(
            Math.Clamp(
                scrollViewer.VerticalOffset - (notchCount * pixelsPerWheelNotch),
                0,
                scrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }
            T? nested = FindVisualDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    private void MoveSelectedSoundFont(int delta)
    {
        if (SoundFontListBox.SelectedItem is not SoundFontDraftItem selected)
        {
            return;
        }
        int source = _soundFonts.IndexOf(selected);
        int target = source + delta;
        if (target < 0 || target >= _soundFonts.Count)
        {
            return;
        }
        _soundFonts.Move(source, target);
        SoundFontListBox.SelectedItem = selected;
    }

    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        Populate(ApplicationPreferences.Default);
        DeviceStatusText.Text = "Defaults restored in the form. Select Apply to persist them.";
        HideValidation();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        HideValidation();
        if (!TryInt(RenderAheadBox.Text, "Render-Ahead", 20, 2_000, out int renderAhead)
            || !TryInt(DeviceRequestBox.Text, "Device Buffer Request", 5, 200, out int deviceRequest)
            || !TryInt(VoicesBox.Text, "Realtime Maximum Sample Voices per Unit Stream", 1, 16_777_216, out int voices))
        {
            return;
        }
        if (!double.TryParse(
                PlaybackMasterVolumeBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double masterVolumeDecibels)
            || !double.IsFinite(masterVolumeDecibels)
            || masterVolumeDecibels is < -float.MaxValue or > 0)
        {
            ShowValidation("Playback Master Volume must be a finite dB value no greater than 0.");
            PlaybackMasterVolumeBox.Focus();
            return;
        }
        if (StopCursorBox.SelectedItem is not StopCursorBehavior stopCursorBehavior)
        {
            ShowValidation("Select a Stop Cursor Behavior.");
            StopCursorBox.Focus();
            return;
        }
        if (!decimal.TryParse(
                CacheQuotaBox.Text,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out decimal quotaGib)
            || quotaGib < 0
            || quotaGib > long.MaxValue / BytesPerGibibyte)
        {
            ShowValidation("Maximum Reusable Audio Cache must be a non-negative GiB value within the Int64 byte range.");
            CacheQuotaBox.Focus();
            return;
        }

        try
        {
            long quotaBytes = decimal.ToInt64(decimal.Round(
                quotaGib * BytesPerGibibyte,
                0,
                MidpointRounding.AwayFromZero));
            ApplicationPreferences candidate = _initial with
            {
                RealtimeAudio = new(
                    (DeviceBox.SelectedItem as DeviceChoice)?.Id,
                    renderAhead,
                    deviceRequest,
                    voices),
                AudioCache = new AudioCachePreferences(
                    MidoraProgramData.Current.AudioCacheDirectory,
                    quotaBytes).Normalize(),
                Playback = new PlaybackPreferences(
                    masterVolumeDecibels,
                    PlaybackLimiterBox.IsChecked == true,
                    stopCursorBehavior),
                Appearance = new AppearancePreferences(
                    LanguageBox.SelectedItem as string
                        ?? AppearancePreferences.EnglishLanguage)
                {
                    ShowEventLaneLines = EventLaneLinesBox.IsChecked == true
                },
                SoundFonts = _soundFonts
                    .Select(value => value.ToPreference())
                    .ToArray()
            };
            candidate.Validate();
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
            or InvalidOperationException
            or NotSupportedException)
        {
            ShowValidation(exception.Message);
        }
    }

    private bool TryInt(string text, string label, int minimum, int maximum, out int result)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result)
            && result >= minimum
            && result <= maximum)
        {
            return true;
        }
        ShowValidation($"{label} must be an integer from {minimum:N0} through {maximum:N0}.");
        return false;
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

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed record DeviceChoice(string? Id, string DisplayName)
    {
        public static DeviceChoice SystemDefault { get; } = new(null, "System Default");
    }

    private sealed class SoundFontDraftItem : INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _hasTarget;
        private string _bankMsbText;
        private string _bankLsbText;
        private string _programText;
        private string _resolvedTargetText = string.Empty;

        public SoundFontDraftItem(
            SoundFontEntryId entryId,
            string path,
            bool enabled,
            SoundFontTarget? target)
        {
            entryId.Validate();
            EntryId = entryId;
            Path = System.IO.Path.GetFullPath(path);
            _enabled = enabled;
            _hasTarget = IsSfz || target is not null;
            SoundFontTarget effectiveTarget = target ?? new(0, 0, 0);
            _bankMsbText = effectiveTarget.BankMsb.ToString(CultureInfo.InvariantCulture);
            _bankLsbText = effectiveTarget.BankLsb.ToString(CultureInfo.InvariantCulture);
            _programText = effectiveTarget.Program.ToString(CultureInfo.InvariantCulture);
        }

        public SoundFontEntryId EntryId { get; }
        public string Path { get; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public bool IsSfz => string.Equals(
            System.IO.Path.GetExtension(Path),
            ".sfz",
            StringComparison.OrdinalIgnoreCase);
        public bool TargetOptional => !IsSfz;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        public bool HasTarget
        {
            get => _hasTarget;
            set
            {
                bool normalized = IsSfz || value;
                if (_hasTarget == normalized)
                {
                    return;
                }
                _hasTarget = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTarget)));
            }
        }

        public string BankMsbText
        {
            get => _bankMsbText;
            set => SetText(ref _bankMsbText, value, nameof(BankMsbText));
        }

        public string BankLsbText
        {
            get => _bankLsbText;
            set => SetText(ref _bankLsbText, value, nameof(BankLsbText));
        }

        public string ProgramText
        {
            get => _programText;
            set => SetText(ref _programText, value, nameof(ProgramText));
        }

        public string ResolvedTargetText
        {
            get => _resolvedTargetText;
            private set => SetText(ref _resolvedTargetText, value, nameof(ResolvedTargetText));
        }

        public void UpdateResolvedTargetText(InstrumentCatalogResolver resolver)
        {
            ArgumentNullException.ThrowIfNull(resolver);
            if (!HasTarget)
            {
                ResolvedTargetText = "All original presets; no single target address.";
                return;
            }
            if (!TryMidiValue(BankMsbText, out byte bankMsb)
                || !TryMidiValue(BankLsbText, out byte bankLsb)
                || !TryMidiValue(ProgramText, out byte program))
            {
                ResolvedTargetText = "Enter a valid target to resolve its catalog name.";
                return;
            }
            ResolvedTargetText = resolver.ResolveProgram(
                new InstrumentAddress(bankMsb, bankLsb, program)).DisplayText;
        }

        public ApplicationSoundFontPreference ToPreference()
        {
            SoundFontTarget? target = null;
            if (HasTarget)
            {
                target = new(
                    ParseMidiValue(BankMsbText, "Bank MSB"),
                    ParseMidiValue(BankLsbText, "Bank LSB"),
                    ParseMidiValue(ProgramText, "Program"));
            }
            return new ApplicationSoundFontPreference(EntryId, Path, Enabled, target).Normalize();
        }

        private byte ParseMidiValue(string text, string name)
        {
            if (byte.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out byte result)
                && result <= 127)
            {
                return result;
            }
            throw new ArgumentOutOfRangeException(
                name,
                $"{FileName}: target {name} must be an integer from 0 through 127.");
        }

        private static bool TryMidiValue(string text, out byte result) =>
            byte.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out result)
            && result <= 127;

        private void SetText(ref string field, string value, string propertyName)
        {
            value ??= string.Empty;
            if (field == value)
            {
                return;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
