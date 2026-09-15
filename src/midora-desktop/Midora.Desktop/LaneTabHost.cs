using System.Windows;
using System.Windows.Controls;

namespace Midora.Desktop;

/// <summary>The three content kinds share a single selected ContentPresenter.
/// Actual target tabs live in the header, not in one control per event target.</summary>
public sealed class LaneTabHost : TabControl
{
    internal LaneTabHeader? Header { get; private set; }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Header = GetTemplateChild("PART_LaneHeader") as LaneTabHeader;
        if (Header is not null) Header.Owner = this;
    }
    internal void ShowCurrentEventTarget() => Header?.ShowCurrentEventTarget();
    internal void ShowInstrumentTarget() => Header?.ShowInstrumentTarget();
}
