using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop.Presentation.Tests;

public sealed class WorkspaceSelectionDiagnosticsTests
{
    [Theory]
    [InlineData(WorkspaceTimelineSelectionKind.TemplateNote)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalParameterPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiEventPoint)]
    public void FormattingDoesNotRecurseThroughQuantizeScope(WorkspaceTimelineSelectionKind kind)
    {
        var source = new WorkspaceTimelineSelectionSource(kind);
        Assert.Equal($"WorkspaceTimelineSelectionSource {{ Kind = {kind} }}", source.ToString());
        Assert.Equal(source.ToString(), source.QuantizeScope.ToString());
    }
}
