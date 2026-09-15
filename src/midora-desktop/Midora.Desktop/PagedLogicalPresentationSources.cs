using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

internal static class TimelinePresentationPaging
{
    public const long MaterializedItemThreshold = 4096;

    public static (TimelineRenderItem[] Items, ITimelineRenderItemSource? Source) Adapt(
        ITimelineRenderItemSource? source)
    {
        if (source is null) return ([], null);
        if (source is IPreparedPagedTimelineItemSource { HasExternalValueStorage: true }) return ([], source);
        return source.Count <= MaterializedItemThreshold
            ? (source.EnumerateAll().ToArray(), null)
            : ([], source);
    }
}

internal enum LogicalNoteTimelineProjection
{
    Notes,
    Velocities
}

internal sealed class PagedLogicalNoteTimelineItemSource :
    IPreparedPagedTimelineItemSource,
    INonBlockingTimelineFingerprintSource,
    ITimelineRasterAggregateSource
{
    private readonly Segment _segment;
    private readonly LogicalNoteCollection _notes;
    private readonly LogicalNoteQuerySnapshot _snapshot;
    private readonly LogicalNoteTimelineProjection _projection;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;

    public PagedLogicalNoteTimelineItemSource(
        Segment segment,
        LogicalNoteTimelineProjection projection,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null,
        LogicalNoteQuerySnapshot? snapshot = null)
    {
        _segment = segment ?? throw new ArgumentNullException(nameof(segment));
        _notes = segment.Notes;
        _projection = projection;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
        _snapshot = snapshot ?? segment.Notes.CreateQuerySnapshot();
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    public long Count => _snapshot.Count;
    public bool HasExternalValueStorage => _snapshot.UsesExternalStorage;
    public bool CanComputeRangeFingerprintWithoutBlocking => !_snapshot.UsesExternalStorage;
    public void PrefetchRange(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        CancellationToken cancellationToken)
    {
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        int minimum = _projection == LogicalNoteTimelineProjection.Notes ? Math.Clamp(128 - lastLaneExclusive, 0, 127) : 0;
        int maximum = _projection == LogicalNoteTimelineProjection.Notes ? Math.Clamp(127 - firstLane, 0, 127) : 127;
        if (maximum >= minimum) _snapshot.Prefetch(new(startTick, endTick, minimum, maximum), cancellationToken);
    }
    public long MaximumEndTick => _snapshot.MaximumEndTick;
    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        null,
        unchecked((long)_snapshot.ContentFingerprint),
        _segment.ContentOffsetTick,
        _segment.LengthTicks,
        (long)_projection);

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (_projection == LogicalNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            return maximumNote < minimumNote
                ? 0
                : _snapshot.GetRangeFingerprint(
                    startTick,
                    endTick,
                    minimumNote,
                    maximumNote);
        }
        return _snapshot.GetRangeFingerprint(startTick, endTick);
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
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
        if (_projection == LogicalNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            if (maximumNote < minimumNote) return;
            foreach (LogicalNoteSnapshotValue value in _snapshot.QueryValues(
                startTick,
                endTick,
                minimumNote,
                maximumNote))
            {
                visitor(ToNoteItem(value));
            }
            return;
        }

        if (firstLane > 0 || lastLaneExclusive <= 0) return;
        foreach (LogicalNoteSnapshotValue value in _snapshot.QueryValues(startTick, endTick))
            visitor(ToVelocityItem(value));
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (_snapshot.TryFindOrdinalById(id, out int ordinal))
        {
            LogicalNoteSnapshotValue value = _snapshot.GetByOrdinal(ordinal);
            item = _projection == LogicalNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value);
            return true;
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
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out TimelineRenderItem value)) destination.Add(value);
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() =>
        _snapshot.EnumerateAll().Select(value =>
            _projection == LogicalNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value));

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (_projection != LogicalNoteTimelineProjection.Notes
            || extent <= 0
            || destination.IsEmpty)
        {
            return;
        }
        Span<byte> occupied = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        _snapshot.AccumulateStartColumns(extent, occupied);
        for (int index = 0; index < occupied.Length; index++)
        {
            if (occupied[index] != 0 && destination[index] < int.MaxValue)
                destination[index]++;
        }
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
        if (kind is not (TimelineRasterAggregateKind.PianoNotes
            or TimelineRasterAggregateKind.Velocity))
        {
            return false;
        }
        TimelineRasterColumnSummary[] raw = new TimelineRasterColumnSummary[destination.Length];
        int minimumNote = kind == TimelineRasterAggregateKind.PianoNotes
            ? Math.Clamp(128 - lastLaneExclusive, 0, 127)
            : 0;
        int maximumNote = kind == TimelineRasterAggregateKind.PianoNotes
            ? Math.Clamp(127 - firstLane, 0, 127)
            : 127;
        if (maximumNote < minimumNote) return true;
        sourceWorkCount = _snapshot.AccumulateRasterColumns(
            projection,
            minimumNote,
            maximumNote,
            raw,
            cancellationToken);
        MergeRasterColumns(raw, destination, kind);
        return true;
    }

    internal static void MergeRasterColumns(
        ReadOnlySpan<TimelineRasterColumnSummary> source,
        Span<TimelineRasterColumnSummary> destination,
        TimelineRasterAggregateKind kind)
    {
        for (int column = 0; column < source.Length; column++)
        {
            TimelineRasterColumnSummary value = source[column];
            if (!value.HasContent) continue;
            if (kind == TimelineRasterAggregateKind.Velocity)
            {
                destination[column].Include(
                    1,
                    0,
                    value.MinimumValue,
                    value.MaximumValue,
                    value.ApproximateSourceCount);
                continue;
            }
            ulong laneMaskLow = 0;
            ulong laneMaskHigh = 0;
            ulong startLaneMaskLow = 0;
            ulong startLaneMaskHigh = 0;
            ulong endLaneMaskLow = 0;
            ulong endLaneMaskHigh = 0;
            for (int note = 0; note < 128; note++)
            {
                bool occupied = note < 64
                    ? (value.LaneMaskLow & (1UL << note)) != 0
                    : (value.LaneMaskHigh & (1UL << (note - 64))) != 0;
                bool starts = note < 64
                    ? (value.StartLaneMaskLow & (1UL << note)) != 0
                    : (value.StartLaneMaskHigh & (1UL << (note - 64))) != 0;
                bool ends = note < 64
                    ? (value.EndLaneMaskLow & (1UL << note)) != 0
                    : (value.EndLaneMaskHigh & (1UL << (note - 64))) != 0;
                if (!occupied && !starts && !ends) continue;
                int lane = 127 - note;
                if (lane < 64)
                {
                    if (occupied) laneMaskLow |= 1UL << lane;
                    if (starts) startLaneMaskLow |= 1UL << lane;
                    if (ends) endLaneMaskLow |= 1UL << lane;
                }
                else
                {
                    if (occupied) laneMaskHigh |= 1UL << (lane - 64);
                    if (starts) startLaneMaskHigh |= 1UL << (lane - 64);
                    if (ends) endLaneMaskHigh |= 1UL << (lane - 64);
                }
            }
            destination[column].Include(
                laneMaskLow,
                laneMaskHigh,
                value.MinimumValue,
                value.MaximumValue,
                value.ApproximateSourceCount);
            destination[column].IncludeStartBoundary(
                startLaneMaskLow,
                startLaneMaskHigh);
            destination[column].IncludeEndBoundary(
                endLaneMaskLow,
                endLaneMaskHigh);
        }
    }

    private TimelineRenderItem ToNoteItem(LogicalNote value) => new(
        value.Id,
        TimelineItemKind.LogicalNote,
        value.StartTick,
        checked(value.StartTick + value.LengthTicks),
        127 - Math.Clamp(value.Note, 0, 127),
        value.Velocity,
        1,
        State(value.Id, value.StartTick, value.Note is < 0 or > 127));

    private TimelineRenderItem ToNoteItem(LogicalNoteSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.LogicalNote,
        value.StartTick,
        checked(value.StartTick + value.LengthTicks),
        127 - Math.Clamp(value.Note, 0, 127),
        value.Velocity,
        1,
        State(value.Id, value.StartTick, value.Note is < 0 or > 127));

    private TimelineRenderItem ToVelocityItem(LogicalNote value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.StartTick,
        checked(value.StartTick + 1),
        0,
        value.Velocity / 127d,
        value.Note,
        State(value.Id, value.StartTick, invalid: false));

    private TimelineRenderItem ToVelocityItem(LogicalNoteSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.StartTick,
        checked(value.StartTick + 1),
        0,
        value.Velocity / 127d,
        value.Note,
        State(value.Id, value.StartTick, invalid: false));

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

internal readonly record struct LogicalSegmentPreviewEventLane(
    CurvePointQuerySnapshot Snapshot,
    double Minimum,
    double Maximum)
{
    public double Normalize(double value) => Maximum <= Minimum
        ? 0.5
        : Math.Clamp((value - Minimum) / (Maximum - Minimum), 0, 1);
}

internal sealed class LogicalSegmentPreviewSource : ITimelineSegmentPreviewSource
{
    private readonly LogicalNoteQuerySnapshot _notes;
    private readonly LogicalSegmentPreviewEventLane[] _eventLanes;
    private readonly long _visibleStart;
    private readonly long _visibleEnd;
    private readonly long _length;

    public LogicalSegmentPreviewSource(
        Segment segment,
        LogicalNoteQuerySnapshot notes,
        IEnumerable<LogicalSegmentPreviewEventLane> eventLanes)
    {
        ArgumentNullException.ThrowIfNull(segment);
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        ArgumentNullException.ThrowIfNull(eventLanes);
        _eventLanes = eventLanes.ToArray();
        _visibleStart = segment.ContentOffsetTick;
        _visibleEnd = segment.ContentEndTick;
        _length = segment.LengthTicks;
        NoteContentFingerprint = PureMidiPresentationFingerprint.Create(
            null,
            unchecked((long)notes.ContentFingerprint),
            _visibleStart,
            _length,
            0x4c4f474943414c4e);
        ulong eventFingerprint = 0;
        foreach (LogicalSegmentPreviewEventLane lane in _eventLanes)
        {
            eventFingerprint = TimelineContentFingerprint.Combine(
                eventFingerprint,
                TimelineContentFingerprint.Combine(
                    lane.Snapshot.ContentFingerprint,
                    TimelineContentFingerprint.Combine(
                        unchecked((ulong)BitConverter.DoubleToInt64Bits(lane.Minimum)),
                        unchecked((ulong)BitConverter.DoubleToInt64Bits(lane.Maximum)))));
        }
        EventContentFingerprint = eventFingerprint;
    }

    public bool HasNoteContent => _notes.Count != 0;
    public bool HasEventContent => _eventLanes.Any(static lane => lane.Snapshot.Count != 0);
    public ulong NoteContentFingerprint { get; }
    public ulong EventContentFingerprint { get; }

    public void QueryNotes(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewNote> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitNotes(normalizedStart, normalizedEnd, destination.Add);
    }

    public void QueryEvents(
        double normalizedStart,
        double normalizedEnd,
        List<TimelineSegmentPreviewEvent> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitEvents(normalizedStart, normalizedEnd, destination.Add);
    }

    public void VisitNotes(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewNote> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        foreach (LogicalNoteSnapshotValue note in _notes.QueryValues(
            startTick,
            endTick,
            int.MinValue,
            int.MaxValue))
        {
            long noteEnd = note.StartTick > long.MaxValue - Math.Max(1, note.LengthTicks)
                ? long.MaxValue
                : note.StartTick + Math.Max(1, note.LengthTicks);
            long clippedStart = Math.Max(_visibleStart, note.StartTick);
            long clippedEnd = Math.Min(_visibleEnd, noteEnd);
            if (clippedEnd <= clippedStart) continue;
            visitor(new(
                (clippedStart - _visibleStart) / (double)_length,
                (clippedEnd - _visibleStart) / (double)_length,
                Math.Clamp(note.Note, 0, 127)));
        }
    }

    public void VisitEvents(
        double normalizedStart,
        double normalizedEnd,
        Action<TimelineSegmentPreviewEvent> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        (long startTick, long endTick) = ToContentRange(normalizedStart, normalizedEnd);
        foreach (LogicalSegmentPreviewEventLane lane in _eventLanes)
        {
            foreach (CurvePointSnapshotValue value in lane.Snapshot.QueryValues(startTick, endTick))
            {
                visitor(ToPreviewEvent(value.Tick, lane.Normalize(value.Value)));
            }
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
            ulong fingerprint = 0;
            foreach (LogicalSegmentPreviewEventLane lane in _eventLanes)
            {
                fingerprint = TimelineContentFingerprint.Combine(
                    fingerprint,
                    TimelineContentFingerprint.Combine(
                        lane.Snapshot.GetRangeFingerprint(
                            startTick,
                            Math.Max(startTick + 1, endTick)),
                        TimelineContentFingerprint.Combine(
                            unchecked((ulong)BitConverter.DoubleToInt64Bits(lane.Minimum)),
                            unchecked((ulong)BitConverter.DoubleToInt64Bits(lane.Maximum)))));
            }
            return fingerprint;
        }
        return _notes.GetRangeFingerprint(startTick, Math.Max(startTick + 1, endTick));
    }

    private (long StartTick, long EndTick) ToContentRange(
        double normalizedStart,
        double normalizedEnd)
    {
        long start = checked(_visibleStart
            + (long)Math.Floor(Math.Clamp(normalizedStart, 0, 1) * _length));
        long end = checked(_visibleStart
            + (long)Math.Ceiling(Math.Clamp(normalizedEnd, 0, 1) * _length));
        return (
            Math.Clamp(start, _visibleStart, _visibleEnd),
            Math.Clamp(end, _visibleStart, _visibleEnd));
    }

    private TimelineSegmentPreviewEvent ToPreviewEvent(long tick, double normalizedValue) =>
        new(
            (tick - _visibleStart) / (double)_length,
            Math.Clamp(normalizedValue, 0, 1));
}

internal sealed class PagedLogicalParameterTimelineItemSource :
    IPreparedPagedTimelineItemSource,
    INonBlockingTimelineFingerprintSource,
    ITimelineRasterAggregateSource
{
    private readonly LogicalParameterLane _lane;
    private readonly CurvePointQuerySnapshot _snapshot;
    private readonly double _minimum;
    private readonly double _maximum;
    private readonly bool _broken;
    private readonly long _activeStart;
    private readonly long _activeEnd;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;

    public PagedLogicalParameterTimelineItemSource(
        LogicalParameterLane lane,
        CurvePointQuerySnapshot snapshot,
        double minimum,
        double maximum,
        bool broken,
        long activeStart,
        long activeEnd,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null)
    {
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _minimum = double.IsFinite(minimum) ? minimum : 0;
        _maximum = double.IsFinite(maximum) && maximum > _minimum ? maximum : _minimum + 1;
        _broken = broken;
        _activeStart = activeStart;
        _activeEnd = activeEnd;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    public long Count => _snapshot.Count;
    public bool HasExternalValueStorage => _snapshot.UsesExternalStorage;
    public bool CanComputeRangeFingerprintWithoutBlocking => !_snapshot.UsesExternalStorage;
    public void PrefetchRange(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        CancellationToken cancellationToken)
    {
        if (endTick > startTick && firstLane <= 0 && lastLaneExclusive > 0)
            _snapshot.Prefetch(new(startTick, endTick, 0, 0), cancellationToken);
    }
    public long MaximumEndTick => _snapshot.MaximumTick == long.MaxValue
        ? long.MaxValue
        : _snapshot.MaximumTick + 1;
    public ulong ContentFingerprint => _snapshot.ContentFingerprint;

    public ulong GetRangeFingerprint(long startTick, long endTick, int firstLane, int lastLaneExclusive) =>
        firstLane > 0 || lastLaneExclusive <= 0 || endTick <= startTick
            ? 0
            : _snapshot.GetRangeFingerprint(startTick, endTick);

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination) =>
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        if (endTick <= startTick || firstLane > 0 || lastLaneExclusive <= 0) return;
        foreach (CurvePointSnapshotValue value in _snapshot.QueryValues(startTick, endTick))
            visitor(ToItem(value.Id, value.Tick, value.Value));
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (_snapshot.TryFindOrdinalById(id, out int ordinal))
        {
            CurvePointSnapshotValue point = _snapshot.GetByOrdinal(ordinal);
            item = ToItem(point.Id, point.Tick, point.Value);
            return true;
        }
        item = default;
        return false;
    }

    public void QueryByIds(IReadOnlySet<MidoraId> ids, List<TimelineRenderItem> destination)
    {
        foreach (MidoraId id in ids)
            if (TryGetById(id, out TimelineRenderItem item)) destination.Add(item);
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() =>
        _snapshot.EnumerateAll().Select(value => ToItem(value.Id, value.Tick, value.Value));

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
        if (kind != TimelineRasterAggregateKind.EventPoints) return false;
        TimelineRasterColumnSummary[] raw = new TimelineRasterColumnSummary[destination.Length];
        sourceWorkCount = _snapshot.AccumulateRasterColumns(projection, raw, cancellationToken);
        for (int column = 0; column < raw.Length; column++)
        {
            TimelineRasterColumnSummary summary = raw[column];
            if (!summary.HasContent) continue;
            destination[column].Include(
                1,
                0,
                Normalize(summary.MinimumValue),
                Normalize(summary.MaximumValue),
                summary.ApproximateSourceCount);
        }
        return true;
    }

    private TimelineRenderItem ToItem(MidoraId id, long tick, double value)
    {
        TimelineItemState state = tick < _activeStart || tick >= _activeEnd
            ? TimelineItemState.OutsideActiveRange
            : TimelineItemState.None;
        if (_broken) state |= TimelineItemState.Broken;
        if (_selectedIds.Contains(id)) state |= TimelineItemState.Selected;
        if (_primaryId == id) state |= TimelineItemState.Primary;
        return new(
            id,
            TimelineItemKind.LogicalParameterPoint,
            tick,
            tick == long.MaxValue ? long.MaxValue : tick + 1,
            0,
            Normalize(value),
            1,
            state);
    }

    private double Normalize(double value) => Math.Clamp((value - _minimum) / (_maximum - _minimum), 0, 1);
}

internal sealed class LogicalSegmentOverviewSource : ITimelineOverviewSource
{
    private readonly LogicalNoteQuerySnapshot _notes;
    private readonly CurvePointQuerySnapshot[] _eventSources;

    public LogicalSegmentOverviewSource(
        LogicalNoteQuerySnapshot notes,
        IEnumerable<CurvePointQuerySnapshot> eventSources)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        ArgumentNullException.ThrowIfNull(eventSources);
        _eventSources = eventSources.ToArray();
        long maximumEventEndTick = 0;
        foreach (CurvePointQuerySnapshot source in _eventSources)
        {
            long maximum = source.MaximumTick;
            maximumEventEndTick = maximum == long.MaxValue
                ? long.MaxValue
                : Math.Max(maximumEventEndTick, maximum + 1);
        }
        MaximumEndTick = Math.Max(notes.MaximumEndTick, maximumEventEndTick);
        ulong fingerprint = notes.ContentFingerprint;
        foreach (CurvePointQuerySnapshot source in _eventSources)
            fingerprint = TimelineContentFingerprint.Combine(fingerprint, source.ContentFingerprint);
        ContentFingerprint = fingerprint;
    }

    public ulong ContentFingerprint { get; }
    public long MaximumEndTick { get; }

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        MaterializedTimelineOverviewSource.ValidateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        _notes.AccumulateStartColumns(extent, noteStartColumns);
        foreach (CurvePointQuerySnapshot source in _eventSources)
            source.AccumulateStartColumns(extent, eventColumns);
    }
}

internal enum TemplateNoteTimelineProjection
{
    Notes,
    Velocities
}

internal sealed class PagedTemplateNoteTimelineItemSource :
    IPreparedPagedTimelineItemSource,
    INonBlockingTimelineFingerprintSource,
    ITimelineRasterAggregateSource
{
    private readonly SubVoice _voice;
    private readonly TemplateEventQuerySnapshot _snapshot;
    private readonly TemplateNoteTimelineProjection _projection;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;

    public PagedTemplateNoteTimelineItemSource(
        SubVoice voice,
        TemplateNoteTimelineProjection projection,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null,
        TemplateEventQuerySnapshot? snapshot = null)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _projection = projection;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
        _snapshot = snapshot ?? voice.Events.CreateQuerySnapshot();
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    // The immutable snapshot deliberately stores Note and non-Note template
    // events together. Count is an upper bound used only for capacity hints.
    public long Count => _snapshot.Count;
    public bool HasExternalValueStorage => _snapshot.UsesExternalStorage;
    public bool CanComputeRangeFingerprintWithoutBlocking => !_snapshot.UsesExternalStorage;
    public void PrefetchRange(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        CancellationToken cancellationToken)
    {
        if (endTick <= startTick || lastLaneExclusive <= firstLane) return;
        int minimum = _projection == TemplateNoteTimelineProjection.Notes ? Math.Clamp(128 - lastLaneExclusive, 0, 127) : 0;
        int maximum = _projection == TemplateNoteTimelineProjection.Notes ? Math.Clamp(127 - firstLane, 0, 127) : 127;
        if (maximum >= minimum) _snapshot.Prefetch(new(startTick, endTick, minimum, maximum, 1UL), cancellationToken);
    }
    public long MaximumEndTick => _snapshot.MaximumEndTick;
    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        null,
        unchecked((long)_snapshot.ContentFingerprint),
        (long)_projection,
        0x535542564f494345);

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (_projection == TemplateNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            return maximumNote < minimumNote
                ? 0
                : _snapshot.GetNoteRangeFingerprint(
                    startTick,
                    endTick,
                    minimumNote,
                    maximumNote);
        }
        return _snapshot.GetNoteRangeFingerprint(startTick, endTick);
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
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
        if (_projection == TemplateNoteTimelineProjection.Notes)
        {
            int minimumNote = Math.Clamp(128 - lastLaneExclusive, 0, 127);
            int maximumNote = Math.Clamp(127 - firstLane, 0, 127);
            if (maximumNote < minimumNote) return;
            foreach (TemplateEventSnapshotValue value in _snapshot.QueryNotes(
                startTick,
                endTick,
                minimumNote,
                maximumNote))
            {
                visitor(ToNoteItem(value));
            }
            return;
        }

        if (firstLane > 0 || lastLaneExclusive <= 0) return;
        foreach (TemplateEventSnapshotValue value in _snapshot.QueryNotes(startTick, endTick))
            visitor(ToVelocityItem(value));
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (_snapshot.TryFindOrdinalById(id, out int ordinal)
            && _snapshot.GetByOrdinal(ordinal) is { Kind: TemplateEventKind.Note } value)
        {
            item = _projection == TemplateNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value);
            return true;
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
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out TimelineRenderItem item)) destination.Add(item);
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll() =>
        _snapshot.EnumerateAll()
            .Where(static value => value.Kind == TemplateEventKind.Note)
            .Select(value => _projection == TemplateNoteTimelineProjection.Notes
                ? ToNoteItem(value)
                : ToVelocityItem(value));

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (_projection != TemplateNoteTimelineProjection.Notes
            || extent <= 0
            || destination.IsEmpty)
        {
            return;
        }
        Span<byte> noteColumns = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        Span<byte> eventColumns = destination.Length <= 4096
            ? stackalloc byte[destination.Length]
            : new byte[destination.Length];
        _snapshot.AccumulateOverviewColumns(extent, noteColumns, eventColumns);
        for (int index = 0; index < noteColumns.Length; index++)
        {
            if (noteColumns[index] != 0 && destination[index] < int.MaxValue)
                destination[index]++;
        }
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
        if (kind is not (TimelineRasterAggregateKind.PianoNotes
            or TimelineRasterAggregateKind.Velocity))
        {
            return false;
        }
        TimelineRasterColumnSummary[] raw = new TimelineRasterColumnSummary[destination.Length];
        int minimumNote = kind == TimelineRasterAggregateKind.PianoNotes
            ? Math.Clamp(128 - lastLaneExclusive, 0, 127)
            : 0;
        int maximumNote = kind == TimelineRasterAggregateKind.PianoNotes
            ? Math.Clamp(127 - firstLane, 0, 127)
            : 127;
        if (maximumNote < minimumNote) return true;
        sourceWorkCount = _snapshot.AccumulateNoteRasterColumns(
            projection,
            minimumNote,
            maximumNote,
            raw,
            cancellationToken);
        PagedLogicalNoteTimelineItemSource.MergeRasterColumns(raw, destination, kind);
        return true;
    }

    private TimelineRenderItem ToNoteItem(TemplateEvent value) => new(
        value.Id,
        TimelineItemKind.TemplateNote,
        value.Tick,
        checked(value.Tick + value.LengthTicks),
        127 - Math.Clamp(value.Number, 0, 127),
        value.Value / 127d,
        1,
        State(value.Id, value.Number is < 0 or > 127));

    private TimelineRenderItem ToNoteItem(TemplateEventSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.TemplateNote,
        value.Tick,
        checked(value.Tick + value.LengthTicks),
        127 - Math.Clamp(value.Number, 0, 127),
        value.Value / 127d,
        1,
        State(value.Id, value.Number is < 0 or > 127));

    private TimelineRenderItem ToVelocityItem(TemplateEvent value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.Tick,
        checked(value.Tick + 1),
        0,
        value.Value / 127d,
        value.Number,
        State(value.Id, invalid: false));

    private TimelineRenderItem ToVelocityItem(TemplateEventSnapshotValue value) => new(
        value.Id,
        TimelineItemKind.Velocity,
        value.Tick,
        checked(value.Tick + 1),
        0,
        value.Value / 127d,
        value.Number,
        State(value.Id, invalid: false));

    private TimelineItemState State(MidoraId id, bool invalid)
    {
        TimelineItemState state = invalid
            ? TimelineItemState.Invalid
            : TimelineItemState.None;
        if (_selectedIds.Contains(id)) state |= TimelineItemState.Selected;
        if (_primaryId == id) state |= TimelineItemState.Primary;
        return state;
    }
}

internal sealed class PagedTemplateEventLaneTimelineItemSource :
    IPreparedPagedTimelineItemSource,
    ITimelineRasterAggregateSource
{
    private const ulong FingerprintOffset = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;
    private readonly SubVoice _voice;
    private readonly TemplateEventQuerySnapshot _snapshot;
    private readonly MidiValueTarget _target;
    private readonly IReadOnlySet<MidoraId> _selectedIds;
    private readonly MidoraId? _primaryId;
    private readonly TimelineEventTargetLaneIndex? _targetIndex;

    public PagedTemplateEventLaneTimelineItemSource(
        SubVoice voice,
        TemplateEventQuerySnapshot snapshot,
        MidiValueTarget target,
        IReadOnlySet<MidoraId>? selectedIds = null,
        MidoraId? primaryId = null,
        TimelineEventTargetLaneIndex? targetIndex = null)
    {
        _voice = voice ?? throw new ArgumentNullException(nameof(voice));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _target = target;
        _selectedIds = selectedIds ?? EmptySelection;
        _primaryId = primaryId;
        _targetIndex = targetIndex;
    }

    private static IReadOnlySet<MidoraId> EmptySelection { get; } = new HashSet<MidoraId>();

    // The immutable SubVoice page store contains Note and non-Note events.
    // This upper bound intentionally keeps a large mixed source on the paged
    // path without counting/materializing the active lane on the UI thread.
    public long Count => _targetIndex?.Count ?? _snapshot.Count;
    public bool HasExternalValueStorage => _snapshot.UsesExternalStorage;
    public void PrefetchRange(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        CancellationToken cancellationToken)
    {
        if (endTick > startTick && firstLane <= 0 && lastLaneExclusive > 0)
            _snapshot.Prefetch(new(startTick, endTick, categoryMask: 2UL), cancellationToken);
    }
    public long MaximumEndTick => _targetIndex?.MaximumEndTick ?? _snapshot.MaximumEndTick;
    public ulong ContentFingerprint => PureMidiPresentationFingerprint.Create(
        null,
        unchecked((long)_snapshot.ContentFingerprint),
        unchecked((long)_target.Kind),
        _target.Number,
        0x53564556454e544c);

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive)
    {
        if (endTick <= startTick || firstLane > 0 || lastLaneExclusive <= 0)
            return EmptyRangeFingerprint(startTick, endTick);
        if (_targetIndex is not null)
            return _targetIndex.GetRangeFingerprint(startTick, endTick);

        ulong hash = FingerprintOffset;
        AddFingerprint(ref hash, unchecked((ulong)_target.Kind));
        AddFingerprint(ref hash, unchecked((ulong)_target.Number));
        long count = 0;
        foreach (TemplateEventSnapshotValue value in _snapshot.QueryEvents(startTick, endTick))
        {
            if (!TryGetTargetValue(value, _target, out int targetValue)) continue;
            AddFingerprint(ref hash, unchecked((ulong)value.Id.Value));
            AddFingerprint(ref hash, unchecked((ulong)value.Tick));
            AddFingerprint(ref hash, unchecked((ulong)targetValue));
            count++;
        }
        AddFingerprint(ref hash, unchecked((ulong)count));
        return hash;
    }

    public void QueryInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);
    }

    public void VisitInto(
        long startTick,
        long endTick,
        int firstLane,
        int lastLaneExclusive,
        Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (endTick <= startTick || firstLane > 0 || lastLaneExclusive <= 0) return;
        if (_targetIndex is not null)
        {
            _targetIndex.Visit(
                startTick,
                endTick,
                value => visitor(ToNormalizedItem(value.Id, value.Tick, value.Value)));
            return;
        }
        foreach (TemplateEventSnapshotValue value in _snapshot.QueryEvents(startTick, endTick))
        {
            if (TryGetTargetValue(value, _target, out int targetValue))
                visitor(ToItem(value.Id, value.Tick, targetValue));
        }
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (_snapshot.TryFindOrdinalById(id, out int ordinal)
            && _snapshot.GetByOrdinal(ordinal) is var value
            && TryGetTargetValue(value, _target, out int targetValue))
        {
            item = ToItem(value.Id, value.Tick, targetValue);
            return true;
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
        foreach (MidoraId id in ids)
        {
            if (TryGetById(id, out TimelineRenderItem item)) destination.Add(item);
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll()
    {
        if (_targetIndex is not null)
        {
            foreach (TimelineEventTargetPoint value in _targetIndex.Query(0, long.MaxValue))
                yield return ToNormalizedItem(value.Id, value.Tick, value.Value);
            yield break;
        }
        foreach (TemplateEventSnapshotValue value in _snapshot.QueryEvents(0, long.MaxValue))
        {
            if (TryGetTargetValue(value, _target, out int targetValue))
                yield return ToItem(value.Id, value.Tick, targetValue);
        }
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
        if (kind != TimelineRasterAggregateKind.EventPoints || _targetIndex is null)
            return false;
        sourceWorkCount = _targetIndex.AccumulateRasterColumns(
            projection,
            destination,
            cancellationToken);
        return true;
    }

    private TimelineRenderItem ToItem(MidoraId id, long tick, int value)
    {
        (double minimum, double maximum) = MidiValueRange(_target);
        return ToNormalizedItem(
            id,
            tick,
            Math.Clamp((value - minimum) / (maximum - minimum), 0, 1));
    }

    private TimelineRenderItem ToNormalizedItem(
        MidoraId id,
        long tick,
        double normalized)
    {
        TimelineItemState state = TimelineItemState.None;
        if (_selectedIds.Contains(id)) state |= TimelineItemState.Selected;
        if (_primaryId == id) state |= TimelineItemState.Primary;
        return new(
            id,
            TimelineItemKind.LogicalParameterPoint,
            tick,
            tick == long.MaxValue ? long.MaxValue : tick + 1,
            0,
            normalized,
            2,
            state);
    }

    private static (double Minimum, double Maximum) MidiValueRange(MidiValueTarget target) =>
        target.Kind switch
        {
            MidiValueKind.PitchBend => (-8192, 8191),
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                (0, 16383),
            MidiValueKind.PitchBendRangeCents => (0, 99),
            _ => (0, 127)
        };

    internal static bool TryGetTargetValue(
        TemplateEventSnapshotValue value,
        MidiValueTarget target,
        out int result) => TryGetTargetValue(
            value.Kind,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            target,
            out result);

    internal static bool TryGetTargetValue(
        TemplateEvent value,
        MidiValueTarget target,
        out int result)
    {
        ArgumentNullException.ThrowIfNull(value);
        return TryGetTargetValue(
            value.Kind,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            target,
            out result);
    }

    private static bool TryGetTargetValue(
        TemplateEventKind kind,
        int number,
        int value,
        int secondaryValue,
        bool hasBankMsb,
        bool hasBankLsb,
        MidiValueTarget target,
        out int result)
    {
        bool matches = target.Kind switch
        {
            MidiValueKind.ControlChange => kind == TemplateEventKind.ControlChange
                && number == target.Number,
            MidiValueKind.BankMsb => kind == TemplateEventKind.Bank && hasBankMsb,
            MidiValueKind.BankLsb => kind == TemplateEventKind.Bank && hasBankLsb,
            MidiValueKind.Program => kind == TemplateEventKind.Program,
            MidiValueKind.PitchBend => kind == TemplateEventKind.PitchBend,
            MidiValueKind.RegisteredParameter => kind == TemplateEventKind.RegisteredParameter
                && number == target.Number,
            MidiValueKind.NonRegisteredParameter => kind == TemplateEventKind.NonRegisteredParameter
                && number == target.Number,
            MidiValueKind.PitchBendRangeSemitones => kind == TemplateEventKind.PitchBendRange,
            MidiValueKind.PitchBendRangeCents => kind == TemplateEventKind.PitchBendRange,
            _ => false
        };
        if (!matches)
        {
            result = default;
            return false;
        }
        result = target.Kind is MidiValueKind.BankLsb or MidiValueKind.PitchBendRangeCents
            ? secondaryValue
            : value;
        return true;
    }

    private static ulong EmptyRangeFingerprint(long startTick, long endTick)
    {
        ulong hash = FingerprintOffset;
        AddFingerprint(ref hash, unchecked((ulong)startTick));
        AddFingerprint(ref hash, unchecked((ulong)endTick));
        return hash;
    }

    private static void AddFingerprint(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= FingerprintPrime;
    }
}

internal sealed class TemplateEventOverviewSource : ITimelineOverviewSource
{
    private readonly TemplateEventQuerySnapshot _snapshot;

    public TemplateEventOverviewSource(TemplateEventQuerySnapshot snapshot) =>
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public ulong ContentFingerprint => _snapshot.ContentFingerprint;
    public long MaximumEndTick => _snapshot.MaximumEndTick;

    public void Accumulate(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        MaterializedTimelineOverviewSource.ValidateOverviewColumns(
            extent,
            noteStartColumns,
            eventColumns);
        _snapshot.AccumulateOverviewColumns(extent, noteStartColumns, eventColumns);
    }
}
