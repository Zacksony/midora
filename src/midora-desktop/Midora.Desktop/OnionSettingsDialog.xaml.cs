using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class OnionSettingsDialog : Window, System.ComponentModel.INotifyPropertyChanged
{
    private double _opacityPercent;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public OnionSettingsDialog(double opacity)
    {
        OpacityPercent = opacity * 100;
        InitializeComponent(); DataContext = this;
    }
    public double OpacityPercent
    {
        get => _opacityPercent;
        set { if (!double.IsFinite(value)) return; _opacityPercent = Math.Clamp(value, 0, 100); PropertyChanged?.Invoke(this, new(nameof(OpacityPercent))); }
    }
    private void OnHelp(object sender, RoutedEventArgs e) => ShowHelp(this);
    internal static void ShowHelp(Window? owner) => MessageDialog.Show(owner,
        "ONION SKIN\nThe layer icon beside Zoom opens a menu. Enable/Disable keeps your choices. Show Previous/Next displays only that neighbor, without changing your saved custom sources. Select Tracks/SubVoices reopens those custom checks; OK switches back to custom sources and enables onion skin. Cancel changes nothing. Settings controls opacity only. The current track/SubVoice is excluded. All Segments on the same target track share this setting.\n\n"
        + "TIME AND LAYERS\nTrack notes line up at their absolute Arrangement positions; hidden source Segment content is clipped. Sources follow the current track/SubVoice order, with later sources on top. Your editable notes and selection remain above the ghosts.\n\n"
        + "ALL TRACKS\nOpen the layer icon beside Arrangement Zoom. Raw shows source notes. Compiled expands only Logical Tracks from the last successful compilation (mapped pitches, loops and emitted gates); MIDI Tracks always show current source notes, not FIFO-paired lengths from the final MIDI stream. Logical stale means that expansion is from an older successful result. Preparation is asynchronous and cancelable; Refresh retries the logical index. MIDI notes remain visible even without a successful compilation. No notes or lanes can be edited here.\n\n"
        + "NAVIGATION\nMiddle-drag to pan. Wheel scrolls keys; Shift+wheel scrolls time. Ctrl+wheel zooms time over the piano roll, or key height over the keyboard ruler. Left-click the All Tracks time ruler or note area to set the playback cursor (seek while playing). This does not edit notes or select a time range. All Tracks obeys Follow Playback: middle-dragging suspends following until release.\n\n"
        + "SAVING\nOpacity, enable state, custom sources and the current Custom/Previous/Next mode are saved together. Neighbor modes follow the current formal order without wrapping. These settings do not create Undo entries or a Project modified indicator. Use Save or Save Copy explicitly to retain them. Closing without saving may lose view changes without a prompt. Deleting a custom source hides its ghosts; Undo restores them. Duplicate copies the target's settings.",
        "Onion Skin and All Tracks", MessageBoxButton.OK, MessageBoxImage.Information);
    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true; DialogResult = e.Key == Key.Enter;
    }
    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
}
