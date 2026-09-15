using System.Windows;
using System.Windows.Input;

namespace Midora.Desktop;

public partial class NoteSplitHelpDialog : Window
{
    public NoteSplitHelpDialog() => InitializeComponent();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnTitleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
