using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class LogicalParameterEventBindingDialogTests
{
    [Fact]
    public void ControllerSelectorMatchesSubVoiceAddEventCatalogExactly()
    {
        Assert.Same(
            MidiControlChangeCatalog.EditableControllers,
            LogicalParameterEventBindingDialog.SelectableControllers);
        Assert.Equal(
            MidiControlChangeCatalog.EditableControllers.Select(value => value.Number),
            LogicalParameterEventBindingDialog.SelectableControllers.Select(value => value.Number));
        Assert.Equal(28, LogicalParameterEventBindingDialog.SelectableControllers.Count);
        Assert.DoesNotContain(
            LogicalParameterEventBindingDialog.SelectableControllers,
            value => value.Number is 2 or 91 or 93 or 120);
        Assert.Equal(
            "11 - Expression (MSB)",
            LogicalParameterEventBindingDialog.SelectableControllers
                .Single(value => value.Number == 11)
                .DisplayName);
    }
}
