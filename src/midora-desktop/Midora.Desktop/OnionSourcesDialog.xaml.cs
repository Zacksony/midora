using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Domain;

namespace Midora.Desktop;

public sealed class OnionSourceChoice(MidoraId id, string name, uint color, bool included) : ObservableObject
{
    private bool _included = included;
    public MidoraId Id { get; } = id;
    public string Name { get; } = name;
    internal uint ColorValue { get; } = color;
    public Brush Color { get; } = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
    public bool Included { get => _included; set => Set(ref _included, value); }
}

public partial class OnionSourcesDialog : Window
{
    public OnionSourcesDialog(IEnumerable<OnionSourceChoice> sources, string title)
    {
        // Own the draft: neither cancellation nor checkbox edits mutate a preset.
        Sources = sources.Select(s => new OnionSourceChoice(s.Id, s.Name, s.ColorValue, s.Included)).ToArray();
        InitializeComponent(); Title = title; DataContext = this;
    }
    public IReadOnlyList<OnionSourceChoice> Sources { get; }
    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var source in Sources) source.Included = true; }
    private void OnClear(object sender, RoutedEventArgs e) { foreach (var source in Sources) source.Included = false; }
    private void OnHelp(object sender, RoutedEventArgs e) => OnionSettingsDialog.ShowHelp(this);
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
