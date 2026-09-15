namespace Midora.Desktop.Presentation.Rendering;

/// <summary>
/// Isolates shared raster computation from a drawing method's UI closures.
/// Callers pass immutable source/value state and a static delegate only.
/// </summary>
internal static class TimelineRasterFactory
{
    internal static Func<CancellationToken, TimelineRasterBuffer> Create<TState>(
        TState state,
        Func<TState, CancellationToken, TimelineRasterBuffer> factory) =>
        token => factory(state, token);
}
