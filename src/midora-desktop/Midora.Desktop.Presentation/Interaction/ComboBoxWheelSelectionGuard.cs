using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Midora.Desktop.Presentation.Interaction;

public static class ComboBoxWheelSelectionGuard
{
    static ComboBoxWheelSelectionGuard()
    {
        // The ScrollViewer consumes this bubbling event before its parent popup
        // Grid can see it. Intercept at the item, restricted to our opted-in popup.
        EventManager.RegisterClassHandler(typeof(ComboBoxItem), FrameworkElement.RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnDropDownRequestBringIntoView));
    }

    private static readonly DependencyProperty ExplicitNavigationProperty = DependencyProperty.RegisterAttached(
        "ExplicitNavigation", typeof(bool), typeof(ComboBoxWheelSelectionGuard), new PropertyMetadata(false));
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ComboBoxWheelSelectionGuard),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty UseSingleStepDropDownWheelProperty =
        DependencyProperty.RegisterAttached(
            "UseSingleStepDropDownWheel",
            typeof(bool),
            typeof(ComboBoxWheelSelectionGuard),
            new PropertyMetadata(false, OnUseSingleStepDropDownWheelChanged));

    private static readonly MouseWheelEventHandler PreviewMouseWheelHandler = OnPreviewMouseWheel;
    private static readonly MouseWheelEventHandler DropDownPreviewMouseWheelHandler =
        OnDropDownPreviewMouseWheel;

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }

    public static bool GetUseSingleStepDropDownWheel(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(UseSingleStepDropDownWheelProperty);
    }

    public static void SetUseSingleStepDropDownWheel(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(UseSingleStepDropDownWheelProperty, value);
    }

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ComboBox comboBox)
        {
            return;
        }

        comboBox.RemoveHandler(UIElement.PreviewMouseWheelEvent, PreviewMouseWheelHandler);
        comboBox.SelectionChanged -= OnExplicitSelectionChanged;
        if (args.NewValue is true)
        {
            comboBox.SelectionChanged += OnExplicitSelectionChanged;
            comboBox.AddHandler(
                UIElement.PreviewMouseWheelEvent,
                PreviewMouseWheelHandler,
                handledEventsToo: true);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (sender is ComboBox { IsDropDownOpen: false })
        {
            args.Handled = true;
        }
    }

    private static void OnUseSingleStepDropDownWheelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not UIElement element) return;
        element.RemoveHandler(
            UIElement.PreviewMouseWheelEvent,
            DropDownPreviewMouseWheelHandler);
        element.PreviewKeyDown -= OnDropDownPreviewKeyDown;
        element.PreviewMouseDown -= OnDropDownPreviewMouseDown;
        if (args.NewValue is true)
        {
            element.AddHandler(
                UIElement.PreviewMouseWheelEvent,
                DropDownPreviewMouseWheelHandler,
                handledEventsToo: true);
            element.PreviewKeyDown += OnDropDownPreviewKeyDown;
            element.PreviewMouseDown += OnDropDownPreviewMouseDown;
        }
    }

    private static void OnDropDownPreviewKeyDown(object sender, KeyEventArgs args)
        => AllowExplicitNavigation((UIElement)sender);

    private static void OnDropDownPreviewMouseDown(object sender, MouseButtonEventArgs args) =>
        AllowExplicitNavigation((UIElement)sender);

    private static void OnExplicitSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox { IsDropDownOpen: true } combo
            || combo.Template?.FindName("PART_Popup", combo) is not Popup { Child: { } child }
            || FindDescendantScrollViewer(child) is not { } viewer) return;
        if (FindDropDownOwner(viewer) is UIElement owner) AllowExplicitNavigation(owner);
    }

    private static void AllowExplicitNavigation(UIElement owner)
    {
        owner.SetValue(ExplicitNavigationProperty, true);
        _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => owner.ClearValue(ExplicitNavigationProperty)));
    }

    internal static bool SuppressHoverBringIntoView(bool isOpen, bool isMouseOverItem,
        bool buttonPressed, bool explicitNavigation) =>
        isOpen && isMouseOverItem && !buttonPressed && !explicitNavigation;

    private static DependencyObject? FindDropDownOwner(DependencyObject current)
    {
        DependencyObject? owner = current;
        while (owner is not null && !GetUseSingleStepDropDownWheel(owner))
            owner = VisualTreeHelper.GetParent(owner);
        return owner;
    }

    private static void OnDropDownRequestBringIntoView(object sender, RequestBringIntoViewEventArgs args)
    {
        // WPF focuses a hovered ComboBoxItem. At a partially visible edge that
        // focus requests BringIntoView, exposing the next item beneath a stationary
        // pointer and repeating. Suppress only this hover-driven focus scroll;
        // never freeze offsets or interfere with actual scroll/navigation commands.
        if (sender is not ComboBoxItem item || !ReferenceEquals(args.TargetObject, item)
            || ItemsControl.ItemsControlFromItemContainer(item) is not ComboBox combo) return;
        DependencyObject? owner = FindDropDownOwner(item);
        if (owner is null) return;
        if (SuppressHoverBringIntoView(combo.IsDropDownOpen, item.IsMouseOver,
            Mouse.LeftButton == MouseButtonState.Pressed, (bool)owner.GetValue(ExplicitNavigationProperty)))
            args.Handled = true;
    }

    private static void OnDropDownPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs args)
    {
        if (sender is not DependencyObject owner || args.Delta == 0) return;
        ScrollViewer? viewer = owner as ScrollViewer ?? FindDescendantScrollViewer(owner);
        if (viewer is not null)
        {
            if (args.Delta > 0) viewer.LineUp();
            else viewer.LineDown();
        }

        // An open drop-down owns the gesture even when its items fit without a
        // scrollbar or the wheel points beyond the current scroll boundary.
        args.Handled = true;
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject owner)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(owner);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(owner, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindDescendantScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }
}
