using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Midora.Domain;
using Midora.Application;

namespace Midora.Desktop;

public partial class MidiStateEntryDialog : Window
{
    private static readonly MidiValueKind[] SupportedKinds =
    [
        MidiValueKind.ControlChange,
        MidiValueKind.RegisteredParameter,
        MidiValueKind.NonRegisteredParameter
    ];

    public MidiStateEntryDialog()
    {
        InitializeComponent();
        KindBox.ItemsSource = SupportedKinds;
        ControllerBox.ItemsSource = MidiControlChangeCatalog.EditableControllers;
        ControllerBox.SelectionChanged += (_, _) => UpdateValueHint();
        ControllerBox.SelectedIndex = 0;
        KindBox.SelectedIndex = 0;
    }

    public MidiValueTarget? Target { get; private set; }
    public int Value { get; private set; }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (KindBox.SelectedItem is not MidiValueKind kind
            || !int.TryParse(ValueBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            ValidationText.Text = "Value must be a base-10 integer.";
            return;
        }
        int number;
        if (kind == MidiValueKind.ControlChange)
        {
            if (ControllerBox.SelectedItem is not MidiControlChangeInfo controller)
            {
                ValidationText.Text = "Select a supported Control Change.";
                return;
            }
            number = controller.Number;
        }
        else if (!int.TryParse(NumberBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
        {
            ValidationText.Text = "Parameter Number must be a base-10 integer.";
            return;
        }
        bool valid = kind switch
        {
            MidiValueKind.ControlChange => number is >= 0 and <= 119 and not 91 and not 93,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                number is >= 0 and <= 16_383,
            _ => false
        };
        if (!valid)
        {
            ValidationText.Text = kind == MidiValueKind.ControlChange
                ? "CC number must be 0–119 except 91/93."
                : "RPN/NRPN Number must be 0–16,383.";
            return;
        }
        Target = new MidiValueTarget(kind, number);
        Value = MidiEditingValueDomain.ClampInitialState(Target.Value, value);
        DialogResult = true;
    }

    private void OnKindChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        bool controller = KindBox.SelectedItem is MidiValueKind.ControlChange;
        ControllerBox.Visibility = controller ? Visibility.Visible : Visibility.Collapsed;
        NumberBox.Visibility = controller ? Visibility.Collapsed : Visibility.Visible;
        NumberLabel.Text = controller ? "CONTROLLER" : "PARAMETER NUMBER";
        UpdateValueHint();
    }

    private void UpdateValueHint()
    {
        if (ValueBox is null) return;
        ValueBox.ToolTip = KindBox.SelectedItem is MidiValueKind.ControlChange
            ? ControllerBox.SelectedItem is MidiControlChangeInfo cc && MidiEditingValueDomain.ControllerOffset(cc.Number) != 0
                ? "Value: -64 to 63; 0 is the center." : "Value: 0 to 127."
            : "Value: 0 to 16383.";
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }
}
