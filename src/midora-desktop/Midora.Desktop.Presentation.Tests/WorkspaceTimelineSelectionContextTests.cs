using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Tests;

public sealed class WorkspaceTimelineSelectionContextTests
{
    [Fact]
    public void TypedClicksTrackExactLaneAndOwnerWideQuantizeScope()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource cc11 = DirectEventSource(segment: 10, controller: 11);
        WorkspaceTimelineSelectionSource cc74 = DirectEventSource(segment: 10, controller: 74);
        WorkspaceTimelineSelectionSource otherSegment = DirectEventSource(segment: 20, controller: 11);

        selection.Replace(new MidoraId(1), cc11);
        selection.Add(new MidoraId(2), cc11);

        Assert.Equal(cc11, selection.HomogeneousTimelineSource);
        Assert.Equal(cc11.QuantizeScope, selection.HomogeneousTimelineQuantizeScope);

        selection.Add(new MidoraId(3), cc74);

        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Equal(cc11.QuantizeScope, selection.HomogeneousTimelineQuantizeScope);

        selection.Add(new MidoraId(4), otherSegment);

        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Null(selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void MaterializedRootRestoresTypedContextWithoutResolvingIds()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource cc11 = DirectEventSource(segment: 10, controller: 11);
        WorkspaceTimelineSelectionSource cc74 = DirectEventSource(segment: 10, controller: 74);
        selection.ApplyRange(
            [new MidoraId(1), new MidoraId(2)],
            WorkspaceSelectionRangeMode.Replace,
            cc11);
        selection.ApplyRange(
            [new MidoraId(3)],
            WorkspaceSelectionRangeMode.Add,
            cc74);
        CompressedMidoraIdSet historicalRoot = selection.SharedIds;
        MidoraId? historicalPrimary = selection.Primary;
        MidoraId? historicalAnchor = selection.Anchor;

        selection.Replace(
            new MidoraId(100),
            new WorkspaceTimelineSelectionSource(
                WorkspaceTimelineSelectionKind.LogicalNote,
                new MidoraId(99)));
        selection.AdoptMaterialized(
            historicalRoot,
            historicalPrimary,
            historicalAnchor);

        Assert.Same(historicalRoot, selection.SharedIds);
        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Equal(cc11.QuantizeScope, selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void PreparedProjectionPreservesMixedLaneContextWhenIdsAreUnchanged()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource cc11 = DirectEventSource(segment: 10, controller: 11);
        WorkspaceTimelineSelectionSource cc74 = DirectEventSource(segment: 10, controller: 74);
        selection.ApplyRange(
            [new MidoraId(1), new MidoraId(2)],
            WorkspaceSelectionRangeMode.Replace,
            cc11);
        selection.ApplyRange(
            [new MidoraId(3), new MidoraId(4)],
            WorkspaceSelectionRangeMode.Add,
            cc74);
        CompressedMidoraIdSet originalRoot = selection.SharedIds;

        PreparedWorkspaceSelectionProjection projection = selection.PrepareProjection(
            selection.Ids.ToArray());
        selection.Replace(
            new MidoraId(100),
            new WorkspaceTimelineSelectionSource(
                WorkspaceTimelineSelectionKind.TemplateNote,
                new MidoraId(90),
                new MidoraId(91)));
        selection.AdoptPrepared(projection);

        Assert.Same(originalRoot, selection.SharedIds);
        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Equal(cc11.QuantizeScope, selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void PreparedProjectionCarriesHomogeneousSourceToChangedResultIds()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource notes = new(
            WorkspaceTimelineSelectionKind.DirectMidiNote,
            new MidoraId(10));
        selection.ApplyRange(
            [new MidoraId(1), new MidoraId(2)],
            WorkspaceSelectionRangeMode.Replace,
            notes);

        PreparedWorkspaceSelectionProjection projection = selection.PrepareProjection(
            [new MidoraId(20), new MidoraId(21), new MidoraId(22)]);
        selection.AdoptPrepared(projection);

        Assert.Equal(3, selection.Ids.Count);
        Assert.Equal(notes, selection.HomogeneousTimelineSource);
        Assert.Equal(notes, selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void ExplicitTypedClickRepairsOnlyAnUnambiguousLegacySelection()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource notes = new(
            WorkspaceTimelineSelectionKind.LogicalNote,
            new MidoraId(10));
        MidoraId one = new(1);

        selection.Replace(one);
        Assert.Null(selection.HomogeneousTimelineSource);

        selection.Add(one, notes);
        Assert.Equal(notes, selection.HomogeneousTimelineSource);

        WorkspaceTimelineSelectionSource projectedElsewhere = notes with
        {
            Kind = WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
            OwnerId = new MidoraId(20),
            SecondaryOwnerId = new MidoraId(21),
            MidiTarget = MidiValueTarget.ControlChange(11)
        };
        selection.Add(one, projectedElsewhere);
        Assert.Equal(projectedElsewhere, selection.HomogeneousTimelineSource);

        selection.Add(new MidoraId(2));
        selection.Add(new MidoraId(3), notes);
        Assert.Null(selection.HomogeneousTimelineSource);

        selection.Replace(one);
        selection.Toggle(one, notes);
        selection.Add(new MidoraId(4), notes);
        Assert.Equal(notes, selection.HomogeneousTimelineSource);
    }

    [Fact]
    public void ExplicitPreparedDestinationReplacesOldSourceAndSurvivesHistoryRestoration()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource oldSource = DirectEventSource(10, 11);
        WorkspaceTimelineSelectionSource destination = new(WorkspaceTimelineSelectionKind.TemplateNote,
            new MidoraId(20), new MidoraId(21));
        selection.Replace(new MidoraId(1), oldSource);
        CompressedMidoraIdSet original = selection.SharedIds;
        PreparedWorkspaceSelectionProjection projection = selection.PrepareProjection(
            [new MidoraId(100), new MidoraId(101)], destination, null);
        Assert.Same(original, selection.SharedIds);
        Assert.Equal(oldSource, selection.HomogeneousTimelineSource);
        selection.AdoptPrepared(projection);
        Assert.Equal(destination, selection.HomogeneousTimelineSource);
        Assert.Equal(destination.QuantizeScope, selection.HomogeneousTimelineQuantizeScope);
        CompressedMidoraIdSet pasted = selection.SharedIds;
        selection.AdoptMaterialized(original, new MidoraId(1), new MidoraId(1));
        Assert.Equal(oldSource, selection.HomogeneousTimelineSource);
        selection.AdoptMaterialized(pasted, new MidoraId(100), new MidoraId(100));
        Assert.Equal(destination, selection.HomogeneousTimelineSource);
    }

    [Fact]
    public void ExplicitUnknownAndEmptyResultsDoNotInheritOldRouting()
    {
        WorkspaceSelection selection = new();
        var source = DirectEventSource(10, 11);
        selection.Replace(new MidoraId(1), source);
        selection.AdoptPrepared(selection.PrepareProjection([new MidoraId(2)], null, null));
        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Null(selection.HomogeneousTimelineQuantizeScope);
        selection.AdoptPrepared(selection.PrepareProjection(
            [new MidoraId(3), new MidoraId(4)], null, source.QuantizeScope));
        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Equal(source.QuantizeScope, selection.HomogeneousTimelineQuantizeScope);
        selection.AdoptPrepared(selection.PrepareProjection([], source, source.QuantizeScope));
        Assert.Empty(selection.Ids);
        Assert.Null(selection.HomogeneousTimelineSource);
        Assert.Null(selection.HomogeneousTimelineQuantizeScope);
    }

    [Fact]
    public void CancellingExplicitDestinationProjectionDoesNotChangeSelection()
    {
        WorkspaceSelection selection = new();
        var source = DirectEventSource(10, 11);
        selection.Replace(new MidoraId(1), source);
        CompressedMidoraIdSet original = selection.SharedIds;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => selection.PrepareProjection(
            [new MidoraId(2)], null, null, cancellation.Token));
        Assert.Same(original, selection.SharedIds);
        Assert.Equal(source, selection.HomogeneousTimelineSource);
    }

    [Fact]
    public void ArrangementSelectionUsesOneTypedContextAcrossTrackKinds()
    {
        WorkspaceSelection selection = new();
        WorkspaceTimelineSelectionSource arrangement = new(
            WorkspaceTimelineSelectionKind.ArrangementSegment);

        selection.Replace(new MidoraId(1), arrangement);
        selection.Add(new MidoraId(2), arrangement);

        Assert.Equal(arrangement, selection.HomogeneousTimelineSource);
        Assert.Equal(arrangement, selection.HomogeneousTimelineQuantizeScope);
    }

    private static WorkspaceTimelineSelectionSource DirectEventSource(
        long segment,
        int controller) =>
        new(
            WorkspaceTimelineSelectionKind.DirectMidiEventPoint,
            new MidoraId(segment),
            DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange,
            DirectMidiData1: controller,
            PointMinimum: 0,
            PointMaximum: 127);
}
