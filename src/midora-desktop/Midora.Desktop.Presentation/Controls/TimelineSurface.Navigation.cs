using System.Windows;

namespace Midora.Desktop.Presentation.Controls;

public partial class TimelineSurface
{
    /// <summary>
    /// Read-only navigation over the ruler/content area, without hit testing,
    /// source enumeration, edit selection or an invisible edit-snap quantization.
    /// </summary>
    public bool TryGetNavigationTick(Point point, out long tick)
    {
        tick = 0;
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || point.X < GetLaneHeaderWidth() || point.X >= ActualWidth
            || point.Y < 0 || point.Y >= ActualHeight || !TryCreateViewport(out var viewport)) return false;
        // Keep the large absolute start out of floating-point addition.
        decimal offset = decimal.Round((decimal)((point.X - GetLaneHeaderWidth()) / viewport.Width)
            * viewport.TickLength, 0, MidpointRounding.AwayFromZero);
        tick = checked(viewport.StartTick + (long)Math.Clamp(offset, 0, viewport.TickLength));
        return true;
    }
}
