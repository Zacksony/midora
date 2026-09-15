using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Midora.Application;

namespace Midora.Desktop;

public partial class JoinNotesDialog : Window
{
    public JoinNotesDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => MaximumGapBox.Focus();
    }

    public NoteJoinOptions? Options { get; private set; }

    public static NoteJoinOptions ParseOptions(string? maximumGapText)
    {
        if (!long.TryParse(
                maximumGapText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long maximumGap)
            || maximumGap < 0)
        {
            throw new ArgumentException("Maximum Gap must be a non-negative Int64 Tick value.");
        }
        return new(maximumGap);
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || ValidationText is null) return;
        ValidationText.Text = string.Empty;
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Options = ParseOptions(MaximumGapBox.Text);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
            _ = MessageDialog.Show(
                this,
                exception.Message,
                "Join Notes",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
