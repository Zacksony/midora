using System.Windows;
using System.Windows.Controls;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop;

public partial class ValueTraceShapeSelector : UserControl
{
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(TimelineValueTraceShape), typeof(ValueTraceShapeSelector),
        new FrameworkPropertyMetadata(TimelineValueTraceShape.Free,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            static (owner, _) => ((ValueTraceShapeSelector)owner).Refresh()));
    public TimelineValueTraceShape Shape
    {
        get => (TimelineValueTraceShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }
    public event EventHandler? ShapeChosen;
    public ValueTraceShapeSelector() { InitializeComponent(); Refresh(); }
    private void Refresh()
    {
        if (Free is null) return;
        Free.IsChecked = Shape == TimelineValueTraceShape.Free;
        Line.IsChecked = Shape == TimelineValueTraceShape.Line;
        Horizontal.IsChecked = Shape == TimelineValueTraceShape.Horizontal;
    }
    private void OnShapeClick(object sender, RoutedEventArgs args)
    {
        SetCurrentValue(ShapeProperty, Enum.Parse<TimelineValueTraceShape>((string)((FrameworkElement)sender).Tag));
        Refresh();
        ShapeChosen?.Invoke(this, EventArgs.Empty);
        args.Handled = true;
    }
}
