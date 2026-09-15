using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Domain;

namespace Midora.Desktop;

public partial class TrackPropertiesDialog : Window
{
    private readonly bool _supportsInheritedColor;
    private readonly MidoraColor? _originalExplicitColor;
    private readonly Func<string, MidoraColor?, string?>? _submit;
    private MidoraColor _selectedColor;
    private bool _colorEdited;

    internal TrackPropertiesDialog(
        string title,
        string name,
        MidoraColor? explicitColor,
        MidoraColor resolvedColor,
        bool supportsInheritedColor,
        Func<string, MidoraColor?, string?>? submit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(name);
        _supportsInheritedColor = supportsInheritedColor;
        _originalExplicitColor = explicitColor;
        _submit = submit;
        _selectedColor = explicitColor ?? resolvedColor;
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        NameTextBox.Text = name;
        UseInstrumentColorCheckBox.Visibility = supportsInheritedColor
            ? Visibility.Visible
            : Visibility.Collapsed;
        UseInstrumentColorCheckBox.IsChecked = supportsInheritedColor && explicitColor is null;
        UpdateColorPresentation();
    }

    internal string TrackName { get; private set; } = string.Empty;
    internal MidoraColor? ExplicitColor { get; private set; }

    private void OnUseInstrumentColorClick(object sender, RoutedEventArgs e) =>
        UpdateColorPresentation();

    private void OnSelectColorClick(object sender, RoutedEventArgs e)
    {
        ColorPickerDialog dialog = new(
            _selectedColor.Red,
            _selectedColor.Green,
            _selectedColor.Blue)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        _selectedColor = new(dialog.Red, dialog.Green, dialog.Blue);
        _colorEdited = true;
        UpdateColorPresentation();
    }

    private void UpdateColorPresentation()
    {
        bool inherits = _supportsInheritedColor
            && UseInstrumentColorCheckBox.IsChecked == true;
        SelectColorButton.IsEnabled = !inherits;
        ColorSwatch.Fill = new SolidColorBrush(Color.FromRgb(
            _selectedColor.Red,
            _selectedColor.Green,
            _selectedColor.Blue));
        ColorText.Text = $"#{_selectedColor.Red:X2}{_selectedColor.Green:X2}{_selectedColor.Blue:X2}";
        ColorHintText.Text = inherits
            ? "The displayed color follows the bound Event Instrument."
            : "Select the color used for this Track and its Segments.";
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        string name = NameTextBox.Text;
        bool inherits = _supportsInheritedColor
            && UseInstrumentColorCheckBox.IsChecked == true;
        MidoraColor? explicitColor = inherits
            ? null
            : !_supportsInheritedColor && !_colorEdited && _originalExplicitColor is null
                ? null
                : _selectedColor;
        string? submitError = _submit?.Invoke(name, explicitColor);
        if (submitError is not null)
        {
            ErrorTextBlock.Text = submitError;
            return;
        }
        TrackName = name;
        ExplicitColor = explicitColor;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
