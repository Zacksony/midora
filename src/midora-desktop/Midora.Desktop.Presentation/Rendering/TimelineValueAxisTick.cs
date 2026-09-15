namespace Midora.Desktop.Presentation.Rendering;

/// <summary>Value-axis labels and grid lines share the same display coordinate.</summary>
public readonly record struct TimelineValueAxisTick(double Normalized, double Value)
{
    public static TimelineValueAxisTick At(
        double minimum, double maximum, double viewMinimum, double viewMaximum,
        double screenRatio, bool integral)
    {
        double normalized = viewMinimum + screenRatio * (viewMaximum - viewMinimum);
        double value = minimum + normalized * (maximum - minimum);
        // Signed integral ranges may be asymmetric by one unit (Pitch Bend is
        // -8192..8191). Their midpoint is -0.5, not a MIDI state. Label and draw
        // the actual neutral tick instead of rounding it to -1. Do not change
        // pointer/value quantization or insert zero when it is off screen.
        if (integral && minimum < 0 && maximum > 0 && Math.Abs(value) <= 0.5)
        {
            double zero = -minimum / (maximum - minimum);
            if (zero >= viewMinimum && zero <= viewMaximum) return new(zero, 0);
        }
        return new(normalized, value);
    }
}
