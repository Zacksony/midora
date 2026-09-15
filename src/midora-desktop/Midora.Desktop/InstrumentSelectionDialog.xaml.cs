using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Application;
using Midora.Compiler;
using Midora.Domain;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop;

public partial class InstrumentSelectionDialog : Window
{
    private readonly InstrumentCatalogResolver _catalog;
    private readonly InstrumentAddress _inherited;
    private readonly bool _inheritance;
    private readonly Func<InstrumentSelectionValues, Task<string?>> _submit;
    private readonly InstrumentPresetAuditionSession? _audition;
    private bool _updating = true;
    private bool _finishing;
    private bool _mayClose;
    private InstrumentBankAddress? _displayedBank;
    internal long? TimelineTick { get; private set; }
    internal void SetTimelineTick(long tick)
    { TimelineTick = tick; TimelineTickBox.Text = tick.ToString(CultureInfo.InvariantCulture); TimelineTickRow.Visibility = Visibility.Visible; }
    // Bank names are resolved only when a virtualized row asks for its label.
    private sealed record BankRow(InstrumentBankAddress Address, InstrumentCatalogResolver Catalog)
    {
        public string Label => $"{Address.BankMsb}.{Address.BankLsb} — {Catalog.ResolveBank(Address).DisplayText}";
    }
    private sealed record ProgramRow(InstrumentAddress Address, string Label);

    internal InstrumentSelectionDialog(InstrumentSelectionValues values, InstrumentAddress inherited,
        bool allowInheritance, InstrumentCatalogResolver catalog, InstrumentAuditionPreferences preferences,
        MidiChannelMode mode, DesktopSessionController? session,
        Func<InstrumentSelectionValues, Task<string?>> submit)
    {
        values.Validate(); inherited.Validate(); preferences.Validate();
        _catalog = catalog; _inherited = inherited; _inheritance = allowInheritance; _submit = submit;
        AuditionPreferences = preferences;
        InitializeComponent();
        if (session is not null) _audition = new(session, Dispatcher, message => SetStatus(message ?? ""));
        foreach (var box in new[] { MsbOverride, LsbOverride, ProgramOverride })
            box.Visibility = allowInheritance ? Visibility.Visible : Visibility.Collapsed;
        MsbOverride.IsChecked = !allowInheritance || values.BankMsb.HasValue;
        LsbOverride.IsChecked = !allowInheritance || values.BankLsb.HasValue;
        ProgramOverride.IsChecked = !allowInheritance || values.Program.HasValue;
        var effective = values.Resolve(inherited);
        SetBoxes(effective);
        AutoBox.IsChecked = preferences.Automatic;
        KeyBox.Text = preferences.Key.ToString(CultureInfo.InvariantCulture);
        VelocityBox.Text = preferences.Velocity.ToString(CultureInfo.InvariantCulture);
        DurationBox.Text = preferences.DurationMilliseconds.ToString(CultureInfo.InvariantCulture);
        ModeBox.ItemsSource = Enum.GetValues<MidiChannelMode>(); ModeBox.SelectedItem = mode;
        var known = catalog.GetKnownBanks().Append(new(effective.BankMsb, effective.BankLsb))
            .Distinct().OrderBy(bank => bank.BankMsb).ThenBy(bank => bank.BankLsb);
        BanksList.ItemsSource = known.Select(bank => new BankRow(bank, catalog)).ToArray();
        BanksList.PreviewMouseWheel += (_, e) => ListBoxWheelScroll.ScrollOneItemPerNotch(BanksList, e);
        ProgramsList.PreviewMouseWheel += (_, e) => ListBoxWheelScroll.ScrollOneItemPerNotch(ProgramsList, e);
        UpdateOverrides(); SyncLists(effective); _updating = false;
        Loaded += (_, _) => { MsbBox.Focus(); KeyboardScroll.ScrollToHorizontalOffset(1000); };
    }

    public InstrumentAuditionPreferences AuditionPreferences { get; private set; }
    private static bool TryNumber(TextBox box, int min, int max, out int value) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    private bool TryValues(out InstrumentSelectionValues values)
    {
        values = default;
        if (!TryNumber(MsbBox, 0, 127, out int msb) || !TryNumber(LsbBox, 0, 127, out int lsb)
            || !TryNumber(ProgramBox, 0, 127, out int program)) return false;
        values = new(_inheritance && MsbOverride.IsChecked != true ? null : msb,
            _inheritance && LsbOverride.IsChecked != true ? null : lsb,
            _inheritance && ProgramOverride.IsChecked != true ? null : program);
        return true;
    }
    private void SetBoxes(InstrumentAddress address)
    {
        MsbBox.Text = address.BankMsb.ToString(CultureInfo.InvariantCulture);
        LsbBox.Text = address.BankLsb.ToString(CultureInfo.InvariantCulture);
        ProgramBox.Text = address.Program.ToString(CultureInfo.InvariantCulture);
    }
    private void UpdateOverrides()
    {
        MsbBox.IsEnabled = !_inheritance || MsbOverride.IsChecked == true;
        LsbBox.IsEnabled = !_inheritance || LsbOverride.IsChecked == true;
        ProgramBox.IsEnabled = !_inheritance || ProgramOverride.IsChecked == true;
        if (!MsbBox.IsEnabled) MsbBox.Text = _inherited.BankMsb.ToString(CultureInfo.InvariantCulture);
        if (!LsbBox.IsEnabled) LsbBox.Text = _inherited.BankLsb.ToString(CultureInfo.InvariantCulture);
        if (!ProgramBox.IsEnabled) ProgramBox.Text = _inherited.Program.ToString(CultureInfo.InvariantCulture);
    }
    private void SyncLists(InstrumentAddress address)
    {
        bool wasUpdating = _updating; _updating = true;
        try
        {
            var bank = new InstrumentBankAddress(address.BankMsb, address.BankLsb);
            if (_displayedBank != bank)
            {
                ProgramsList.ItemsSource = Enumerable.Range(0, 128).Select(program =>
                {
                    var key = new InstrumentAddress(bank.BankMsb, bank.BankLsb, (byte)program);
                    return new ProgramRow(key, $"{program} — {_catalog.ResolveProgram(key).DisplayText}");
                }).ToArray();
                _displayedBank = bank;
            }
            BanksList.SelectedItem = BanksList.Items.OfType<BankRow>().FirstOrDefault(value => value.Address == bank);
            ProgramsList.SelectedIndex = address.Program;
            if (ProgramsList.SelectedItem is { } selected) ProgramsList.ScrollIntoView(selected);
        }
        finally { _updating = wasUpdating; }
    }
    private void OnValueChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (TryValues(out var values)) { SyncLists(values.Resolve(_inherited)); PreviewAutomatic(); }
        else { _audition?.Stop(); SetStatus("Bank MSB, Bank LSB and Program must be integers from 0 to 127.", true); }
    }
    private void OnOverrideChanged(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updating = true; UpdateOverrides(); _updating = false;
        if (TryValues(out var values)) SyncLists(values.Resolve(_inherited));
        PreviewAutomatic();
    }
    private void OnBankSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || BanksList.SelectedItem is not BankRow bank) return;
        _updating = true;
        MsbOverride.IsChecked = LsbOverride.IsChecked = true;
        MsbBox.Text = bank.Address.BankMsb.ToString(CultureInfo.InvariantCulture);
        LsbBox.Text = bank.Address.BankLsb.ToString(CultureInfo.InvariantCulture);
        UpdateOverrides(); _updating = false;
        if (TryValues(out var values)) SyncLists(values.Resolve(_inherited));
        PreviewAutomatic();
    }
    private void OnProgramSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ProgramsList.SelectedItem is not ProgramRow program) return;
        _updating = true;
        MsbOverride.IsChecked = LsbOverride.IsChecked = ProgramOverride.IsChecked = true;
        SetBoxes(program.Address); UpdateOverrides(); _updating = false;
        PreviewAutomatic();
    }
    private bool ReadPreviewSettings()
    {
        if (!TryNumber(KeyBox, 0, 127, out int key) || !TryNumber(VelocityBox, 1, 127, out int velocity)
            || !TryNumber(DurationBox, 1, int.MaxValue, out int duration)) return false;
        AuditionPreferences = new(AutoBox.IsChecked == true, key, velocity, duration);
        return true;
    }
    private void OnPreviewSettingsChanged(object sender, EventArgs e)
    {
        if (_updating) return;
        PreviewAutomatic();
    }
    private void PreviewAutomatic()
    {
        _audition?.Stop();
        if (ReadPreviewSettings() && AuditionPreferences.Automatic) Preview(held: false);
    }
    private void Preview(bool held)
    {
        if (_finishing || !ReadPreviewSettings() || !TryValues(out var values)) return;
        var address = values.Resolve(_inherited);
        if (_audition is null) { SetStatus("Preview unavailable. Instrument editing remains available."); return; }
        SetStatus(held ? "Preview · release the key to end the note" : "Preview");
        _audition.Play(new(address.BankMsb, address.BankLsb, address.Program,
            AuditionPreferences.Key, AuditionPreferences.Velocity, AuditionPreferences.DurationMilliseconds,
            ModeBox.SelectedItem is MidiChannelMode mode ? mode : MidiChannelMode.Melodic), held);
    }
    private void OnPreviewKeyPressed(object? sender, PianoKeyEventArgs e)
    {
        _updating = true;
        KeyBox.Text = e.Note.ToString(CultureInfo.InvariantCulture);
        VelocityBox.Text = e.Velocity.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        Preview(held: true);
    }
    private void OnPreviewKeyReleased(object? sender, PianoKeyEventArgs e) => _audition?.Release();
    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.Red.Hover" : "Brush.Text.Tertiary");
    }
    private async void OnOkClick(object sender, RoutedEventArgs e) => await FinishAsync(true);
    private async void OnCancelClick(object sender, RoutedEventArgs e) => await FinishAsync(false);
    private async Task FinishAsync(bool accept)
    {
        if (_finishing) return;
        if (accept && !TryValues(out _)) { SetStatus("Bank and Program must be integers from 0 to 127.", true); return; }
        if (accept && TimelineTick.HasValue)
        {
            if (!long.TryParse(TimelineTickBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long tick) || tick == long.MaxValue)
            { SetStatus("Tick must be a non-negative integer below Int64.MaxValue.", true); return; }
            TimelineTick = tick;
        }
        _finishing = true; OkButton.IsEnabled = false;
        try
        {
            PreviewKeyboard.ReleaseMouseCapture();
            if (_audition is not null) await _audition.StopAsync();
            if (accept && TryValues(out var values))
            {
                string? error = await _submit(values);
                if (error is not null) { SetStatus(error, true); return; }
            }
            if (_audition is not null) await _audition.CloseAsync();
            ReadPreviewSettings(); _mayClose = true; DialogResult = accept;
        }
        catch (Exception exception) { SetStatus(exception.Message, true); }
        finally { _finishing = false; OkButton.IsEnabled = true; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_mayClose) return;
        e.Cancel = true; await FinishAsync(false);
    }
    private async void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (ModeBox.IsDropDownOpen) return;
        if (e.Key == Key.Escape) { e.Handled = true; await FinishAsync(false); }
        else if (e.Key == Key.Enter) { e.Handled = true; await FinishAsync(true); }
    }
    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    { if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove(); }
}
