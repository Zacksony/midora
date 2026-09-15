using System.Diagnostics;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class ProjectSegmentIndexTests
{
    [Fact]
    public void LookupRepairsReorderMoveDeleteAndNewSegmentWithoutReturningStaleOwners()
    {
        using MidoraProject project = new(192);
        LogicalTrack first = new(project) { Name = "First" };
        LogicalTrack second = new(project) { Name = "Second" };
        project.Tracks.AddRange([first, second]);
        Segment one = new(project);
        Segment two = new(project);
        first.Segments.AddRange([one, two]);

        Assert.Equal((first, two, 1), Tuple(ProjectSegmentIndex.FindLogical(project, two.Id)));

        first.Segments.Reverse();
        Assert.Equal((first, two, 0), Tuple(ProjectSegmentIndex.FindLogical(project, two.Id)));

        first.Segments.Remove(two);
        second.Segments.Add(two);
        Assert.Equal((second, two, 0), Tuple(ProjectSegmentIndex.FindLogical(project, two.Id)));

        second.Segments.Remove(two);
        Assert.Null(ProjectSegmentIndex.FindLogical(project, two.Id));

        Segment three = new(project);
        second.Segments.Add(three);
        Assert.Equal((second, three, 0), Tuple(ProjectSegmentIndex.FindLogical(project, three.Id)));

        static (LogicalTrack, Segment, int)? Tuple(LogicalSegmentIndexEntry? value) =>
            value is null
                ? null
                : (value.Value.Track, value.Value.Segment, value.Value.Index);
    }

    [Fact]
    public void WarmedStableIdLookupDoesNotRescanTenThousandTracks()
    {
        const int trackCount = 10_000;
        const int lookupCount = 20_000;
        using MidoraProject project = new(192);
        Segment? target = null;
        LogicalTrack? targetTrack = null;
        for (int index = 0; index < trackCount; index++)
        {
            LogicalTrack track = new(project) { Name = $"Track {index}" };
            Segment segment = new(project);
            track.Segments.Add(segment);
            project.Tracks.Add(track);
            target = segment;
            targetTrack = track;
        }

        ProjectSegmentIndex.Warm(project);
        Stopwatch watch = Stopwatch.StartNew();
        for (int index = 0; index < lookupCount; index++)
        {
            LogicalSegmentIndexEntry located =
                ProjectSegmentIndex.FindLogical(project, target!.Id)!.Value;
            Assert.Same(targetTrack, located.Track);
        }
        watch.Stop();

        Assert.True(
            watch.Elapsed < TimeSpan.FromSeconds(1),
            $"{lookupCount:N0} warmed lookups across {trackCount:N0} Tracks took "
            + $"{watch.Elapsed.TotalMilliseconds:N1} ms.");
    }

    [Fact]
    public void LogicalAndMidiNamespacesAreResolvedIndependently()
    {
        using MidoraProject project = new(192);
        LogicalTrack logicalTrack = new(project) { Name = "Logical" };
        Segment logical = new(project);
        logicalTrack.Segments.Add(logical);
        project.Tracks.Add(logicalTrack);
        MidiChannelRoot root = new(project) { Name = "Root" };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack midiTrack = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
        MidiSegment midi = new(project);
        midiTrack.Segments.Add(midi);
        project.PureMidiTracks.Add(midiTrack);

        Assert.Equal(logical.Id, ProjectSegmentIndex.FindLogical(project, logical.Id)?.Segment.Id);
        Assert.Null(ProjectSegmentIndex.FindMidi(project, logical.Id));
        Assert.Equal(midi.Id, ProjectSegmentIndex.FindMidi(project, midi.Id)?.Segment.Id);
        Assert.Null(ProjectSegmentIndex.FindLogical(project, midi.Id));
    }
}
