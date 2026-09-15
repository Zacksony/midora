using System.Runtime.CompilerServices;

namespace Midora.Domain;

/// <summary>
/// Project-scoped stable-ID lookup for Arrangement Segments. The index is
/// populated once and repairs only entries whose owning list actually changed;
/// ordinary note/event edits therefore remain O(1) and do not rescan Tracks.
/// </summary>
public static class ProjectSegmentIndex
{
    private static readonly ConditionalWeakTable<MidoraProject, Index> Indexes = new();

    public static void Warm(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _ = Indexes.GetValue(project, static value => new Index(value));
    }

    public static LogicalSegmentIndexEntry? FindLogical(
        MidoraProject project,
        MidoraId segmentId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (segmentId == default) return null;
        return Indexes.GetValue(project, static value => new Index(value))
            .FindLogical(segmentId);
    }

    public static MidiSegmentIndexEntry? FindMidi(
        MidoraProject project,
        MidoraId segmentId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (segmentId == default) return null;
        return Indexes.GetValue(project, static value => new Index(value))
            .FindMidi(segmentId);
    }

    private sealed class Index
    {
        private readonly MidoraProject _project;
        private readonly object _gate = new();
        private readonly Dictionary<MidoraId, LogicalSegmentIndexEntry> _logical = [];
        private readonly Dictionary<MidoraId, MidiSegmentIndexEntry> _midi = [];
        private List<LogicalTrack> _logicalCatalog;
        private List<PureMidiTrack> _midiCatalog;

        public Index(MidoraProject project)
        {
            _project = project;
            _logicalCatalog = project.Tracks;
            _midiCatalog = project.PureMidiTracks;
            Rebuild();
        }

        public LogicalSegmentIndexEntry? FindLogical(MidoraId id)
        {
            lock (_gate)
            {
                RefreshCatalogRoots();
                if (_midi.ContainsKey(id)) return null;
                if (_logical.TryGetValue(id, out LogicalSegmentIndexEntry cached)
                    && TryRefresh(cached, out LogicalSegmentIndexEntry refreshed))
                {
                    _logical[id] = refreshed;
                    return refreshed;
                }

                _logical.Remove(id);
                LogicalSegmentIndexEntry? found = ScanLogical(id);
                if (found is not null) _logical.Add(id, found.Value);
                return found;
            }
        }

        public MidiSegmentIndexEntry? FindMidi(MidoraId id)
        {
            lock (_gate)
            {
                RefreshCatalogRoots();
                if (_logical.ContainsKey(id)) return null;
                if (_midi.TryGetValue(id, out MidiSegmentIndexEntry cached)
                    && TryRefresh(cached, out MidiSegmentIndexEntry refreshed))
                {
                    _midi[id] = refreshed;
                    return refreshed;
                }

                _midi.Remove(id);
                MidiSegmentIndexEntry? found = ScanMidi(id);
                if (found is not null) _midi.Add(id, found.Value);
                return found;
            }
        }

        private void RefreshCatalogRoots()
        {
            // A detached transaction may replace an entire Track catalog. The
            // old Track still owns its old Segments, so validating its child
            // list alone cannot establish that it remains in this Project.
            if (!ReferenceEquals(_logicalCatalog, _project.Tracks))
            {
                _logicalCatalog = _project.Tracks;
                _logical.Clear();
            }
            if (!ReferenceEquals(_midiCatalog, _project.PureMidiTracks))
            {
                _midiCatalog = _project.PureMidiTracks;
                _midi.Clear();
            }
        }

        private void Rebuild()
        {
            foreach (LogicalTrack track in _project.Tracks)
            {
                for (int index = 0; index < track.Segments.Count; index++)
                {
                    Segment segment = track.Segments[index];
                    if (!_logical.TryAdd(segment.Id, new(track, segment, index)))
                        throw new InvalidOperationException("The Segment stable ID is duplicated.");
                }
            }
            foreach (PureMidiTrack track in _project.PureMidiTracks)
            {
                for (int index = 0; index < track.Segments.Count; index++)
                {
                    MidiSegment segment = track.Segments[index];
                    if (!_midi.TryAdd(segment.Id, new(track, segment, index)))
                        throw new InvalidOperationException("The MIDI Segment stable ID is duplicated.");
                }
            }
        }

        private static bool TryRefresh(
            LogicalSegmentIndexEntry cached,
            out LogicalSegmentIndexEntry refreshed)
        {
            if ((uint)cached.Index < (uint)cached.Track.Segments.Count
                && ReferenceEquals(cached.Track.Segments[cached.Index], cached.Segment))
            {
                refreshed = cached;
                return true;
            }
            int index = cached.Track.Segments.IndexOf(cached.Segment);
            refreshed = cached with { Index = index };
            return index >= 0;
        }

        private static bool TryRefresh(
            MidiSegmentIndexEntry cached,
            out MidiSegmentIndexEntry refreshed)
        {
            if ((uint)cached.Index < (uint)cached.Track.Segments.Count
                && ReferenceEquals(cached.Track.Segments[cached.Index], cached.Segment))
            {
                refreshed = cached;
                return true;
            }
            int index = cached.Track.Segments.IndexOf(cached.Segment);
            refreshed = cached with { Index = index };
            return index >= 0;
        }

        private LogicalSegmentIndexEntry? ScanLogical(MidoraId id)
        {
            LogicalSegmentIndexEntry? found = null;
            foreach (LogicalTrack track in _project.Tracks)
            {
                for (int index = 0; index < track.Segments.Count; index++)
                {
                    Segment segment = track.Segments[index];
                    if (segment.Id != id) continue;
                    if (found is not null)
                        throw new InvalidOperationException("The Segment stable ID is duplicated.");
                    found = new(track, segment, index);
                }
            }
            return found;
        }

        private MidiSegmentIndexEntry? ScanMidi(MidoraId id)
        {
            MidiSegmentIndexEntry? found = null;
            foreach (PureMidiTrack track in _project.PureMidiTracks)
            {
                for (int index = 0; index < track.Segments.Count; index++)
                {
                    MidiSegment segment = track.Segments[index];
                    if (segment.Id != id) continue;
                    if (found is not null)
                        throw new InvalidOperationException("The MIDI Segment stable ID is duplicated.");
                    found = new(track, segment, index);
                }
            }
            return found;
        }
    }
}

public readonly record struct LogicalSegmentIndexEntry(
    LogicalTrack Track,
    Segment Segment,
    int Index);

public readonly record struct MidiSegmentIndexEntry(
    PureMidiTrack Track,
    MidiSegment Segment,
    int Index);
