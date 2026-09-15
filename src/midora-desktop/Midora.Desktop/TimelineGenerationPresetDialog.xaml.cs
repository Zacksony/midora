using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class TimelineGenerationPresetDialog : Window
{
    private readonly TimelineGenerationPresetStore _store;
    private bool _initialized;

    public TimelineGenerationPresetDialog(TimelineGenerationPresetStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        Title = TitleText.Text = store.Notes ? "Note Generation Presets" : "Event Generation Presets";
        _initialized = true;
        DataContext = this;
        Reload();
    }

    public ObservableCollection<TimelineGenerationPresetInfo> Presets { get; } = [];
    public TimelineGenerationPreset? SelectedPreset { get; private set; }

    private void Reload()
    {
        Presets.Clear();
        foreach (TimelineGenerationPresetInfo preset in _store.Load()) Presets.Add(preset);
        PresetList.SelectedItem = null;
        UpdateSelectionState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized) UpdateSelectionState();
    }
    private void UpdateSelectionState()
    {
        bool hasSelection = PresetList.SelectedItem is TimelineGenerationPresetInfo;
        DeleteButton.IsEnabled = ApplyButton.IsEnabled = hasSelection;
        PreviewText.Text = PresetList.SelectedItem is TimelineGenerationPresetInfo selected
            ? selected.Preview : Presets.Count == 0 ? "No presets are stored for this generation tool." : "Select a preset to preview it.";
    }
    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not TimelineGenerationPresetInfo selected) return;
        SelectedPreset = TimelineGenerationPresetStore.NormalizeAndValidate(selected.Preset, _store.Notes);
        DialogResult = true;
    }
    private void OnPresetListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(PresetList, source) is not ListBoxItem) return;
        OnApplyClick(sender, e);
    }
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not TimelineGenerationPresetInfo selected) return;
        if (MessageDialog.Show(this, $"Delete preset '{selected.Name}'?", "Delete Generation Preset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _store.Delete(selected); Reload(); }
        catch (Exception exception) when (TimelineGenerationPresetStore.IsPresetFailure(exception))
        {
            _ = MessageDialog.Show(this, exception.Message, "Delete Generation Preset", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void OnPresetListMouseWheel(object sender, MouseWheelEventArgs e) => ListBoxWheelScroll.ScrollOneItemPerNotch(PresetList, e);
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnDialogPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        OnCancelClick(sender, e);
    }
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
