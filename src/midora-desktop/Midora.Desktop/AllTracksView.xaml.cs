using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop;

public partial class AllTracksView : UserControl
{
    private DispatcherOperation? _focusOperation;
    public AllTracksView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnVisibilityChanged;
        DataContextChanged += OnDataContextChanged;
    }
    private void CancelPendingFocus()
    { _focusOperation?.Abort(); _focusOperation = null; }
    private void OnLoaded(object sender, RoutedEventArgs e) => QueueTimelineFocus();
    private void OnUnloaded(object sender, RoutedEventArgs e) => CancelPendingFocus();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) QueueTimelineFocus();
        else CancelPendingFocus();
    }
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueTimelineFocus();
    public event EventHandler<TimelineRulerEventArgs>? PlaybackCursorRequested;

    internal bool TryNavigate(Point point, MouseButton button)
    {
        if (button != MouseButton.Left || DataContext is not AllTracksWorkspaceViewModel { IsDisposed: false, IsPresentationSuspended: false }
            || !Timeline.TryGetNavigationTick(point, out long tick)) return false;
        PlaybackCursorRequested?.Invoke(this, new(tick));
        return true;
    }

    private void OnReadOnlyMouseDown(object sender, MouseButtonEventArgs e)
    {
        OnFollowMouseDown(sender, e);
        if (e.ChangedButton == MouseButton.Middle) return; // Keep normal viewport panning.
        Timeline.Focus();
        TryNavigate(e.GetPosition(Timeline), e.ChangedButton);
        e.Handled = true; // No marquee, note editing, scrub or delayed editing menu.
    }
    private void OnFollowMouseDown(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowViewportPreviewMouseDown(sender, e);
    private void OnFollowMouseUp(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowViewportPreviewMouseUp(sender, e);
    private void OnFollowLostMouseCapture(object sender, MouseEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowViewportLostMouseCapture(sender, e);
    private void OnFollowOverviewMouseWheel(object sender, MouseWheelEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OnFollowOverviewPreviewMouseWheel(sender, e);
    private void OnZoomOut(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.TickSpan = Math.Min(long.MaxValue / 2, vm.TickSpan) * 2; }
    private void OnZoomIn(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.TickSpan /= 2; }
    private void OnFit(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) { vm.StartTick = 0; vm.TickSpan = vm.ExtentEndTick; vm.FirstLane = 0; vm.LaneHeight = Math.Max(3, Timeline.ActualHeight / 128); } }
    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
        => QueueTimelineFocus();
    private void OnModeDropDownClosed(object sender, EventArgs e)
        => QueueTimelineFocus();

    private void QueueTimelineFocus()
    {
        CancelPendingFocus();
        // Initial Mode binding runs before Loaded. Do not lose the focus handoff
        // when that notification is skipped: Loaded/visibility activation also
        // requests it, after TabControl's automatic toolbar focus has settled.
        if (!IsLoaded || !IsVisible
            || DataContext is not AllTracksWorkspaceViewModel workspace) return;
        var owner = new WeakReference<AllTracksView>(this);
        var target = new WeakReference<AllTracksWorkspaceViewModel>(workspace);
        _focusOperation = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!owner.TryGetTarget(out var view)) return;
            view._focusOperation = null;
            if (view.IsLoaded && view.IsVisible && view.IsEnabled && !view.ModeSelector.IsDropDownOpen
                && target.TryGetTarget(out var active)
                && !active.IsDisposed && !active.IsPresentationSuspended
                && ReferenceEquals(view.DataContext, active))
                view.Timeline.Focus();
        }));
    }
    private void OnRefresh(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.RetryBuild(); }
    private void OnCancelBuild(object sender, RoutedEventArgs e)
    { if (DataContext is AllTracksWorkspaceViewModel vm) vm.CancelBuild(); }
    private void OnHelp(object sender, RoutedEventArgs e) => OnionSettingsDialog.ShowHelp(Window.GetWindow(this));
}
