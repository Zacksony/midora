using Midora.Application;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

internal static class OnionPresentation
{
    internal static uint Color(MidoraColor color) => 0xff000000u | (uint)color.Red << 16 | (uint)color.Green << 8 | color.Blue;
    internal static readonly uint[] VoiceColors = [0xff6d7fa8, 0xff9b6a6a, 0xff6f936f, 0xff9a815f,
        0xff806fa3, 0xff60918c, 0xff9a6f8a, 0xff849064];

    public static IReadOnlyList<TimelineOnionTrack> CaptureTracks(MidoraProject project,
        IReadOnlySet<MidoraId>? included = null, long targetOffset = 0)
    {
        var logical = project.Tracks.ToDictionary(t => t.Id);
        var midi = project.PureMidiTracks.ToDictionary(t => t.Id);
        List<TimelineOnionTrack> tracks = [];
        foreach (var reference in project.TracksInArrangementOrder())
        {
            if (included is not null && !included.Contains(reference.TrackId)) continue;
            List<TimelineOnionClip> clips = [];
            uint color;
            if (logical.TryGetValue(reference.TrackId, out var track))
            {
                color = Color(ProjectTrackColorPolicy.ResolveDisplayColor(project, track));
                foreach (var segment in track.Segments)
                {
                    Int128 shift = (Int128)segment.ProjectStartTick - segment.ContentOffsetTick - targetOffset;
                    if (shift < long.MinValue || shift > long.MaxValue) continue; // Entire clip lies outside the representable target timeline.
                    clips.Add(new(new PagedLogicalNoteTimelineItemSource(segment, LogicalNoteTimelineProjection.Notes),
                        segment.ContentOffsetTick, segment.ContentEndTick,
                        (long)shift));
                }
            }
            else if (midi.TryGetValue(reference.TrackId, out var midiTrack))
            {
                color = Color(ProjectTrackColorPolicy.ResolveDisplayColor(midiTrack));
                foreach (var segment in midiTrack.Segments)
                {
                    Int128 shift = (Int128)segment.ProjectStartTick - segment.ContentOffsetTick - targetOffset;
                    if (shift < long.MinValue || shift > long.MaxValue) continue;
                    clips.Add(new(new PagedDirectMidiTimelineItemSource(segment, DirectMidiTimelineProjection.Notes),
                        segment.ContentOffsetTick, segment.ContentEndTick,
                        (long)shift));
                }
            }
            else continue;
            tracks.Add(new(reference.TrackId, color, clips));
        }
        return tracks;
    }

    public static (MidoraId Track, long Offset)? Target(MidoraProject project, MidoraId? segmentId)
    {
        if (TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } logical)
            return (logical.Track.Id, logical.Segment.ProjectStartTick - logical.Segment.ContentOffsetTick);
        if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } midi)
            return (midi.Track.Id, midi.Segment.ProjectStartTick - midi.Segment.ContentOffsetTick);
        return null;
    }
}

internal sealed class CompiledOnionItemSource(CompiledOnionNoteIndex index, MidoraId track) :
    INonBlockingTimelineFingerprintSource
{
    public long Count => index.Count;
    public long MaximumEndTick => index.GetTrackEndTick(track);
    public ulong ContentFingerprint => index.GetTrackFingerprint(track);
    public bool HasHitTestableItems => false;
    public void VisitInto(long startTick, long endTick, int firstLane, int lastLaneExclusive, Action<TimelineRenderItem> visitor)
        => index.Visit(track, startTick, endTick, Math.Clamp(128 - lastLaneExclusive, 0, 127),
            Math.Clamp(127 - firstLane, 0, 127), note => visitor(new(track, TimelineItemKind.DirectMidiNote,
                note.StartTick, note.EndTick, 127 - note.Key, 0, 0, TimelineItemState.HitTestDisabled)));
    public void QueryInto(long startTick, long endTick, int firstLane, int lastLaneExclusive, List<TimelineRenderItem> destination)
        => VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
    public bool TryGetById(MidoraId id, out TimelineRenderItem item) { item = default; return false; }
    public IEnumerable<TimelineRenderItem> EnumerateAll() => throw new NotSupportedException("Onion sources are range-only and read-only.");
}
