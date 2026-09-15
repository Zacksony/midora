using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class NoteSplitPresetDialog : Window
{
    private readonly NoteSplitPresetStore _store;

    public NoteSplitPresetDialog(NoteSplitPresetStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        DataContext = this;
        Reload();
    }

    public ObservableCollection<NoteSplitPresetInfo> Presets { get; } = [];
    public NoteSplitPreset? SelectedPreset { get; private set; }

    private void Reload()
    {
        Presets.Clear();
        foreach (NoteSplitPresetInfo preset in _store.Load()) Presets.Add(preset);
        PresetList.SelectedItem = null;
        UpdateSelectionState();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionState();
    }

    private void UpdateSelectionState()
    {
        bool hasSelection = PresetList.SelectedItem is NoteSplitPresetInfo;
        DeleteButton.IsEnabled = hasSelection;
        ApplyButton.IsEnabled = hasSelection;
        PreviewText.Text = PresetList.SelectedItem is NoteSplitPresetInfo selected
            ? selected.Preview
            : Presets.Count == 0
                ? "No Note Split presets are stored."
                : "Select a preset to preview it.";
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not NoteSplitPresetInfo selected) return;
        SelectedPreset = selected.Preset;
        DialogResult = true;
    }

    private void OnPresetListMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(PresetList, source) is not ListBoxItem)
        {
            return;
        }

        OnApplyClick(sender, e);
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not NoteSplitPresetInfo selected) return;
        if (MessageDialog.Show(
                this,
                $"Delete Note Split preset '{selected.Name}'?",
                "Delete Note Split Preset",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _store.Delete(selected);
            Reload();
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Delete Note Split Preset",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnPresetListMouseWheel(object sender, MouseWheelEventArgs e) =>
        ListBoxWheelScroll.ScrollOneItemPerNotch(PresetList, e);

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
