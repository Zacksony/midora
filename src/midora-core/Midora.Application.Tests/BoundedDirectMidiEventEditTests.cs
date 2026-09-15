using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedDirectMidiEventEditTests
{
    [Fact]
    public void LargeEventMoveOverwritesOnlyTouchedLanesAndUndoRestoresExactRoot()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Create(project);
        var selected = segment.ChannelEvents.Take(5000).Select(static value => value.Id).ToArray();
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var edit = ProjectDomainEditCommands.AdjustDirectMidiEventPoints(segment.Id, selected, 20000, 0, 1, false).Prepare(project);
        Assert.Same(segment, track.Segments[0]);
        edit.Apply(project);
        var current = track.Segments[0];
        Assert.IsType<BoundedDirectMidiEventSource>(current.ChannelEvents.PagedSource);
        Assert.Equal(5000, current.ChannelEvents.Count);
        Assert.Equal(selected, current.ChannelEvents.Select(static value => value.Id));
        Assert.All(current.ChannelEvents, value => Assert.Equal(42, value.Data2));
        Assert.Equal(20000, current.ChannelEvents[0].Tick);
        Assert.Equal(10000, segment.ChannelEvents.Count);
        edit.Undo(project);
        Assert.Same(segment, track.Segments[0]);
        edit.Apply(project);
        Assert.Same(current, track.Segments[0]);
        Assert.True(scope.Resources.PeakResidentBytes <= scope.Resources.Budget.MaximumResidentBytes);
    }

    [Fact]
    public void RepeatedEventEditsPreserveImportedDuplicatesOutsideTheirTargets()
    {
        using var project = new MidoraProject(480);
        var (track, segment) = Create(project);
        var imported = new DirectMidiChannelEvent(project)
        { Tick = 39996, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 20 };
        segment.ChannelEvents.Add(imported);
        var ids = segment.ChannelEvents.Take(5000).Select(static value => value.Id).ToArray();
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var first = ProjectDomainEditCommands.AdjustDirectMidiEventPoints(segment.Id, ids, 100000, 0, 0, false).Prepare(project);
        first.Apply(project);
        var current = track.Segments[0];
        Assert.Equal(10001, current.ChannelEvents.Count);
        Assert.Equal(2, current.ChannelEvents.QueryValues(39996, 39997).Count());
        var second = ProjectDomainEditCommands.SetDirectMidiEventValues(segment.Id, ids, data2: 60).Prepare(project);
        second.Apply(project);
        var latest = track.Segments[0];
        Assert.Equal(10001, latest.ChannelEvents.Count);
        Assert.All(latest.ChannelEvents.ResolveValuesByIds(ids.ToHashSet()), value => Assert.Equal(60, value.Data2));
        Assert.Equal(2, latest.ChannelEvents.QueryValues(39996, 39997).Count());
    }

    private static (PureMidiTrack, MidiSegment) Create(MidoraProject project)
    {
        var root = new MidiChannelRoot(project) { Name = "Root" };
        project.MidiChannelRoots.Add(root);
        var track = new PureMidiTrack(project) { Name = "Track", MidiChannelRootId = root.Id };
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var segment = new MidiSegment(project) { LengthTicks = 200000 };
        track.Segments.Add(segment);
        segment.ChannelEvents.AddRange(Enumerable.Range(0, 10000).Select(i => new DirectMidiChannelEvent(project)
        { Tick = i * 4L, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 41, Order = i }));
        return (track, segment);
    }
}
