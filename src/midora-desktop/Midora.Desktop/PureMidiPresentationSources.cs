using System.Text;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

internal sealed class PagedMidiSegmentPreviewSource : ITimelineSegmentPreviewSource
{
    private readonly DirectMidiNoteQuerySnapshot _notes;
    private readonly DirectMidiChannelEventQuerySnapshot _channelEvents;
    private readonly OpaqueMidiEventQuerySnapshot _opaqueEvents;
    private readonly long _visibleStart;
    private readonly long _visibleEnd;
    private readonly long _length;
    private readonly ulong _noteContentFingerprint;
    private readonly ulong _eventContentFingerprint;

    public PagedMidiSegmentPreviewSource(MidiSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        _notes = segment.Notes.CreateQuerySnapshot();
        _channelEvents = segment.ChannelEvents.CreateQuerySnapshot();
        _opaqueEvents = segment.OpaqueEvents.CreateQuerySnapshot();
        _visibleStart = segment.ContentOffsetTick;
        _visibleEnd = segment.ContentEndTick;
        _length = segment.LengthTicks;
        _noteContentFingerprint = PureMidiPresentationFingerprint.Create(
            segment.PagedContentFingerprint,
            _notes.Generation,
            _visibleStart,
            _length,
            1);
        _eventContentFingerprint = PureMidiPresentationFingerprint.Create(
            segment.PagedContentFingerprint,
            _channelEvents.Generation,
            _opaqueEvents.Generation,
            _visibleStart,
            _length,
            2);
    }

    public bool HasNoteContent => _notes.Count != 0;

    public bool HasEventContent => _channelEvents.Count != 0
        || _opaqueEvents.Count != 0;

    public ulong NoteContentFingerprint => _noteContentFingerprint;

    public ulong EventContentFingerprint => _eventContentFingerprint;

    public void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination)
    {
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _visibleStart;
        long visibleEnd = _visibleEnd;
        foreach (DirectMidiNoteValue note in _notes.QueryValues(startTick, endTick))
        {
            long noteEnd = note.StartTick > long.MaxValue - Math.Max(1, note.LengthTicks)
                ? long.MaxValue
                : note.StartTick + Math.Max(1, note.LengthTicks);
            long clippedStart = Math.Max(visibleStart, note.StartTick);
            long clippedEnd = Math.Min(visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            destination.Add(new(
                (clippedStart - visibleStart) / (double)_length,
                (clippedEnd - visibleStart) / (double)_length,
                Math.Clamp(note.Key, 0, 127)));
        }
    }

    public void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination)
    {
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _visibleStart;
        foreach (DirectMidiChannelEventValue value in _channelEvents.QueryValues(startTick, endTick))
        {
            if (value.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff)
                continue;
            destination.Add(new(
                (value.Tick - visibleStart) / (double)_length,
                Normalize(value)));
        }
        foreach (OpaqueMidiEventValue value in _opaqueEvents.QueryValues(startTick, endTick))
        {
            destination.Add(new(
                (value.Tick - visibleStart) / (double)_length,
                1));
        }
    }

    public void VisitNotes(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _visibleStart;
        long visibleEnd = _visibleEnd;
        foreach (DirectMidiNoteValue note in _notes.QueryValues(startTick, endTick))
        {
            long noteEnd = note.StartTick > long.MaxValue - Math.Max(1, note.LengthTicks)
                ? long.MaxValue
                : note.StartTick + Math.Max(1, note.LengthTicks);
            long clippedStart = Math.Max(visibleStart, note.StartTick);
            long clippedEnd = Math.Min(visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            visitor(new(
                (clippedStart - visibleStart) / (double)_length,
                (clippedEnd - visibleStart) / (double)_length,
                Math.Clamp(note.Key, 0, 127)));
        }
    }

    public void VisitEvents(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (endTick <= startTick) return;
        long visibleStart = _visibleStart;
        foreach (DirectMidiChannelEventValue value in _channelEvents.QueryValues(startTick, endTick))
        {
            if (value.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff)
                continue;
            visitor(new(
                (value.Tick - visibleStart) / (double)_length,
                Normalize(value)));
        }
        foreach (OpaqueMidiEventValue value in _opaqueEvents.QueryValues(startTick, endTick))
        {
            visitor(new(
                (value.Tick - visibleStart) / (double)_length,
                1));
        }
    }

    public ulong GetTileContentFingerprint(
        bool eventLayer,
        double deviceSegmentWidth,
        long tileX)
    {
        double normalizedStart = Math.Max(
            0,
            (tileX * TimelineSegmentPreviewRasterizer.TileSize - 1d) / deviceSegmentWidth);
        double normalizedEnd = Math.Min(
            Math.BitIncrement(1d),
            ((tileX + 1d) * TimelineSegmentPreviewRasterizer.TileSize + 1d)
            / deviceSegmentWidth);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        if (eventLayer)
        {
            return TimelineContentFingerprint.Combine(
                _channelEvents.GetRangeFingerprint(startTick, Math.Max(startTick + 1, endTick)),
                _opaqueEvents.GetRangeFingerprint(startTick, Math.Max(startTick + 1, endTick)));
        }
        return _notes.GetRangeFingerprint(startTick, Math.Max(startTick + 1, endTick));
    }

    private (long StartTick, long EndTick) ToContentRange(double normalizedStart, double normalizedEnd)
    {
        double clampedStart = Math.Clamp(normalizedStart, 0, 1);
        double clampedEnd = Math.Clamp(normalizedEnd, 0, 1);
        long start = checked(_visibleStart
            + (long)Math.Floor(clampedStart * _length));
        long end = checked(_visibleStart
            + (long)Math.Ceiling(clampedEnd * _length));
        return (
            Math.Clamp(start, _visibleStart, _visibleEnd),
            Math.Clamp(end, _visibleStart, _visibleEnd));
    }

    private static double Normalize(DirectMidiChannelEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PolyphonicKeyPressure => value.Data2 / 127d,
        DirectMidiChannelEventKind.ControlChange => value.Data2 / 127d,
        DirectMidiChannelEventKind.ProgramChange => value.Data1 / 127d,
        DirectMidiChannelEventKind.ChannelPressure => value.Data1 / 127d,
        DirectMidiChannelEventKind.PitchBend => ((value.Data2 << 7) | value.Data1) / 16383d,
        _ => value.Data2 / 127d
    };
}

internal enum DirectMidiTimelineProjection
{
    Notes,
    Velocities,
    ChannelEvents,
    OpaqueEvents
}

internal sealed class PagedDirectMidiTimelineItemSource :
    IPreparedTimelineRenderItemSource,
    INonBlockingTimelineFingerprintSource,
    ITimelineRasterAggregateSource
{
    private readonly MidiSegment _segment;
    private readonly DirectMidiTimelineProjection _projection;
    private readonly DirectMidiEventLaneTarget? _eventTarget;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;
    private readonly long _count;
    private readonly DirectMidiNoteQuerySnapshot? _noteSnapshot;
    private readonly DirectMidiChannelEventQuerySnapshot? _channelEventSnapshot;
    private readonly OpaqueMidiEventQuerySnapshot? _opaqueEventSnapshot;
    private readonly TimelineEventTargetLaneIndex? _eventIndex;
    private readonly ulong _contentFingerprint;
    private readonly long _maximumEndTick;

    public PagedDirectMidiTimelineItemSource(
        MidiSegment segment,
        DirectMidiTimelineProjection projection,
        DirectMidiEventLaneTarget? eventTarget = null,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null,
        DirectMidiNoteQuerySnapshot? noteSnapshot = null,
        DirectMidiChannelEventQuerySnapshot? channelEventSnapshot = null,
        OpaqueMidiEventQuerySnapshot? opaqueEventSnapshot = null,
        TimelineEventTargetLaneIndex? eventIndex = null)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _projection = projection;
        _eventTarget = eventTarget;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
        _noteSnapshot = projection is DirectMidiTimelineProjection.Notes
            or DirectMidiTimelineProjection.Velocities
                ? noteSnapshot ?? segment.Notes.CreateQuerySnapshot()
                : null;
        _channelEventSnapshot = projection == DirectMidiTimelineProjection.ChannelEvents
            ? channelEventSnapshot ?? segment.ChannelEvents.CreateQuerySnapshot()
            : null;
        _opaqueEventSnapshot = projection == DirectMidiTimelineProjection.OpaqueEvents
            ? opaqueEventSnapshot ?? segment.OpaqueEvents.CreateQuerySnapshot()
            : null;
        _eventIndex = projection == DirectMidiTimelineProjection.ChannelEvents
            ? eventIndex
            : null;
        _maximumEndTick = projection is DirectMidiTimelineProjection.Notes
            or DirectMidiTimelineProjection.Velocities
                ? _noteSnapshot!.MaximumEndTick
                : projection == DirectMidiTimelineProjection.ChannelEvents
                    && _eventIndex is not null
                        ? Math.Max(segment.ContentEndTick, _eventIndex.MaximumEndTick)
                        : segment.ContentEndTick;
        _count = projection switch
        {
            DirectMidiTimelineProjection.Notes or DirectMidiTimelineProjection.Velocities =>
                _noteSnapshot!.Count,
            DirectMidiTimelineProjection.OpaqueEvents => segment.OpaqueEvents.Count,
            DirectMidiTimelineProjection.ChannelEvents => eventTarget is null
                ? 0
                : _eventIndex?.Count ?? segment.ChannelEvents.Count,
            _ => 0
        };
        _contentFingerprint = PureMidiPresentationFingerprint.Create(
            _segment.PagedContentFingerprint,
            _noteSnapshot?.Generation ?? segment.Notes.Generation,
            _channelEventSnapshot?.Generation ?? segment.ChannelEvents.Generation,
            _opaqueEventSnapshot?.Generation ?? segment.OpaqueEvents.Generation,
            _segment.ContentOffsetTick,
            _segment.LengthTicks,
            (long)_projection,
            _eventTarget is null ? -1 : (long)_eventTarget.Value.Kind,
            _eventTarget?.Data1 ?? -1);
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    public long Count => _count;
    public long MaximumEndTick => _maximumEndTick;
    public ulong ContentFingerprint => _contentFingerprint;

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (_projection == DirectMidiTimelineProjection.Notes)
        {
            int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
            return maximumKey < minimumKey
                ? 0
                : _noteSnapshot!.GetRangeFingerprint(
                    startTick,
                    endTick,
                    minimumKey,
                    maximumKey);
        }
        if (_projection == DirectMidiTimelineProjection.Velocities)
            return _noteSnapshot!.GetRangeFingerprint(startTick, endTick);
        if (_projection == DirectMidiTimelineProjection.ChannelEvents)
            return _eventIndex?.GetRangeFingerprint(startTick, endTick)
                ?? _channelEventSnapshot!.GetRangeFingerprint(startTick, endTick);
        if (_projection == DirectMidiTimelineProjection.OpaqueEvents)
            return _opaqueEventSnapshot!.GetRangeFingerprint(startTick, endTick);
        return 0;
    }

    public bool TryAccumulateRasterColumns(
        TimelineRasterAggregateKind kind,
        TimelineRasterColumnProjection projection,
        int firstLane,
        int lastLaneExclusive,
        Span<TimelineRasterColumnSummary> destination,
        out int sourceWorkCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceWorkCount = 0;
        if (_projection == DirectMidiTimelineProjection.ChannelEvents
            && kind == TimelineRasterAggregateKind.EventPoints
            && _eventIndex is not null)
        {
            sourceWorkCount = _eventIndex.AccumulateRasterColumns(
                projection,
                destination,
                cancellationToken);
            return true;
        }
        if (_projection is not (DirectMidiTimelineProjection.Notes
                or DirectMidiTimelineProjection.Velocities)
            || kind is not (TimelineRasterAggregateKind.PianoNotes
                or TimelineRasterAggregateKind.Velocity))
        {
            return false;
        }
        TimelineRasterColumnSummary[] raw = new TimelineRasterColumnSummary[destination.Length];
        int minimumKey = kind == TimelineRasterAggregateKind.PianoNotes
            ? Math.Clamp(128 - lastLaneExclusive, 0, 127)
            : 0;
        int maximumKey = kind == TimelineRasterAggregateKind.PianoNotes
            ? Math.Clamp(127 - firstLane, 0, 127)
            : 127;
        if (maximumKey < minimumKey) return true;
        if (!_noteSnapshot!.TryAccumulateRasterColumns(
                projection,
                minimumKey,
                maximumKey,
                raw,
                out sourceWorkCount,
                cancellationToken))
        {
            return false;
        }
        PagedLogicalNoteTimelineItemSource.MergeRasterColumns(raw, destination, kind);
        return true;
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                {
                    int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
                    int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
                    if (maximumKey < minimumKey) return;
                    foreach (DirectMidiNoteValue note in _noteSnapshot!.QueryValues(
                        startTick,
                        endTick,
                        minimumKey,
                        maximumKey))
                    {
                        destination.Add(ToNoteItem(note));
                    }
                    break;
                }
            case DirectMidiTimelineProjection.Velocities:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (DirectMidiNoteValue note in _noteSnapshot!.QueryValues(startTick, endTick))
                    destination.Add(ToVelocityItem(note));
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0 || _eventTarget is null) return;
                if (_eventIndex is not null)
                {
                    _eventIndex.Visit(
                        startTick,
                        endTick,
                        value => destination.Add(ToEventItem(value)));
                    break;
                }
                foreach (DirectMidiChannelEventValue value in _channelEventSnapshot!.QueryValues(startTick, endTick))
                {
                    if (ToLaneTarget(value) == _eventTarget.Value)
                        destination.Add(ToEventItem(value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (OpaqueMidiEventValue value in _opaqueEventSnapshot!.QueryValues(startTick, endTick))
                    destination.Add(ToOpaqueItem(value));
                break;
        }
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                {
                    int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
                    int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
                    if (maximumKey < minimumKey) return;
                    foreach (DirectMidiNoteValue note in _noteSnapshot!.QueryValues(
                        startTick,
                        endTick,
                        minimumKey,
                        maximumKey))
                    {
                        visitor(ToNoteItem(note));
                    }
                    break;
                }
            case DirectMidiTimelineProjection.Velocities:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (DirectMidiNoteValue note in _noteSnapshot!.QueryValues(startTick, endTick))
                    visitor(ToVelocityItem(note));
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0 || _eventTarget is null) return;
                if (_eventIndex is not null)
                {
                    _eventIndex.Visit(startTick, endTick, value => visitor(ToEventItem(value)));
                    break;
                }
                foreach (DirectMidiChannelEventValue value in _channelEventSnapshot!.QueryValues(startTick, endTick))
                {
                    if (ToLaneTarget(value) == _eventTarget.Value) visitor(ToEventItem(value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                if (firstLane > 0 || lastLaneExclusive <= 0) return;
                foreach (OpaqueMidiEventValue value in _opaqueEventSnapshot!.QueryValues(startTick, endTick))
                    visitor(ToOpaqueItem(value));
                break;
        }
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        HashSet<MidoraId> requested = [id];
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                DirectMidiNoteValue note = _noteSnapshot!.ResolveByIds(requested).FirstOrDefault();
                if (note.Id != default)
                {
                    item = ToNoteItem(note);
                    return true;
                }
                break;
            case DirectMidiTimelineProjection.Velocities:
                DirectMidiNoteValue velocityNote = _noteSnapshot!.ResolveByIds(requested).FirstOrDefault();
                if (velocityNote.Id != default)
                {
                    item = ToVelocityItem(velocityNote);
                    return true;
                }
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                DirectMidiChannelEventValue value =
                    _channelEventSnapshot!.ResolveByIds(requested).FirstOrDefault();
                if (value.Id != default
                    && _eventTarget is not null
                    && ToLaneTarget(value) == _eventTarget.Value)
                {
                    item = ToEventItem(value);
                    return true;
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                OpaqueMidiEventValue opaque =
                    _opaqueEventSnapshot!.ResolveByIds(requested).FirstOrDefault();
                if (opaque.Id != default)
                {
                    item = ToOpaqueItem(opaque);
                    return true;
                }
                break;
        }
        item = default;
        return false;
    }

    public void QueryByIds(
        IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                foreach (DirectMidiNoteValue value in _noteSnapshot!.ResolveByIds(ids))
                    destination.Add(ToNoteItem(value));
                break;
            case DirectMidiTimelineProjection.Velocities:
                foreach (DirectMidiNoteValue value in _noteSnapshot!.ResolveByIds(ids))
                    destination.Add(ToVelocityItem(value));
                break;
            case DirectMidiTimelineProjection.ChannelEvents when _eventTarget is DirectMidiEventLaneTarget target:
                foreach (DirectMidiChannelEventValue value in _channelEventSnapshot!.ResolveByIds(ids))
                {
                    if (ToLaneTarget(value) == target)
                        destination.Add(ToEventItem(value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                foreach (OpaqueMidiEventValue value in _opaqueEventSnapshot!.ResolveByIds(ids))
                    destination.Add(ToOpaqueItem(value));
                break;
        }
    }

    public void VisitByIds(
        IReadOnlySet<MidoraId> ids,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(visitor);
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                foreach (DirectMidiNoteValue value in _noteSnapshot!.ResolveByIds(ids))
                    visitor(ToNoteItem(value));
                break;
            case DirectMidiTimelineProjection.Velocities:
                foreach (DirectMidiNoteValue value in _noteSnapshot!.ResolveByIds(ids))
                    visitor(ToVelocityItem(value));
                break;
            case DirectMidiTimelineProjection.ChannelEvents
                when _eventTarget is DirectMidiEventLaneTarget target:
                foreach (DirectMidiChannelEventValue value in _channelEventSnapshot!.ResolveByIds(ids))
                {
                    if (ToLaneTarget(value) == target) visitor(ToEventItem(value));
                }
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                foreach (OpaqueMidiEventValue value in _opaqueEventSnapshot!.ResolveByIds(ids))
                    visitor(ToOpaqueItem(value));
                break;
        }
    }

    public bool TryQueryIntoCached(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return true;
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                {
                    int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
                    int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
                    if (maximumKey < minimumKey) return true;
                    List<DirectMidiNoteValue> values = [];
                    if (!_noteSnapshot!.TryQueryValuesCached(
                            startTick,
                            endTick,
                            minimumKey,
                            maximumKey,
                            values))
                    {
                        return false;
                    }
                    foreach (DirectMidiNoteValue value in values)
                        destination.Add(ToNoteItem(value));
                    return true;
                }
            case DirectMidiTimelineProjection.Velocities:
                {
                    if (firstLane > 0 || lastLaneExclusive <= 0) return true;
                    List<DirectMidiNoteValue> values = [];
                    if (!_noteSnapshot!.TryQueryValuesCached(
                            startTick,
                            endTick,
                            0,
                            127,
                            values))
                    {
                        return false;
                    }
                    foreach (DirectMidiNoteValue value in values)
                        destination.Add(ToVelocityItem(value));
                    return true;
                }
            case DirectMidiTimelineProjection.ChannelEvents:
                {
                    if (firstLane > 0 || lastLaneExclusive <= 0 || _eventTarget is null)
                        return true;
                    if (_eventIndex is not null)
                    {
                        _eventIndex.Visit(
                            startTick,
                            endTick,
                            value => destination.Add(ToEventItem(value)));
                        return true;
                    }
                    List<DirectMidiChannelEventValue> values = [];
                    if (!_channelEventSnapshot!.TryQueryValuesCached(startTick, endTick, values))
                        return false;
                    foreach (DirectMidiChannelEventValue value in values)
                    {
                        if (ToLaneTarget(value) == _eventTarget.Value)
                            destination.Add(ToEventItem(value));
                    }
                    return true;
                }
            case DirectMidiTimelineProjection.OpaqueEvents:
                {
                    if (firstLane > 0 || lastLaneExclusive <= 0) return true;
                    List<OpaqueMidiEventValue> values = [];
                    if (!_opaqueEventSnapshot!.TryQueryValuesCached(startTick, endTick, values))
                        return false;
                    foreach (OpaqueMidiEventValue value in values)
                        destination.Add(ToOpaqueItem(value));
                    return true;
                }
            default:
                return true;
        }
    }

    public void PrefetchRange(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        CancellationToken cancellationToken)
    {
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
                {
                    int minimumKey = Math.Clamp(128 - lastLaneExclusive, 0, 127);
                    int maximumKey = Math.Clamp(127 - firstLane, 0, 127);
                    if (maximumKey >= minimumKey)
                    {
                        _noteSnapshot!.PrefetchRange(
                            startTick,
                            endTick,
                            minimumKey,
                            maximumKey,
                            cancellationToken);
                    }
                    break;
                }
            case DirectMidiTimelineProjection.Velocities:
                if (firstLane <= 0 && lastLaneExclusive > 0)
                    _noteSnapshot!.PrefetchRange(startTick, endTick, 0, 127, cancellationToken);
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                if (_eventIndex is null
                    && firstLane <= 0
                    && lastLaneExclusive > 0
                    && _eventTarget is not null)
                    _channelEventSnapshot!.PrefetchRange(startTick, endTick, cancellationToken);
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                if (firstLane <= 0 && lastLaneExclusive > 0)
                    _opaqueEventSnapshot!.PrefetchRange(startTick, endTick, cancellationToken);
                break;
        }
    }

    public bool TryQueryByIdsCached(
        IReadOnlySet<MidoraId> ids,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(destination);
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
            case DirectMidiTimelineProjection.Velocities:
                {
                    List<DirectMidiNoteValue> values = [];
                    if (!_noteSnapshot!.TryQueryByIdsCached(ids, values)) return false;
                    foreach (DirectMidiNoteValue value in values)
                    {
                        destination.Add(_projection == DirectMidiTimelineProjection.Notes
                            ? ToNoteItem(value)
                            : ToVelocityItem(value));
                    }
                    return true;
                }
            case DirectMidiTimelineProjection.ChannelEvents:
                {
                    if (_eventTarget is null) return true;
                    List<DirectMidiChannelEventValue> values = [];
                    if (!_channelEventSnapshot!.TryQueryByIdsCached(ids, values)) return false;
                    foreach (DirectMidiChannelEventValue value in values)
                    {
                        if (ToLaneTarget(value) == _eventTarget.Value)
                            destination.Add(ToEventItem(value));
                    }
                    return true;
                }
            case DirectMidiTimelineProjection.OpaqueEvents:
                {
                    List<OpaqueMidiEventValue> values = [];
                    if (!_opaqueEventSnapshot!.TryQueryByIdsCached(ids, values)) return false;
                    foreach (OpaqueMidiEventValue value in values)
                        destination.Add(ToOpaqueItem(value));
                    return true;
                }
            default:
                return true;
        }
    }

    public void PrefetchIds(
        IReadOnlySet<MidoraId> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        switch (_projection)
        {
            case DirectMidiTimelineProjection.Notes:
            case DirectMidiTimelineProjection.Velocities:
                _noteSnapshot!.PrefetchIds(ids, cancellationToken);
                break;
            case DirectMidiTimelineProjection.ChannelEvents:
                _channelEventSnapshot!.PrefetchIds(ids, cancellationToken);
                break;
            case DirectMidiTimelineProjection.OpaqueEvents:
                _opaqueEventSnapshot!.PrefetchIds(ids, cancellationToken);
                break;
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() => _projection switch
    {
        DirectMidiTimelineProjection.Notes =>
            _noteSnapshot!.QueryValues(0, long.MaxValue).Select(ToNoteItem),
        DirectMidiTimelineProjection.Velocities =>
            _noteSnapshot!.QueryValues(0, long.MaxValue).Select(ToVelocityItem),
        DirectMidiTimelineProjection.ChannelEvents when _eventTarget is DirectMidiEventLaneTarget target =>
            _eventIndex is not null
                ? _eventIndex.Query(0, long.MaxValue).Select(ToEventItem)
                : _channelEventSnapshot!.QueryValues(0, long.MaxValue)
                    .Where(value => ToLaneTarget(value) == target)
                    .Select(ToEventItem),
        DirectMidiTimelineProjection.OpaqueEvents =>
            _opaqueEventSnapshot!.QueryValues(0, long.MaxValue).Select(ToOpaqueItem),
        _ => []
    };

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (_projection != DirectMidiTimelineProjection.Notes
            || extent <= 0
            || destination.IsEmpty)
        {
            return;
        }

        Span<byte> occupiedColumns = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        _segment.Notes.AccumulateOverviewColumns(extent, occupiedColumns);
        for (int x = 0; x < occupiedColumns.Length; x++)
        {
            if (occupiedColumns[x] != 0 && destination[x] < int.MaxValue)
                destination[x]++;
        }
    }

    private TimelineRenderItem ToNoteItem(DirectMidiNote note) => new(
        note.Id,
        TimelineItemKind.DirectMidiNote,
        note.StartTick,
        checked(note.StartTick + note.LengthTicks),
        127 - Math.Clamp(note.Key, 0, 127),
        note.NoteOnVelocity,
        1,
        State(note.Id, note.StartTick, note.Key is < 0 or > 127));

    private TimelineRenderItem ToNoteItem(DirectMidiNoteValue note) => new(
        note.Id,
        TimelineItemKind.DirectMidiNote,
        note.StartTick,
        checked(note.StartTick + note.LengthTicks),
        127 - Math.Clamp(note.Key, 0, 127),
        note.NoteOnVelocity,
        1,
        State(note.Id, note.StartTick, note.Key is < 0 or > 127));

    private TimelineRenderItem ToVelocityItem(DirectMidiNote note) => new(
        note.Id,
        TimelineItemKind.Velocity,
        note.StartTick,
        checked(note.StartTick + 1),
        0,
        note.NoteOnVelocity / 127d,
        note.Key,
        State(note.Id, note.StartTick, invalid: false));

    private TimelineRenderItem ToVelocityItem(DirectMidiNoteValue note) => new(
        note.Id,
        TimelineItemKind.Velocity,
        note.StartTick,
        checked(note.StartTick + 1),
        0,
        note.NoteOnVelocity / 127d,
        note.Key,
        State(note.Id, note.StartTick, invalid: false));

    private TimelineRenderItem ToEventItem(DirectMidiChannelEvent value) => new(
        value.Id,
        TimelineItemKind.DirectMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        TimelineWorkspaceViewModel.NormalizeDirectMidiEventValue(value),
        1,
        State(value.Id, value.Tick, invalid: false));

    private TimelineRenderItem ToEventItem(DirectMidiChannelEventValue value) => new(
        value.Id,
        TimelineItemKind.DirectMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        Normalize(value),
        1,
        State(value.Id, value.Tick, invalid: false));

    private TimelineRenderItem ToEventItem(TimelineEventTargetPoint value) => new(
        value.Id,
        TimelineItemKind.DirectMidiEvent,
        value.Tick,
        value.Tick == long.MaxValue ? long.MaxValue : value.Tick + 1,
        0,
        value.Value,
        1,
        State(value.Id, value.Tick, invalid: false));

    private TimelineRenderItem ToOpaqueItem(OpaqueMidiEvent value) => new(
        value.Id,
        TimelineItemKind.OpaqueMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        1,
        1,
        State(value.Id, value.Tick, invalid: false))
    {
        Label = TimelineWorkspaceViewModel.OpaqueMidiEventLabel(value)
    };

    private TimelineRenderItem ToOpaqueItem(OpaqueMidiEventValue value) => new(
        value.Id,
        TimelineItemKind.OpaqueMidiEvent,
        value.Tick,
        checked(value.Tick + 1),
        0,
        1,
        1,
        State(value.Id, value.Tick, invalid: false));

    private static DirectMidiEventLaneTarget ToLaneTarget(DirectMidiChannelEventValue value) => new(
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
            ? value.Data1
            : 0);

    private static double Normalize(DirectMidiChannelEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PolyphonicKeyPressure => value.Data2 / 127d,
        DirectMidiChannelEventKind.ControlChange => value.Data2 / 127d,
        DirectMidiChannelEventKind.ProgramChange => value.Data1 / 127d,
        DirectMidiChannelEventKind.ChannelPressure => value.Data1 / 127d,
        DirectMidiChannelEventKind.PitchBend => ((value.Data2 << 7) | value.Data1) / 16383d,
        _ => value.Data2 / 127d
    };

    private TimelineItemState State(MidoraId id, long tick, bool invalid)
    {
        TimelineItemState state = tick < _segment.ContentOffsetTick || tick >= _segment.ContentEndTick
            ? TimelineItemState.OutsideActiveRange
            : TimelineItemState.None;
        if (invalid) state |= TimelineItemState.Invalid;
        if (_selectedIds.Contains(id)) state |= TimelineItemState.Selected;
        if (_primaryId == id) state |= TimelineItemState.Primary;
        return state;
    }
}

/// <summary>
/// Exact, device-column-bounded overview projection for a complete Direct MIDI
/// Segment. Paged sources use their ordered endpoint indexes and may only use a
/// page range without decoding when the complete page maps to one output column.
/// Collection generations keep the surface cache coherent with copy-on-write edits.
/// </summary>
internal sealed class PureMidiSegmentOverviewSource : ITimelineOverviewSource
{
    private readonly MidiSegment _segment;

    public PureMidiSegmentOverviewSource(MidiSegment segment)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        MaximumEndTick = ResolveMaximumEndTick(segment);
    }

    public long MaximumEndTick { get; }

    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        _segment.PagedContentFingerprint,
        _segment.Notes.Generation,
        _segment.ChannelEvents.Generation,
        _segment.OpaqueEvents.Generation,
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        0x4f56455256494557);

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (noteStartColumns.Length != eventColumns.Length)
            throw new ArgumentException("Timeline overview channels must have equal widths.");
        if (noteStartColumns.IsEmpty) return;

        _segment.Notes.AccumulateOverviewColumns(extent, noteStartColumns);
        _segment.ChannelEvents.AccumulateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        _segment.OpaqueEvents.AccumulateOverviewColumns(extent, eventColumns);
    }

    private static long ResolveMaximumEndTick(MidiSegment segment)
    {
        long maximum = segment.ContentEndTick;
        foreach (PureMidiContentRangeSummary summary in segment.Notes.GetOverviewRangeSummaries()
            .Concat(segment.ChannelEvents.GetOverviewRangeSummaries())
            .Concat(segment.OpaqueEvents.GetOverviewRangeSummaries()))
        {
            long end = summary.MaximumTick == long.MaxValue
                ? long.MaxValue
                : summary.MaximumTick + 1;
            maximum = Math.Max(maximum, end);
        }
        return maximum;
    }
}

internal static class PureMidiPresentationFingerprint
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Create(string? text, params long[] values)
    {
        ulong hash = Offset;
        if (text is not null)
        {
            foreach (byte value in Encoding.UTF8.GetBytes(text))
            {
                hash ^= value;
                hash *= Prime;
            }
        }
        foreach (long value in values)
        {
            hash ^= unchecked((ulong)value);
            hash *= Prime;
        }
        return hash;
    }
}
