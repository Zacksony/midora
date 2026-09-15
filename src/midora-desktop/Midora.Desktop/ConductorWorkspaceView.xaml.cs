using System.Windows;
using System.Windows.Controls;

namespace Midora.Desktop;

public partial class ConductorWorkspaceView : UserControl
{
    public ConductorWorkspaceView() => InitializeComponent();
    public event EventHandler<string>? CommandRequested;
    internal bool HandlersAttached { get; set; }
    internal void ResetTempoView()
    {
        if (DataContext is not TimelineWorkspaceViewModel workspace) return;
        workspace.SetTempoAxis(0, 240);
        TempoTimeline.ResetValueViewport();
    }
    private void OnPropertiesClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Properties");
    private void OnLocateClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Locate");
    private void OnCopyClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Copy");
    private void OnCutClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Cut");
    private void OnPasteClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Paste");
    private void OnDeleteClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Delete");
    private void OnFitClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Fit");
    private void OnResetTempoViewClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "ResetAxis");
    private void OnAxisClick(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "Axis");
}
