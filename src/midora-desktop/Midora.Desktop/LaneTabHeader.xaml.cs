using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Midora.Domain;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop;

public partial class LaneTabHeader : UserControl
{
    private EditorStateOwner? _memoryOwner;
    private WorkspaceViewModel? _workspace;
    private LaneTabSession? _state;
    private LaneTabKey _displayed;
    private bool _restoreOwnerAxis;
    private LaneTabKey? _restoreAxisKey;
    private long _presentationVersion;
    private bool _refreshPending, _syncing;
    private int _tabWheelRemainder, _directoryWheelRemainder;
    internal LaneTabHost? Owner { get; set; }
    internal MainWindow? CommandHost { get; set; }
    private MainWindow? Host => CommandHost ?? Window.GetWindow(this) as MainWindow;
    public LaneTabHeader() { InitializeComponent(); DataContextChanged += (_, _) => Attach(); }
    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
    private void OnUnloaded(object sender, RoutedEventArgs e)
    { SaveAxis(); ++_presentationVersion; ResetTabDrag(); DirectoryPopup.IsOpen = false; Detach(); _state = null; _memoryOwner = null; }
    private void Attach()
    {
        if (!ReferenceEquals(_workspace, DataContext)) { SaveAxis(); ResetTabDrag(); _state = null; _memoryOwner = null; }
        Detach();
        if (!IsLoaded || DataContext is not WorkspaceViewModel workspace) return;
        _workspace = workspace;
        workspace.PropertyChanged += OnChanged;
        workspace.LaneViewStateCaptureRequested += SaveAxis;
        if (workspace is TimelineWorkspaceViewModel timeline) timeline.ParameterLaneOptions.CollectionChanged += OnCollectionChanged;
        if (workspace is InstrumentWorkspaceViewModel voice) voice.RenderLanes.CollectionChanged += OnCollectionChanged;
        string settings = workspace is TimelineWorkspaceViewModel ? "LaneEditorSettings" : "EventLaneEditorSettings";
        Snap.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(settings + ".SnapEnabled") { Mode = BindingMode.TwoWay });
        Subdivision.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(settings + ".SubdivisionPresets"));
        Subdivision.SetBinding(ComboBox.TextProperty, new Binding(settings + ".OperationSubdivisionText") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        QueueRefresh();
    }
    private void Detach()
    {
        if (_workspace is not null) _workspace.PropertyChanged -= OnChanged;
        if (_workspace is not null) _workspace.LaneViewStateCaptureRequested -= SaveAxis;
        if (_workspace is TimelineWorkspaceViewModel timeline) timeline.ParameterLaneOptions.CollectionChanged -= OnCollectionChanged;
        if (_workspace is InstrumentWorkspaceViewModel voice) voice.RenderLanes.CollectionChanged -= OnCollectionChanged;
        _workspace = null;
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Snapshot" or "ParameterSnapshot" or "SubVoiceSnapshot" or "SubVoiceEventSnapshot"
            or "ActiveSubVoiceId" or "LaneTargetCounts") QueueRefresh();
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_refreshPending || !IsLoaded) return;
        _refreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        { _refreshPending = false; if (IsLoaded) Refresh(); }));
    }
    internal void Refresh(bool present = true)
    {
        if (_workspace is null || Host?.GetLaneTabDescriptors(_workspace) is not { } description) return;
        LaneTabSession state;
        if (_workspace.EditorState is { LocalOwner: { } owner } binding && owner.Id == description.Owner)
        {
            if (_state is not null && _memoryOwner == owner) state = _state;
            else state = new(binding.GetLaneMemory(), memory => binding.SaveLaneMemory(owner, memory));
            _memoryOwner = owner;
        }
        else
        {
            var sessions = _workspace.LocalLaneSessions;
            if (!sessions.TryGetValue(description.Owner, out state!)) sessions.Add(description.Owner, state = new());
        }
        if (!ReferenceEquals(_state, state)) { SaveAxis(); ResetTabDrag(); _state = state; _displayed = state.Active; _restoreOwnerAxis = true; }
        state.Sync(description.Descriptors, _workspace is not TimelineWorkspaceViewModel t || t.IsLaneDirectoryComplete);
        RenderHeader();
        if (present) Present(false);
        if (DirectoryPopup.IsOpen) RefreshDirectory();
    }
    private void RenderHeader()
    {
        if (_state is null) return;
        _syncing = true;
        try
        {
            var visible = _state.Visible().ToArray();
            // A pointer-down selection must not replace the very containers
            // which are about to initiate a drag (or reset their scroll offset).
            if (!Tabs.Items.Cast<LaneTabDescriptor>().SequenceEqual(visible)) Tabs.ItemsSource = visible;
            Tabs.SelectedItem = Tabs.Items.Cast<LaneTabDescriptor>().FirstOrDefault(value => value.Key == _state.EffectiveActive);
        }
        finally { _syncing = false; }
    }
    private TimelineSurface? ActiveSurface => Owner is null ? null : Find<TimelineSurface>(Owner,
        surface => surface.IsVisible && surface.IsHitTestVisible);
    private void SaveAxis()
    { if (_restoreAxisKey is null && !_restoreOwnerAxis && _state is not null && ActiveSurface is { } surface) _state.SaveAxis(_displayed, surface.CaptureValueViewport()); }
    private void Present(bool focus)
    {
        if (_state is null || Owner is null || _workspace is null) return;
        var key = _state.EffectiveActive;
        Shapes.Visibility = key == LaneTabKey.Instrument ? Visibility.Collapsed : Visibility.Visible;
        int index = key.Type == 0 ? 0 : key.Type == 1 ? 1 : 2;
        bool restore = _restoreOwnerAxis || _restoreAxisKey == key || _displayed != key || Owner.SelectedIndex != index;
        _restoreOwnerAxis = false;
        if (_displayed != key) SaveAxis();
        if (restore) _restoreAxisKey = key;
        _displayed = key;
        if (Owner.SelectedIndex != index) Owner.SetCurrentValue(System.Windows.Controls.Primitives.Selector.SelectedIndexProperty, index);
        if (key.Type >= 2) Host?.ActivateLaneTarget(_workspace, key, focus);
        var state = _state; long version = ++_presentationVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || !ReferenceEquals(_state, state) || _state?.EffectiveActive != key || version != _presentationVersion) return;
            var surface = ActiveSurface;
            if (surface is not null)
            {
                Host?.BindEventLaneLines(surface);
                if (restore) surface.RestoreValueViewport(_state.Axis(key));
                _restoreAxisKey = null;
                Coordinates.SetBinding(TextBlock.TextProperty, new Binding(nameof(TimelineSurface.PointerPositionText)) { Source = surface });
                if (focus) surface.Focus();
            }
            else
            {
                _restoreAxisKey = null;
                var lane = Find<InstrumentChangeLane>(Owner, static lane => lane.IsVisible);
                if (lane is not null) Coordinates.SetBinding(TextBlock.TextProperty, new Binding(nameof(InstrumentChangeLane.PointerPositionText)) { Source = lane });
                else { BindingOperations.ClearBinding(Coordinates, TextBlock.TextProperty); Coordinates.Text = ""; }
                if (focus) lane?.Focus();
            }
        }));
    }
    private void Activate(LaneTabKey key)
    {
        if (_state is null) return;
        SaveAxis(); _state.Show(key); RenderHeader(); Present(true);
    }
    internal void ShowCurrentEventTarget()
    {
        LaneTabKey? key = _workspace switch
        {
            TimelineWorkspaceViewModel t when t.GetActiveParameterLaneOption() is { } option => LaneTabKey.From(option),
            InstrumentWorkspaceViewModel v when v.GetRenderLane(v.ActiveRenderLaneIndex)?.Target is { } target => LaneTabKey.From(target),
            _ => null
        };
        Refresh(present: false);
        if (key is { } value) Activate(value);
    }
    internal void ShowInstrumentTarget() { Refresh(present: false); Activate(LaneTabKey.Instrument); }
    private void OnTabSelected(object sender, SelectionChangedEventArgs e)
    { if (!_syncing && Tabs.SelectedItem is LaneTabDescriptor item) Activate(item.Key); }
    private void OnCloseTab(object sender, RoutedEventArgs e)
    { e.Handled = true; if (sender is FrameworkElement { Tag: LaneTabDescriptor item } && _state is not null) { SaveAxis(); _state.Hide(item.Key); RenderHeader(); Present(true); } }
    private void OnTabWheel(object sender, MouseWheelEventArgs e)
    {
        int steps = ConsumeSteps(ref _tabWheelRemainder, e.Delta);
        if (Find<ScrollViewer>(Tabs) is { } viewer)
            for (int i = 0; i < Math.Abs(steps); i++) { if (steps < 0) viewer.LineRight(); else viewer.LineLeft(); }
        e.Handled = true;
    }
    private void OnDirectory(object sender, RoutedEventArgs e)
    { RefreshDirectory(); DirectoryPopup.IsOpen = true; Search.Focus(); }
    private sealed record DirectoryRow(LaneTabDescriptor Descriptor, string Status)
    { public string Name => Descriptor.Name; public string Description => Descriptor.Description; }
    private void RefreshDirectory()
    {
        if (_state is null || DirectoryList is null || Search is null) return;
        DirectoryList.ItemsSource = _state.Descriptors.Where(value => value.Name.Contains(Search.Text, StringComparison.OrdinalIgnoreCase))
            .Select(value => new DirectoryRow(value, _state.IsVisible(value.Key) ? "Visible" : "Hidden")).ToArray();
    }
    private void OnSearch(object sender, TextChangedEventArgs e) => RefreshDirectory();
    private void ChooseDirectory()
    { if (DirectoryList.SelectedItem is DirectoryRow row) { DirectoryPopup.IsOpen = false; Activate(row.Descriptor.Key); } }
    private void OnDirectoryClick(object sender, MouseButtonEventArgs e)
    { if (ItemsControl.ContainerFromElement(DirectoryList, e.OriginalSource as DependencyObject) is ListBoxItem) ChooseDirectory(); }
    private void OnDirectoryKey(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { ChooseDirectory(); e.Handled = true; } }
    private void OnDirectoryWheel(object sender, MouseWheelEventArgs e)
    {
        int steps = ConsumeSteps(ref _directoryWheelRemainder, e.Delta);
        if (Find<ScrollViewer>(DirectoryList) is { } viewer) viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset - steps, 0, viewer.ScrollableHeight));
        e.Handled = true;
    }
    private static int ConsumeSteps(ref int remainder, int delta)
    { int total = remainder + delta; remainder = total % 120; return total / 120; }
    private void OnDirectoryClosed(object sender, EventArgs e)
    { if (IsLoaded) Present(true); }
    private void OnSnapClick(object sender, RoutedEventArgs e) => Present(true);
    private void OnShapeChosen(object? sender, EventArgs e) => Present(true);
    private void OnAddLane(object sender, RoutedEventArgs e) => Host?.AddLaneFromHeader(this);
    private void OnSubdivisionLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Host?.CommitLaneSubdivision(sender, e);
    internal static T? Find<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T found && (predicate is null || predicate(found))) return found;
        if (root is null) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), predicate) is T child) return child;
        return null;
    }
}
