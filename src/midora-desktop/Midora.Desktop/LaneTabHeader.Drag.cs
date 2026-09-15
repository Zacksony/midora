using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Midora.Desktop;

public partial class LaneTabHeader
{
    private const string LaneTabDragFormat = "Midora.LaneTab";
    private sealed record LaneTabDragData(LaneTabHeader Header, LaneTabSession Session, LaneTabKey Key);
    private Point? _dragOrigin;
    private LaneTabKey? _dragKey;
    private LaneTabSession? _dragSession;
    private DispatcherTimer? _dragScrollTimer;
    private int _dragScrollDirection;

    private bool CanReorderTabs => Host?.DataContext is DesktopSessionController { IsMainWindowTaskLocked: false };

    private void OnTabDown(object sender, MouseButtonEventArgs e)
    {
        ResetTabDrag();
        if (!CanReorderTabs || _state is null) return;
        var source = e.OriginalSource as DependencyObject;
        // Hide buttons act only as buttons, like the main workspace close button.
        for (var element = source; element is Visual && element != Tabs; element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return;
        if (ItemsControl.ContainerFromElement(Tabs, source) is not ListBoxItem { DataContext: LaneTabDescriptor item }) return;
        if (item.Key == _state.Active) Present(true);
        if (item.Key.Type < 2) return;
        _dragOrigin = e.GetPosition(Tabs);
        _dragKey = item.Key;
        _dragSession = _state;
    }

    private void OnTabMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { ResetTabDrag(); return; }
        if (_dragOrigin is not { } origin || _dragKey is not { } key || _state is null
            || !ReferenceEquals(_state, _dragSession) || !CanReorderTabs
            || !MainWindow.HasReachedUiReorderDragThreshold(origin, e.GetPosition(Tabs))) return;
        _dragOrigin = null;
        try
        {
            // The full header is the target, as for workspace tabs. A payload
            // carries its owner/session too: a lane cannot be dragged to another editor.
            DragDrop.DoDragDrop(Tabs, new DataObject(LaneTabDragFormat, new LaneTabDragData(this, _state, key)), DragDropEffects.Move);
        }
        finally { ResetTabDrag(); }
    }

    private LaneTabDragData? ReadTabDrag(IDataObject data) => data.GetDataPresent(LaneTabDragFormat)
        && data.GetData(LaneTabDragFormat) is LaneTabDragData drag
        && ReferenceEquals(drag.Header, this) && ReferenceEquals(drag.Session, _state)
        && drag.Key.Type >= 2 && _state.IsVisible(drag.Key)
        && _state.Descriptors.Any(item => item.Key == drag.Key) && CanReorderTabs ? drag : null;

    private ListBoxItem? FindDropHeader(DependencyObject? source, Point point)
    {
        if (!new Rect(Tabs.RenderSize).Contains(point)) return null;
        if (ItemsControl.ContainerFromElement(Tabs, source) is ListBoxItem direct) return direct;
        if (ItemsControl.ContainerFromElement(Tabs, Tabs.InputHitTest(point) as DependencyObject) is ListBoxItem hit) return hit;
        // Include gaps and the trailing strip. Inspect only realized containers,
        // never all possible MIDI targets or events.
        if (Find<VirtualizingStackPanel>(Tabs) is not { } panel) return null;
        ListBoxItem? nearest = null;
        double distance = double.PositiveInfinity;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(panel); i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is not ListBoxItem item) continue;
            double left = item.TranslatePoint(default, Tabs).X, right = left + item.ActualWidth;
            if (right <= 0 || left >= Tabs.ActualWidth) continue;
            double d = Math.Max(0, Math.Max(left - point.X, point.X - right));
            if (d < distance) { nearest = item; distance = d; }
        }
        return nearest;
    }

    private bool TryResolveTabDrop(DragEventArgs e, out LaneTabDragData? drag, out ListBoxItem? target)
    {
        drag = ReadTabDrag(e.Data);
        target = drag is null ? null : FindDropHeader(e.OriginalSource as DependencyObject, e.GetPosition(Tabs));
        return target?.DataContext is LaneTabDescriptor { Key.Type: >= 2 } descriptor && descriptor.Key != drag!.Key;
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryResolveTabDrop(e, out var drag, out _) ? DragDropEffects.Move : DragDropEffects.None;
        // Essential: do not let the outer WorkspaceTabs reinterpret a Lane payload
        // and replace Move with None. DragEnter and DragOver follow the same path.
        e.Handled = true;
        double x = e.GetPosition(Tabs).X;
        SetDragScroll(drag is null ? 0 : x < 18 ? -1 : x > Tabs.ActualWidth - 18 ? 1 : 0);
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        SetDragScroll(0);
        if (!TryResolveTabDrop(e, out var drag, out var container)
            || container?.DataContext is not LaneTabDescriptor target) return;
        _state!.Move(drag!.Key, target.Key, e.GetPosition(container).X >= container.ActualWidth / 2);
        RenderHeader();
        e.Effects = DragDropEffects.Move;
        Present(true);
    }

    private void OnTabDragLeave(object sender, DragEventArgs e) { SetDragScroll(0); e.Handled = true; }

    private void SetDragScroll(int direction)
    {
        _dragScrollDirection = direction;
        if (direction == 0) { _dragScrollTimer?.Stop(); return; }
        if (_dragScrollTimer is null)
        {
            _dragScrollTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
            _dragScrollTimer.Tick += (_, _) =>
            {
                if (!IsLoaded || !CanReorderTabs) { SetDragScroll(0); return; }
                if (Find<ScrollViewer>(Tabs) is { } viewer)
                { if (_dragScrollDirection < 0) viewer.LineLeft(); else viewer.LineRight(); }
            };
        }
        _dragScrollTimer.Start();
    }

    private void ResetTabDrag()
    { _dragOrigin = null; _dragKey = null; _dragSession = null; SetDragScroll(0); }
}
