using System.Windows.Input;
using Midora.Application;
using Xunit;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Tests;

public sealed class SegmentConversionRoutingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HomogeneousVerticalTransferKeepsFastPathAcrossInterleavedTrackKinds(bool copy)
    {
        var (snapshot, items) = Snapshot([L, L, M, L, L], [0, 1]);
        Assert.False(MainWindow.RequiresArrangementSegmentConversion(snapshot, items.Select(static item => item.Id).ToArray(),
            Edit(items[0], 3, copy)));
    }

    [Fact]
    public void AnyDestinationTypeChangeRequiresReviewIncludingNonPrimarySegment()
    {
        var (snapshot, items) = Snapshot([L, L, L, M], [0, 1]);
        Assert.True(MainWindow.RequiresArrangementSegmentConversion(snapshot, items.Select(static item => item.Id).ToArray(),
            Edit(items[0], 2, false)));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    public void MixedSelectionsUseUnifiedTransferOnlyWhenRequired(int laneDelta, bool copy, bool expected)
    {
        var (snapshot, items) = Snapshot([L, M, L, M], [0, 1]);
        Assert.Equal(expected, MainWindow.RequiresArrangementSegmentConversion(snapshot,
            items.Select(static item => item.Id).ToArray(), Edit(items[0], laneDelta, copy)));
    }

    [Theory]
    [InlineData(TimelineItemEditKind.ResizeStart)]
    [InlineData(TimelineItemEditKind.ResizeEnd)]
    public void MixedResizeRemainsOnExistingAtomicEdgePath(TimelineItemEditKind kind)
    {
        var (snapshot, items) = Snapshot([L, M], [0, 1]);
        Assert.False(MainWindow.RequiresArrangementSegmentConversion(snapshot,
            items.Select(static item => item.Id).ToArray(),
            new(items[0], kind, 24, 1, 0, ModifierKeys.None)));
    }

    [Fact]
    public void LossTextIncludesEmptyLaneAndOnlyActualLossCategories()
    {
        string text = MainWindow.FormatSegmentConversionLosses(new(LogicalParameterLanes: 1));
        Assert.Contains("Logical parameter lanes: 1", text);
        Assert.DoesNotContain("Logical parameter points:", text);
        Assert.DoesNotContain("Note Off velocities:", text);
        Assert.Contains("hidden notes", text);
    }

    private const ArrangementLaneKind L = ArrangementLaneKind.LogicalTrack;
    private const ArrangementLaneKind M = ArrangementLaneKind.PureMidiTrack;
    private static TimelineItemEditEventArgs Edit(TimelineRenderItem item, int lanes, bool copy) =>
        new(item, TimelineItemEditKind.Move, 480, lanes, 0, copy ? ModifierKeys.Control : ModifierKeys.None, copy);
    private static (TimelineRenderSnapshot Snapshot, TimelineRenderItem[] Items) Snapshot(
        ArrangementLaneKind[] laneKinds, int[] itemLanes)
    {
        TimelineRenderItem[] items = itemLanes.Select((lane, index) =>
            new TimelineRenderItem(new(index + 1), TimelineItemKind.Segment, 0, 240, lane, 0, 0, TimelineItemState.None)).ToArray();
        ArrangementLaneDescriptor[] lanes = laneKinds.Select((kind, lane) =>
            new ArrangementLaneDescriptor(lane, kind, new MidoraId(lane + 100), null, 0, true, false, true)).ToArray();
        return (new(0, "test-arrangement-conversion", items, laneKinds.Select(static kind => kind.ToString()).ToArray(),
            arrangementLanes: lanes), items);
    }
}
