using System.Windows.Input;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class Stage5ArrangementRoutingTests
{
    [Fact]
    public void SameTypeMovesAndResizesKeepTheExistingFastPath()
    {
        var (snapshot, first, second) = Snapshot(false);
        Assert.False(Requires(snapshot, [first.Id, second.Id], first, 0, false));
        Assert.False(Requires(snapshot, [first.Id, second.Id], first, 1, false));
        Assert.False(Requires(snapshot, [first.Id], first, 1, true));
        Assert.False(MainWindow.RequiresArrangementSegmentConversion(snapshot, [first.Id, second.Id],
            new(first, TimelineItemEditKind.ResizeEnd, 100, 1, 0, ModifierKeys.None)));
    }

    [Fact]
    public void MixedVerticalAndCopyMovesUseOneConversionButHorizontalMoveStaysFast()
    {
        var (snapshot, first, second) = Snapshot(true);
        Assert.False(Requires(snapshot, [first.Id, second.Id], first, 0, false));
        Assert.True(Requires(snapshot, [first.Id, second.Id], first, 1, false));
        Assert.True(Requires(snapshot, [first.Id, second.Id], first, 0, true));
        Assert.True(Requires(snapshot, [first.Id], first, 1, false));
    }

    [Fact]
    public void ConversionLossTextOmitsZeroFieldsButIncludesEmptyLaneOwners()
    {
        string text = MainWindow.FormatSegmentConversionLosses(new(LogicalParameterLanes: 1));
        Assert.Contains("Logical parameter lanes: 1", text);
        Assert.DoesNotContain("Logical parameter points:", text);
        Assert.DoesNotContain("Note Off velocities:", text);
        Assert.Contains("Cancel leaves the source unchanged", text);
    }

    [Theory]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalNote)]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiNote)]
    [InlineData(WorkspaceTimelineSelectionKind.TemplateNote)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalParameterPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiEventPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.SubVoiceEventPoint)]
    public void CreationAvailabilityUsesOwnerTypeNotSelection(WorkspaceTimelineSelectionKind kind) =>
        Assert.True(MainWindow.IsTimelineGenerationSource(new(kind, new MidoraId(1))));

    [Fact]
    public void ArrangementAndMissingLaneDoNotOfferNoteGeneration()
    {
        Assert.False(MainWindow.IsTimelineGenerationSource(null));
        Assert.False(MainWindow.IsTimelineGenerationSource(new(WorkspaceTimelineSelectionKind.ArrangementSegment)));
    }

    private static bool Requires(TimelineRenderSnapshot snapshot, MidoraId[] ids,
        TimelineRenderItem primary, int laneDelta, bool copy) => MainWindow.RequiresArrangementSegmentConversion(
            snapshot, ids, new(primary, TimelineItemEditKind.Move, 100, laneDelta, 0, ModifierKeys.None, copy));

    private static (TimelineRenderSnapshot, TimelineRenderItem, TimelineRenderItem) Snapshot(bool mixed)
    {
        var first = new TimelineRenderItem(new(10), TimelineItemKind.Segment, 0, 100, 1, 0, 0, TimelineItemState.None);
        var second = new TimelineRenderItem(new(20), TimelineItemKind.Segment, 200, 300, 2, 0, 0, TimelineItemState.None);
        ArrangementLaneDescriptor[] lanes = Enumerable.Range(0, 4).Select(index => new ArrangementLaneDescriptor(
            index, index == 0 ? ArrangementLaneKind.Conductor
                : mixed && index == 2 ? ArrangementLaneKind.PureMidiTrack : ArrangementLaneKind.LogicalTrack,
            new MidoraId(index + 1), null, 0, true, false, index != 0)).ToArray();
        return (new(0, "stage5", [first, second], laneLabels: ["Conductor", "A", "B", "C"], arrangementLanes: lanes), first, second);
    }
}
