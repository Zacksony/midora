using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Numerics;

namespace Midora.Domain;

/// <summary>
/// Immutable render/compile value for a Logical Note page. This is an in-memory
/// paging boundary only; Format 1 persistence continues to use its frozen wire
/// representation.
/// </summary>
public readonly record struct LogicalNoteSnapshotValue(
    MidoraId Id,
    long StartTick,
    long LengthTicks,
    int Note,
    int Velocity);

/// <summary>
/// Immutable render/compile value for a SubVoice event page.
/// </summary>
public readonly record struct TemplateEventSnapshotValue(
    MidoraId Id,
    TemplateEventKind Kind,
    long Tick,
    long LengthTicks,
    int Number,
    int Value,
    int SecondaryValue,
    bool HasBankMsb,
    bool HasBankLsb,
    bool FollowPitchDelta);

/// <summary>
/// Immutable render/compile value for a logical-parameter or ValueCurve point page.
/// </summary>
public readonly record struct CurvePointSnapshotValue(
    MidoraId Id,
    long Tick,
    double Value,
    CurveInterpolation Interpolation);

internal readonly record struct TimelineStartLaneKey(long Tick, int Lane);

/// <summary>
/// Raster-only, fixed-width summary of immutable timeline content. It is a
/// conservative presentation envelope and must never be used for hit testing,
/// selection, editing, compilation, or persistence semantics.
/// </summary>
public struct TimelineRasterColumnSummary
{
    public ulong LaneMaskLow { get; private set; }
    public ulong LaneMaskHigh { get; private set; }
    public ulong StartLaneMaskLow { get; private set; }
    public ulong StartLaneMaskHigh { get; private set; }
    public ulong EndLaneMaskLow { get; private set; }
    public ulong EndLaneMaskHigh { get; private set; }
    public double MinimumValue { get; private set; }
    public double MaximumValue { get; private set; }
    public int ApproximateSourceCount { get; private set; }
    public bool HasContent => ApproximateSourceCount != 0;

    public void Include(
        ulong laneMaskLow,
        ulong laneMaskHigh,
        double minimumValue,
        double maximumValue,
        int approximateSourceCount)
    {
        if (approximateSourceCount <= 0) return;
        LaneMaskLow |= laneMaskLow;
        LaneMaskHigh |= laneMaskHigh;
        if (ApproximateSourceCount == 0)
        {
            MinimumValue = minimumValue;
            MaximumValue = maximumValue;
        }
        else
        {
            MinimumValue = Math.Min(MinimumValue, minimumValue);
            MaximumValue = Math.Max(MaximumValue, maximumValue);
        }
        ApproximateSourceCount = ApproximateSourceCount > int.MaxValue - approximateSourceCount
            ? int.MaxValue
            : ApproximateSourceCount + approximateSourceCount;
    }

    /// <summary>
    /// Preserves source-object boundaries which remain distinguishable after
    /// the fixed-column projection. This is presentation-only metadata; it
    /// must never be used to reconstruct timeline objects or semantic boundaries.
    /// </summary>
    public void IncludeStartBoundary(
        ulong laneMaskLow,
        ulong laneMaskHigh)
    {
        StartLaneMaskLow |= laneMaskLow;
        StartLaneMaskHigh |= laneMaskHigh;
    }

    public void IncludeEndBoundary(
        ulong laneMaskLow,
        ulong laneMaskHigh)
    {
        EndLaneMaskLow |= laneMaskLow;
        EndLaneMaskHigh |= laneMaskHigh;
    }
}

/// <summary>
/// Exact device-column projection shared by detailed and aggregated timeline
/// rasterization.  Keeping a nearby tick origin avoids precision loss at very
/// large project positions, while the half-up boundary rule matches the bitmap
/// rasterizers exactly.
/// </summary>
public readonly record struct TimelineRasterColumnProjection
{
    public TimelineRasterColumnProjection(
        long startTick,
        long endTick,
        long tickOrigin,
        double deviceXAtOrigin,
        double devicePixelsPerTick,
        int columnCount)
    {
        if (startTick < 0) throw new ArgumentOutOfRangeException(nameof(startTick));
        if (endTick <= startTick) throw new ArgumentOutOfRangeException(nameof(endTick));
        if (!double.IsFinite(deviceXAtOrigin))
            throw new ArgumentOutOfRangeException(nameof(deviceXAtOrigin));
        if (!double.IsFinite(devicePixelsPerTick) || devicePixelsPerTick <= 0)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerTick));
        if (columnCount <= 0) throw new ArgumentOutOfRangeException(nameof(columnCount));
        StartTick = startTick;
        EndTick = endTick;
        TickOrigin = tickOrigin;
        DeviceXAtOrigin = deviceXAtOrigin;
        DevicePixelsPerTick = devicePixelsPerTick;
        ColumnCount = columnCount;
    }

    public long StartTick { get; }
    public long EndTick { get; }
    public long TickOrigin { get; }
    public double DeviceXAtOrigin { get; }
    public double DevicePixelsPerTick { get; }
    public int ColumnCount { get; }

    public bool TryGetColumns(
        long contentStartTick,
        long contentEndTick,
        out int first,
        out int lastExclusive)
    {
        first = 0;
        lastExclusive = 0;
        if (contentEndTick <= StartTick || contentStartTick >= EndTick) return false;
        long clippedStart = Math.Max(StartTick, contentStartTick);
        long clippedEnd = Math.Min(EndTick, contentEndTick);
        int rawFirst = Boundary(clippedStart);
        int rawLastExclusive = Math.Max(rawFirst + 1, Boundary(clippedEnd));
        first = Math.Clamp(rawFirst, 0, ColumnCount);
        lastExclusive = Math.Clamp(rawLastExclusive, 0, ColumnCount);
        return lastExclusive > first;
    }

    public int ColumnSpan(long contentStartTick, long contentEndTick) =>
        TryGetColumns(contentStartTick, contentEndTick, out int first, out int lastExclusive)
            ? lastExclusive - first
            : 0;

    public int PointColumn(long tick) => Math.Clamp(Boundary(tick), 0, ColumnCount - 1);

    private int Boundary(long tick)
    {
        double relative = TickDifference(tick, TickOrigin) * DevicePixelsPerTick
            + DeviceXAtOrigin;
        if (relative <= int.MinValue) return int.MinValue;
        if (relative >= int.MaxValue) return int.MaxValue;
        return checked((int)Math.Floor(relative + 0.5));
    }

    private static double TickDifference(long left, long right) => left >= right
        ? (double)unchecked((ulong)left - (ulong)right)
        : -(double)unchecked((ulong)right - (ulong)left);
}

public sealed class LogicalNoteQuerySnapshot : ITimelineObjectSource<LogicalNoteSnapshotValue>
{
    private readonly PagedTimelineValueSnapshot<LogicalNoteSnapshotValue> _values;
    internal PagedTimelineValueSnapshot<LogicalNoteSnapshotValue> Values => _values;

    internal LogicalNoteQuerySnapshot(PagedTimelineValueSnapshot<LogicalNoteSnapshotValue> values) =>
        _values = values;

    public int Count => _values.Count;
    public bool UsesExternalStorage => _values.UsesExternalStorage;
    public long Generation => _values.Generation;
    public long MaximumEndTick => _values.MaximumEndTick;
    public ulong ContentFingerprint => _values.ContentFingerprint;
    public long SourceRevision => Generation;
    public int PageCapacity => PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue>.DefaultPageCapacity;

    public IEnumerable<LogicalNoteSnapshotValue> QueryValues(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.Query(startTick, endTick, minimumNote, maximumNote);

    public IEnumerable<LogicalNoteSnapshotValue> EnumerateAll() => _values.EnumerateAll();
    /// <summary>Bounded-block candidate stream for an explicitly opened object list.
    /// It is deliberately unordered and must not be used as a sorted formal sequence.</summary>
    public IEnumerable<LogicalNoteSnapshotValue> EnumerateListCandidates(long endTick, CancellationToken token = default) =>
        _values.EnumerateListCandidates(endTick, token);
    public LogicalNoteSnapshotValue GetByOrdinal(int ordinal) => _values.GetByOrdinal(ordinal);
    public void PrepareOrdinalLookup(CancellationToken token = default) => _values.PrepareOrdinalLookup(token);
    public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => _values.PrepareOrdinalLookup(builder, token);

    public bool TryGetPageByOrdinal(
        int firstOrdinal,
        int count,
        out TimelineObjectPage<LogicalNoteSnapshotValue> page) =>
        _values.TryGetPageByOrdinal(firstOrdinal, count, out page);

    public int FindOrdinalAtOrAfterTick(long tick) =>
        _values.FindOrdinalAtOrAfterTick(tick);

    public bool TryFindOrdinalById(MidoraId id, out int ordinal) =>
        _values.TryFindOrdinalById(id, out ordinal);

    public IEnumerable<LogicalNoteSnapshotValue> QueryTickRange(
        TimelineObjectRangeQuery query) =>
        _values.Query(
            query.StartTick,
            query.EndTick,
            query.MinimumLane,
            query.MaximumLane,
            query.CategoryMask);

    public void Prefetch(
        TimelineObjectRangeQuery query,
        CancellationToken cancellationToken = default)
    {
        _values.Prefetch(query, cancellationToken);
    }

    public IReadOnlyList<LogicalNoteSnapshotValue> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids) => _values.ResolveByIds(ids);

    internal IReadOnlyList<LogicalNoteSnapshotValue> QueryStartKeys(
        IReadOnlySet<TimelineStartLaneKey> keys) => _values.QueryExactStarts(keys);

    internal IEnumerable<LogicalNoteSnapshotValue> EnumerateExactStart(long tick, int key) =>
        _values.EnumerateExactStart(tick, key, key);
    internal IEnumerable<LogicalNoteSnapshotValue> EnumerateRangeValues(long start, long end) =>
        _values.EnumerateRangeValues(start, end, 0, 127);

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.GetRangeFingerprint(startTick, endTick, minimumNote, maximumNote);

    public void AccumulateStartColumns(long extent, Span<byte> destination) =>
        _values.AccumulateStartColumns(extent, destination);

    public int AccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumNote,
        int maximumNote,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default) =>
        _values.AccumulateRasterColumns(
            projection,
            minimumNote,
            maximumNote,
            destination,
            cancellationToken: cancellationToken);

    internal int CountCandidateSpatialBlocks(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.CountCandidateSpatialBlocks(startTick, endTick, minimumNote, maximumNote);
}

public sealed class TemplateEventQuerySnapshot : ITimelineObjectSource<TemplateEventSnapshotValue>
{
    /// <summary>Unordered, bounded-block object-list candidates; no all-match array.</summary>
    public IEnumerable<TemplateEventSnapshotValue> EnumerateListCandidates(long endTick, CancellationToken token = default) =>
        _values.EnumerateListCandidates(endTick, token);
    private const ulong NoteCategory = 1UL;
    private const ulong EventCategory = 2UL;
    private readonly PagedTimelineValueSnapshot<TemplateEventSnapshotValue> _values;
    internal PagedTimelineValueSnapshot<TemplateEventSnapshotValue> Values => _values;

    internal TemplateEventQuerySnapshot(PagedTimelineValueSnapshot<TemplateEventSnapshotValue> values) =>
        _values = values;

    public int Count => _values.Count;
    public bool UsesExternalStorage => _values.UsesExternalStorage;
    public long Generation => _values.Generation;
    public long MaximumEndTick => _values.MaximumEndTick;
    public ulong ContentFingerprint => _values.ContentFingerprint;
    public long SourceRevision => Generation;
    public int PageCapacity => PagedTimelineObjectList<TemplateEvent, TemplateEventSnapshotValue>.DefaultPageCapacity;

    public IEnumerable<TemplateEventSnapshotValue> QueryNotes(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.Query(startTick, endTick, minimumNote, maximumNote, NoteCategory)
            .Where(static value => value.Kind == TemplateEventKind.Note);

    /// <summary>Unsorted, bounded-block candidates for non-interactive raster aggregation.</summary>
    public IEnumerable<TemplateEventSnapshotValue> QuerySignalCandidates(long startTick, long endTick) =>
        _values.EnumerateRangeValues(startTick, endTick, int.MinValue, int.MaxValue, EventCategory);

    public IEnumerable<TemplateEventSnapshotValue> QueryEvents(
        long startTick,
        long endTick) =>
        _values.Query(startTick, endTick, int.MinValue, int.MaxValue, EventCategory)
            .Where(static value => value.Kind != TemplateEventKind.Note);

    public IEnumerable<TemplateEventSnapshotValue> EnumerateAll() => _values.EnumerateAll();
    public TemplateEventSnapshotValue GetByOrdinal(int ordinal) => _values.GetByOrdinal(ordinal);
    public void PrepareOrdinalLookup(CancellationToken token = default) => _values.PrepareOrdinalLookup(token);
    public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => _values.PrepareOrdinalLookup(builder, token);

    public bool TryGetPageByOrdinal(
        int firstOrdinal,
        int count,
        out TimelineObjectPage<TemplateEventSnapshotValue> page) =>
        _values.TryGetPageByOrdinal(firstOrdinal, count, out page);

    public int FindOrdinalAtOrAfterTick(long tick) =>
        _values.FindOrdinalAtOrAfterTick(tick);

    public bool TryFindOrdinalById(MidoraId id, out int ordinal) =>
        _values.TryFindOrdinalById(id, out ordinal);

    public IEnumerable<TemplateEventSnapshotValue> QueryTickRange(
        TimelineObjectRangeQuery query) =>
        _values.Query(
            query.StartTick,
            query.EndTick,
            query.MinimumLane,
            query.MaximumLane,
            query.CategoryMask);

    public void Prefetch(
        TimelineObjectRangeQuery query,
        CancellationToken cancellationToken = default)
    {
        _values.Prefetch(query, cancellationToken);
    }

    public IReadOnlyList<TemplateEventSnapshotValue> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids) => _values.ResolveByIds(ids);

    internal IReadOnlyList<TemplateEventSnapshotValue> QueryNoteStartKeys(
        IReadOnlySet<TimelineStartLaneKey> keys) =>
        _values.QueryExactStarts(keys, NoteCategory);

    internal IEnumerable<TemplateEventSnapshotValue> EnumerateNoteExactStart(long tick, int key) =>
        _values.EnumerateExactStart(tick, key, key, NoteCategory);
    internal IEnumerable<TemplateEventSnapshotValue> EnumerateEventExactTick(long tick) =>
        _values.EnumerateExactStart(tick, int.MinValue, int.MaxValue, EventCategory);
    internal IEnumerable<TemplateEventSnapshotValue> EnumerateNoteRangeValues(long start, long end) =>
        _values.EnumerateRangeValues(start, end, 0, 127, NoteCategory);

    internal IReadOnlyList<TemplateEventSnapshotValue> QueryEventTicks(
        IReadOnlySet<long> ticks) =>
        _values.QueryExactTicks(ticks, int.MinValue, int.MaxValue, EventCategory);

    public IReadOnlyList<long> DiscoveryKeys => _values.DiscoveryKeys;
    public int NoteCount => _values.DiscoveryCounts.GetValueOrDefault(long.MinValue);
    public IReadOnlyDictionary<long, int> DiscoveryCounts => _values.DiscoveryCounts;

    public ulong GetNoteRangeFingerprint(
        long startTick,
        long endTick,
        int minimumNote = 0,
        int maximumNote = 127) =>
        _values.GetRangeFingerprint(
            startTick,
            endTick,
            minimumNote,
            maximumNote,
            NoteCategory);

    public ulong GetEventRangeFingerprint(long startTick, long endTick) =>
        _values.GetRangeFingerprint(
            startTick,
            endTick,
            int.MinValue,
            int.MaxValue,
            EventCategory);

    public void AccumulateOverviewColumns(
        long extent,
        Span<byte> noteStartColumns,
        Span<byte> eventColumns)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        if (noteStartColumns.Length != eventColumns.Length)
            throw new ArgumentException("SubVoice overview channels must have equal widths.");
        foreach (TemplateEventSnapshotValue value in _values.EnumerateAll())
        {
            Span<byte> target = value.Kind == TemplateEventKind.Note
                ? noteStartColumns
                : eventColumns;
            PagedTimelineValueSnapshot<TemplateEventSnapshotValue>.MarkColumn(
                target,
                value.Tick,
                extent);
        }
    }

    public int AccumulateNoteRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumNote,
        int maximumNote,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default) =>
        _values.AccumulateRasterColumns(
            projection,
            minimumNote,
            maximumNote,
            destination,
            NoteCategory,
            cancellationToken);

    public int AccumulateEventRasterColumns(
        TimelineRasterColumnProjection projection,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default) =>
        _values.AccumulateRasterColumns(
            projection,
            int.MinValue,
            int.MaxValue,
            destination,
            EventCategory,
            cancellationToken);

}

public sealed class CurvePointQuerySnapshot : ITimelineObjectSource<CurvePointSnapshotValue>
{
    /// <summary>Unordered, bounded-block object-list candidates; no all-match array.</summary>
    public IEnumerable<CurvePointSnapshotValue> EnumerateListCandidates(long endTick, CancellationToken token = default) =>
        _values.EnumerateListCandidates(endTick, token);
    public CurvePointSnapshotValue GetByOrdinal(int ordinal) => _values.GetByOrdinal(ordinal);
    public void PrepareOrdinalLookup(CancellationToken token = default) => _values.PrepareOrdinalLookup(token);
    public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => _values.PrepareOrdinalLookup(builder, token);
    internal IEnumerable<CurvePointSnapshotValue> EnumerateExactTick(long tick) =>
        _values.EnumerateExactStart(tick, int.MinValue, int.MaxValue);
    internal IEnumerable<CurvePointSnapshotValue> EnumerateRangeValues(long start, long end) =>
        _values.EnumerateRangeValues(start, end, int.MinValue, int.MaxValue);
    private readonly PagedTimelineValueSnapshot<CurvePointSnapshotValue> _values;
    internal PagedTimelineValueSnapshot<CurvePointSnapshotValue> Values => _values;

    internal CurvePointQuerySnapshot(PagedTimelineValueSnapshot<CurvePointSnapshotValue> values) =>
        _values = values;

    public int Count => _values.Count;
    public bool UsesExternalStorage => _values.UsesExternalStorage;
    public long Generation => _values.Generation;
    public long MaximumTick => Math.Max(0, _values.MaximumEndTick - 1);
    public ulong ContentFingerprint => _values.ContentFingerprint;
    public long SourceRevision => Generation;
    public int PageCapacity => PagedTimelineObjectList<CurvePoint, CurvePointSnapshotValue>.DefaultPageCapacity;

    /// <summary>Unsorted, bounded-block candidates; does not allocate an all-match array.</summary>
    public IEnumerable<CurvePointSnapshotValue> QuerySignalCandidates(long startTick, long endTick) =>
        _values.EnumerateRangeValues(startTick, endTick, 0, 0);

    public IEnumerable<CurvePointSnapshotValue> QueryValues(long startTick, long endTick) =>
        _values.Query(startTick, endTick, 0, 0);

    public IEnumerable<CurvePointSnapshotValue> EnumerateAll() => _values.EnumerateAll();

    public bool TryGetPageByOrdinal(
        int firstOrdinal,
        int count,
        out TimelineObjectPage<CurvePointSnapshotValue> page) =>
        _values.TryGetPageByOrdinal(firstOrdinal, count, out page);

    public int FindOrdinalAtOrAfterTick(long tick) =>
        _values.FindOrdinalAtOrAfterTick(tick);

    public bool TryFindOrdinalById(MidoraId id, out int ordinal) =>
        _values.TryFindOrdinalById(id, out ordinal);

    public IEnumerable<CurvePointSnapshotValue> QueryTickRange(
        TimelineObjectRangeQuery query) =>
        _values.Query(
            query.StartTick,
            query.EndTick,
            query.MinimumLane,
            query.MaximumLane,
            query.CategoryMask);

    public void Prefetch(
        TimelineObjectRangeQuery query,
        CancellationToken cancellationToken = default)
    {
        _values.Prefetch(query, cancellationToken);
    }

    public IReadOnlyList<CurvePointSnapshotValue> ResolveByIds(
        IReadOnlyCollection<MidoraId> ids) => _values.ResolveByIds(ids);

    internal IReadOnlyList<CurvePointSnapshotValue> QueryTicks(IReadOnlySet<long> ticks) =>
        _values.QueryExactTicks(ticks, 0, 0);

    public ulong GetRangeFingerprint(long startTick, long endTick) =>
        _values.GetRangeFingerprint(startTick, endTick, 0, 0);

    public void AccumulateStartColumns(long extent, Span<byte> destination) =>
        _values.AccumulateStartColumns(extent, destination);

    public int AccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default) =>
        _values.AccumulateRasterColumns(projection, 0, 0, destination, cancellationToken: cancellationToken);

    internal int CountCandidateSpatialBlocks(long startTick, long endTick) =>
        _values.CountCandidateSpatialBlocks(startTick, endTick, 0, 0);
}

public sealed class LogicalNoteCollection : Collection<LogicalNote>
{
    private readonly PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue> _store;

    internal LogicalNoteCollection()
        : this(CreateStore())
    {
    }

    private LogicalNoteCollection(
        PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue> store)
        : base(store) => _store = store;

    public long Generation => _store.Generation;
    public int PageCount => _store.PageCount;
    internal (int RetainedObjects, int FacadeSlots) StorageCounts => _store.StorageCounts;

    public void AddRange(IEnumerable<LogicalNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<LogicalNote> materialized = values as IReadOnlyList<LogicalNote>
            ?? values.ToArray();
        _store.InsertRange(Count, materialized);
    }

    public void InsertRange(int index, IReadOnlyList<LogicalNote> values) =>
        _store.InsertRange(index, values);

    public int RemoveRange(IReadOnlyCollection<LogicalNote> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return _store.RemoveRange(values);
    }

    internal Action RemoveRangeForExactCollision(IReadOnlyCollection<LogicalNote> values) =>
        _store.RemoveRangeForExactCollision(values);

    internal Action RemoveRangeWithUndo(IReadOnlyCollection<LogicalNote> values) =>
        _store.RemoveRangeForExactCollision(values);

    public IDisposable BeginBatchChange() => _store.BeginBatchChange();

    public LogicalNoteQuerySnapshot CreateQuerySnapshot() =>
        new(_store.CreateSnapshot());

    public void AdoptSnapshot(MidoraProject project, LogicalNoteQuerySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptSnapshot(snapshot.Values, value => new LogicalNote(project, value.Id)
        {
            StartTick = value.StartTick, LengthTicks = value.LengthTicks,
            Note = value.Note, Velocity = value.Velocity
        });
    }

    public void AdoptSource(MidoraProject project, IImmutableTimelineValueSource<LogicalNoteSnapshotValue> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        _store.AdoptSource(source, value => new LogicalNote(project, value.Id)
        {
            StartTick = value.StartTick, LengthTicks = value.LengthTicks,
            Note = value.Note, Velocity = value.Velocity
        }, cancellationToken);
    }

    public void AdoptEditedSnapshot(MidoraProject project, LogicalNoteQuerySnapshot snapshot,
        IImmutableTimelineValueSource<TimelineValueEdit<LogicalNoteSnapshotValue>> changes,
        IImmutableTimelineValueSource<LogicalNoteSnapshotValue>? appended = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptEditedSnapshot(snapshot.Values, changes, appended, value => new LogicalNote(project, value.Id)
        {
            StartTick = value.StartTick, LengthTicks = value.LengthTicks,
            Note = value.Note, Velocity = value.Velocity
        }, cancellationToken);
    }

    public bool TryGetById(MidoraId id, out LogicalNote? value) =>
        _store.TryGetById(id, out value);

    public void AdoptSplicedSnapshot(MidoraProject project, LogicalNoteQuerySnapshot snapshot,
        IImmutableTimelineValueSource<TimelineValueSplice> splices,
        IIndexedImmutableTimelineValueSource<LogicalNoteSnapshotValue> values, CancellationToken cancellationToken = default)
    {
        _store.AdoptSplicedSnapshot(snapshot.Values, splices, values, value => new LogicalNote(project, value.Id)
        { StartTick = value.StartTick, LengthTicks = value.LengthTicks, Note = value.Note, Velocity = value.Velocity }, cancellationToken);
    }

    public IReadOnlyList<LogicalNote> ResolveByIdsInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids) =>
        _store.ResolveByIdsInCollectionOrder(ids);

    public IReadOnlyList<(int Index, LogicalNote Value)> ResolveByIdsWithIndicesInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids) =>
        _store.ResolveByIdsWithIndicesInCollectionOrder(ids);

    private static PagedTimelineObjectList<LogicalNote, LogicalNoteSnapshotValue> CreateStore() =>
        new(
            static value => new(
                value.Id,
                value.StartTick,
                value.LengthTicks,
                value.Note,
                value.Velocity),
            static value => value.Id,
            static value => value.StartTick,
            static value => SaturatingAdd(value.StartTick, Math.Max(1, value.LengthTicks)),
            static value => value.Note,
            static value => PagedTimelineFingerprint.ForLogicalNote(value),
            static (value, sink) => value.SetChangeSink(sink),
            getRasterValue: static value => value.Velocity / 127d,
            materialize: static value => new LogicalNote(value));

    private static long SaturatingAdd(long left, long right) =>
        right <= 0 || left > long.MaxValue - right ? long.MaxValue : left + right;
}

public sealed class CurvePointCollection : Collection<CurvePoint>, IReadOnlyList<CurvePoint>
{
    private readonly PagedTimelineObjectList<CurvePoint, CurvePointSnapshotValue> _store;

    public void AdoptSplicedSnapshot(MidoraProject project, CurvePointQuerySnapshot snapshot,
        IImmutableTimelineValueSource<TimelineValueSplice> splices,
        IIndexedImmutableTimelineValueSource<CurvePointSnapshotValue> values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptSplicedSnapshot(snapshot.Values, splices, values,
            value => new CurvePoint(project, value.Id, value.Tick, value.Value, value.Interpolation), cancellationToken);
    }

    internal CurvePointCollection()
        : this(CreateStore())
    {
    }

    private CurvePointCollection(
        PagedTimelineObjectList<CurvePoint, CurvePointSnapshotValue> store)
        : base(store) => _store = store;

    public long Generation => _store.Generation;
    public int PageCount => _store.PageCount;

    public void AddRange(IEnumerable<CurvePoint> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<CurvePoint> materialized = values as IReadOnlyList<CurvePoint>
            ?? values.ToArray();
        _store.InsertRange(Count, materialized);
    }

    public void InsertRange(int index, IReadOnlyList<CurvePoint> values) =>
        _store.InsertRange(index, values);

    public int RemoveRange(IReadOnlyCollection<CurvePoint> values) =>
        _store.RemoveRange(values);

    internal Action RemoveRangeWithUndo(IReadOnlyCollection<CurvePoint> values) =>
        _store.RemoveRangeForExactCollision(values);

    public int RemoveAll(Predicate<CurvePoint> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        CurvePoint[] removed = this.Where(value => match(value)).ToArray();
        return _store.RemoveRange(removed);
    }

    public void Reverse()
    {
        if (Count < 2) return;
        CurvePoint[] values = this.ToArray();
        Array.Reverse(values);
        using IDisposable batch = _store.BeginBatchChange();
        Clear();
        _store.InsertRange(0, values);
    }

    public void ReplaceRange(
        IReadOnlyList<CurvePoint> expected,
        IReadOnlyList<CurvePoint> replacement) =>
        _store.ReplaceRange(expected, replacement);

    public IDisposable BeginBatchChange() => _store.BeginBatchChange();

    public CurvePointQuerySnapshot CreateQuerySnapshot() => new(_store.CreateSnapshot());

    public void AdoptSnapshot(MidoraProject project, CurvePointQuerySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptSnapshot(snapshot.Values, value => new CurvePoint(
            project, value.Id, value.Tick, value.Value, value.Interpolation));
    }

    public void AdoptSource(MidoraProject project, IImmutableTimelineValueSource<CurvePointSnapshotValue> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        _store.AdoptSource(source, value => new CurvePoint(
            project, value.Id, value.Tick, value.Value, value.Interpolation), cancellationToken);
    }

    public void AdoptEditedSnapshot(MidoraProject project, CurvePointQuerySnapshot snapshot,
        IImmutableTimelineValueSource<TimelineValueEdit<CurvePointSnapshotValue>> changes,
        IImmutableTimelineValueSource<CurvePointSnapshotValue>? appended = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(snapshot);
        _store.AdoptEditedSnapshot(snapshot.Values, changes, appended, value => new CurvePoint(
            project, value.Id, value.Tick, value.Value, value.Interpolation), cancellationToken);
    }

    public bool TryGetById(MidoraId id, out CurvePoint? value) =>
        _store.TryGetById(id, out value);

    public IReadOnlyList<CurvePoint> ResolveByIdsInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids) =>
        _store.ResolveByIdsInCollectionOrder(ids);

    public IReadOnlyList<(int Index, CurvePoint Value)> ResolveByIdsWithIndicesInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids) =>
        _store.ResolveByIdsWithIndicesInCollectionOrder(ids);

    private static PagedTimelineObjectList<CurvePoint, CurvePointSnapshotValue> CreateStore() =>
        new(
            static value => new(value.Id, value.Tick, value.Value, value.Interpolation),
            static value => value.Id,
            static value => value.Tick,
            static value => value.Tick == long.MaxValue ? long.MaxValue : value.Tick + 1,
            static _ => 0,
            static value => PagedTimelineFingerprint.ForCurvePoint(value),
            static (_, _) => { },
            getRasterValue: static value => value.Value,
            materialize: static value => new CurvePoint(value));
}

internal sealed partial class PagedTimelineObjectList<T, TValue> : IList<T>
    where T : class
{
    internal const int DefaultPageCapacity = 4096;

    private readonly Func<T, TValue> _toValue;
    private readonly Func<TValue, MidoraId> _getId;
    private readonly Func<TValue, long> _getStart;
    private readonly Func<TValue, long> _getEnd;
    private readonly Func<TValue, int> _getLane;
    private readonly Func<TValue, ulong> _getFingerprint;
    private readonly Func<TValue, ulong>? _getCategoryMask;
    private readonly Func<TValue, double>? _getRasterValue;
    private readonly Func<TValue, IEnumerable<long>>? _getDiscoveryKeys;
    private readonly Action<T, Action<T>?> _setChangeSink;
    private readonly Func<TValue, T>? _materialize;
    private readonly List<Page> _pages = [];
    // Membership is queried while publishing dirty snapshots.  A linear
    // List.Contains here made a first snapshot after a large batch O(P^2).
    private readonly HashSet<Page> _livePages = [];
    private readonly Dictionary<MidoraId, Entry> _entries = [];
    private Dictionary<T, int>? _duplicateReferenceCounts;
    private readonly HashSet<Page> _dirtyPages = [];
    // Snapshot publication updates the persistent spatial root and each page's
    // immutable published value. UI and compilation readers may request the
    // same revision concurrently; serialize that publication so neither
    // reader can observe a partially replaced root or mutate HashSet state
    // concurrently with the other.
    private readonly object _snapshotPublicationSync = new();
    private PagedTimelineSpatialBlockIndex<TValue> _spatialIndex =
        PagedTimelineSpatialBlockIndex<TValue>.Empty;
    private ImmutableDictionary<long, int> _discoveryKeyPageCounts = ImmutableDictionary<long, int>.Empty;
    private int[] _pageStarts = [0];
    private Dictionary<Page, int> _pageIndices = [];
    private bool _pageDirectoryDirty;
    private int _count;
    private int _batchDepth;
    private bool _batchChanged;
    private long _generation;
    private PersistentTimelineSequence<TValue>? _publishedSequence;
    private ImmutableDictionary<PersistentTimelineSequence<TValue>.Leaf, PagedTimelineValuePage<TValue>>
        _publishedLeafPages = ImmutableDictionary<PersistentTimelineSequence<TValue>.Leaf, PagedTimelineValuePage<TValue>>.Empty;
    private IReadOnlyDictionary<MidoraId, TValue>? _publishedBaseById;
    private PersistentTimelineIdDeltaMap<TValue> _publishedIdDelta =
        PersistentTimelineIdDeltaMap<TValue>.Empty;
    private PersistentTimelineIdDeltaMap<TValue> _spatialBaseDelta =
        PersistentTimelineIdDeltaMap<TValue>.Empty;
    private PersistentTimelineIdDeltaMap<TValue> _spatialValueOverlay =
        PersistentTimelineIdDeltaMap<TValue>.Empty;
    private ImmutableDictionary<PersistentTimelineIdDeltaMap<TValue>.Bucket,
        PagedTimelineValuePage<TValue>> _spatialOverlayPages = ImmutableDictionary<PersistentTimelineIdDeltaMap<TValue>.Bucket, PagedTimelineValuePage<TValue>>.Empty;
    private PagedTimelineSpatialBlockIndex<TValue> _spatialOverlayIndex =
        PagedTimelineSpatialBlockIndex<TValue>.Empty;
    private readonly HashSet<MidoraId> _pendingPublishedValueIds = [];
    private PagedTimelineValueSnapshot<TValue>? _publishedSnapshot;
    private long _publishedSnapshotGeneration = long.MinValue;

    public PagedTimelineObjectList(
        Func<T, TValue> toValue,
        Func<TValue, MidoraId> getId,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint,
        Action<T, Action<T>?> setChangeSink,
        Func<TValue, ulong>? getCategoryMask = null,
        Func<TValue, double>? getRasterValue = null,
        Func<TValue, IEnumerable<long>>? getDiscoveryKeys = null,
        Func<TValue, T>? materialize = null)
    {
        _toValue = toValue ?? throw new ArgumentNullException(nameof(toValue));
        _getId = getId ?? throw new ArgumentNullException(nameof(getId));
        _getStart = getStart ?? throw new ArgumentNullException(nameof(getStart));
        _getEnd = getEnd ?? throw new ArgumentNullException(nameof(getEnd));
        _getLane = getLane ?? throw new ArgumentNullException(nameof(getLane));
        _getFingerprint = getFingerprint ?? throw new ArgumentNullException(nameof(getFingerprint));
        _setChangeSink = setChangeSink ?? throw new ArgumentNullException(nameof(setChangeSink));
        _getCategoryMask = getCategoryMask;
        _getRasterValue = getRasterValue;
        _getDiscoveryKeys = getDiscoveryKeys;
        _materialize = materialize;
    }

    public int Count => _count;
    public bool IsReadOnly => false;
    public long Generation => _generation;
    public int PageCount => _shared is null ? _pages.Count : (_count + DefaultPageCapacity - 1) / DefaultPageCapacity;
    internal (int RetainedObjects, int FacadeSlots) StorageCounts => _shared is null
        ? (_count, 0) : (_shared.RetainedObjectCount, _shared.FacadeSlotCount);

    public T this[int index]
    {
        get
        {
            if (_shared is not null) return _shared.Get(index);
            (Page page, int localIndex) = Locate(index, allowEnd: false);
            return page.Items[localIndex];
        }
        set
        {
            if (_shared is not null) { _shared.Set(index, value); return; }
            ArgumentNullException.ThrowIfNull(value);
            (Page page, int localIndex) = Locate(index, allowEnd: false);
            T old = page.Items[localIndex];
            if (ReferenceEquals(old, value)) return;
            EnsureInsertable(value, old);
            PublishPendingValueChanges();
            page.Items[localIndex] = value;
            Detach(old);
            Attach(value, page, localIndex);
            ApplyPublishedMutation(_publishedSequence?.ReplaceBatch(
                new Dictionary<int, TValue> { [index] = _toValue(value) }));
            MarkChanged(page);
        }
    }

    public void Add(T item)
    {
        if (_shared is not null) { _shared.Append(item); return; }
        ArgumentNullException.ThrowIfNull(item);
        EnsureInsertable(item);
        PublishPendingValueChanges();
        int publishedIndex = _count;
        Page page = _pages.Count == 0 || _pages[^1].Items.Count >= DefaultPageCapacity
            ? AddPage()
            : _pages[^1];
        page.Items.Add(item);
        _count++;
        InvalidatePageDirectory();
        Attach(item, page, page.Items.Count - 1);
        ApplyPublishedMutation(_publishedSequence?.InsertRange(
            publishedIndex,
            [_toValue(item)]));
        MarkChanged(page);
        PromoteOrdinaryStoreIfNeeded();
    }

    public void Clear()
    {
        if (_shared is not null) { _shared.Clear(); return; }
        if (_count == 0) return;
        PublishPendingValueChanges();
        foreach (Page page in _pages)
        {
            foreach (T item in page.Items) _setChangeSink(item, null);
        }
        _pages.Clear();
        _livePages.Clear();
        _entries.Clear();
        _duplicateReferenceCounts?.Clear();
        _dirtyPages.Clear();
        _spatialIndex = PagedTimelineSpatialBlockIndex<TValue>.Empty;
        _discoveryKeyPageCounts = _discoveryKeyPageCounts.Clear();
        _count = 0;
        InvalidatePageDirectory();
        if (_publishedSequence is not null)
            ResetPublishedStateToEmpty();
        Touch();
    }

    public bool Contains(T item) => _shared is not null ? _shared.IndexOf(item) >= 0
        : item is not null && _entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
            && ReferenceEquals(entry.Item, item);

    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length - Count)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        foreach (T item in this) array[arrayIndex++] = item;
    }

    public IEnumerator<T> GetEnumerator()
    {
        if (_shared is not null)
        {
            foreach (T item in _shared.Enumerate()) yield return item;
            yield break;
        }
        foreach (Page page in _pages)
        {
            foreach (T item in page.Items) yield return item;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public int IndexOf(T item)
    {
        if (_shared is not null) return _shared.IndexOf(item);
        if (item is null
            || !_entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
            || !ReferenceEquals(entry.Item, item))
        {
            return -1;
        }
        EnsurePageDirectory();
        if (!_pageIndices.TryGetValue(entry.Page, out int pageIndex)) return -1;
        int localIndex = entry.LocalIndex;
        if (localIndex < 0)
            localIndex = entry.Page.Items.FindIndex(value => ReferenceEquals(value, item));
        return localIndex < 0 ? -1 : checked(_pageStarts[pageIndex] + localIndex);
    }

    public void Insert(int index, T item)
    {
        if (_shared is not null)
        {
            if (index == _count) _shared.Append(item);
            else _shared.InsertRange(index, [item]);
            return;
        }
        ArgumentNullException.ThrowIfNull(item);
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _count)
        {
            Add(item);
            return;
        }
        EnsureInsertable(item);
        PublishPendingValueChanges();
        (Page page, int localIndex) = Locate(index, allowEnd: false);
        page.Items.Insert(localIndex, item);
        _count++;
        InvalidatePageDirectory();
        ReindexPage(page, localIndex + 1);
        Attach(item, page, localIndex);
        ApplyPublishedMutation(_publishedSequence?.InsertRange(index, [_toValue(item)]));
        if (page.Items.Count > DefaultPageCapacity) Split(page);
        else MarkChanged(page);
        PromoteOrdinaryStoreIfNeeded();
    }

    public void InsertRange(int index, IReadOnlyList<T> values)
    {
        if (_shared is not null) { _shared.InsertRange(index, values); return; }
        ArgumentNullException.ThrowIfNull(values);
        if ((uint)index > (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
        if (values.Count == 0) return;
        ValidateInsertRange(values);

        PublishPendingValueChanges();
        TValue[] publishedValues = _publishedSequence is null
            ? []
            : values.Select(_toValue).ToArray();

        using IDisposable batch = BeginBatchChange();
        if (index == _count)
        {
            int valueIndex = 0;
            Page? page = _pages.Count == 0 ? null : _pages[^1];
            while (valueIndex < values.Count)
            {
                if (page is null || page.Items.Count >= DefaultPageCapacity)
                    page = AddPage();
                int take = Math.Min(
                    DefaultPageCapacity - page.Items.Count,
                    values.Count - valueIndex);
                for (int offset = 0; offset < take; offset++)
                {
                    T value = values[valueIndex++];
                    page.Items.Add(value);
                    Attach(value, page, page.Items.Count - 1);
                }
                _count += take;
                InvalidatePageDirectory();
                MarkChanged(page);
            }
            ApplyPublishedMutation(_publishedSequence?.InsertRange(index, publishedValues));
            PromoteOrdinaryStoreIfNeeded();
            return;
        }

        (Page target, int localIndex) = Locate(index, allowEnd: false);
        EnsurePageDirectory();
        int targetPageIndex = _pageIndices[target];
        List<(T Value, bool Inserted)> combined = new(target.Items.Count + values.Count);
        for (int existing = 0; existing < localIndex; existing++)
            combined.Add((target.Items[existing], false));
        foreach (T value in values) combined.Add((value, true));
        for (int existing = localIndex; existing < target.Items.Count; existing++)
            combined.Add((target.Items[existing], false));

        foreach (T existing in target.Items) Detach(existing);
        target.Items.Clear();
        int combinedIndex = 0;
        int pageOffset = 0;
        while (combinedIndex < combined.Count)
        {
            Page page = pageOffset++ == 0 ? target : new Page();
            if (!ReferenceEquals(page, target))
            {
                _pages.Insert(targetPageIndex + pageOffset - 1, page);
                _livePages.Add(page);
                InvalidatePageDirectory();
            }
            int take = Math.Min(DefaultPageCapacity, combined.Count - combinedIndex);
            for (int offset = 0; offset < take; offset++)
            {
                T value = combined[combinedIndex++].Value;
                page.Items.Add(value);
                Attach(value, page, page.Items.Count - 1);
            }
            MarkChanged(page);
        }
        _count += values.Count;
        InvalidatePageDirectory();
        ApplyPublishedMutation(_publishedSequence?.InsertRange(index, publishedValues));
        Touch();
        PromoteOrdinaryStoreIfNeeded();
    }

    internal void ValidateInsertRange(IReadOnlyList<T> values)
    {
        if (_shared is not null) { _shared.ValidateInsertRange(values); return; }
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<MidoraId, T> batchIds = new(values.Count);
        foreach (T value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            EnsureInsertable(value);
            MidoraId id = _getId(_toValue(value));
            if (batchIds.TryGetValue(id, out T? existing)
                && !ReferenceEquals(existing, value))
            {
                throw new InvalidOperationException(
                    "A paged timeline insertion cannot contain distinct objects with duplicate Stable IDs.");
            }
            batchIds.TryAdd(id, value);
        }
    }

    public bool Remove(T item)
    {
        if (_shared is not null) return _shared.RemoveRange([item]) != 0;
        if (item is null
            || !_entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
            || !ReferenceEquals(entry.Item, item))
        {
            return false;
        }
        int index = entry.LocalIndex;
        if (index < 0)
            index = entry.Page.Items.FindIndex(value => ReferenceEquals(value, item));
        if (index < 0) return false;
        EnsurePageDirectory();
        int publishedIndex = checked(_pageStarts[_pageIndices[entry.Page]] + index);
        PublishPendingValueChanges();
        entry.Page.Items.RemoveAt(index);
        ReindexPage(entry.Page, index);
        _count--;
        InvalidatePageDirectory();
        Detach(item);
        ApplyPublishedMutation(_publishedSequence?.RemoveIndices([publishedIndex]));
        if (entry.Page.Items.Count == 0)
        {
            RetirePage(entry.Page);
            _pages.Remove(entry.Page);
            _livePages.Remove(entry.Page);
            _dirtyPages.Remove(entry.Page);
            Touch();
        }
        else
        {
            MarkChanged(entry.Page);
        }
        return true;
    }

    public int RemoveRange(IReadOnlyCollection<T> values)
    {
        if (_shared is not null) return _shared.RemoveRange(values);
        if (values.Count == 0 || _count == 0) return 0;
        Dictionary<Page, HashSet<T>> requestedByPage = [];
        foreach (T value in values)
        {
            if (!_entries.TryGetValue(_getId(_toValue(value)), out Entry? entry)
                || !ReferenceEquals(entry.Item, value))
            {
                continue;
            }
            if (!requestedByPage.TryGetValue(entry.Page, out HashSet<T>? requested))
            {
                requested = new(ReferenceEqualityComparer.Instance);
                requestedByPage.Add(entry.Page, requested);
            }
            requested.Add(value);
        }
        PublishPendingValueChanges();
        int[] publishedIndices = [];
        if (_publishedSequence is not null)
        {
            EnsurePageDirectory();
            publishedIndices = requestedByPage
                .SelectMany(pair => pair.Key.Items.Select((item, localIndex) =>
                    pair.Value.Contains(item)
                        ? checked(_pageStarts[_pageIndices[pair.Key]] + localIndex)
                        : -1))
                .Where(static index => index >= 0)
                .Order()
                .ToArray();
        }
        int removed = 0;
        HashSet<Page>? emptyPages = null;
        using IDisposable batch = BeginBatchChange();
        foreach (Page page in requestedByPage.Keys)
        {
            HashSet<T> requested = requestedByPage[page];
            List<T>? removedFromPage = null;
            int write = 0;
            for (int read = 0; read < page.Items.Count; read++)
            {
                T item = page.Items[read];
                if (requested.Contains(item))
                {
                    (removedFromPage ??= []).Add(item);
                    removed++;
                    continue;
                }
                if (write != read) page.Items[write] = item;
                if (_entries.TryGetValue(_getId(_toValue(item)), out Entry? retainedEntry)
                    && ReferenceEquals(retainedEntry.Item, item))
                {
                    retainedEntry.LocalIndex = write;
                }
                write++;
            }
            if (write == page.Items.Count) continue;
            page.Items.RemoveRange(write, page.Items.Count - write);
            if (removedFromPage is not null)
            {
                foreach (T item in removedFromPage) Detach(item);
            }
            if (page.Items.Count == 0)
            {
                RetirePage(page);
                (emptyPages ??= []).Add(page);
                _livePages.Remove(page);
                _dirtyPages.Remove(page);
            }
            else
            {
                MarkChanged(page);
            }
        }
        if (removed != 0)
        {
            if (emptyPages is not null)
                _pages.RemoveAll(emptyPages.Contains);
            _count -= removed;
            InvalidatePageDirectory();
            ApplyPublishedMutation(_publishedSequence?.RemoveIndices(publishedIndices));
            Touch();
        }
        return removed;
    }

    public void ReplaceRange(IReadOnlyList<T> expected, IReadOnlyList<T> replacement)
    {
        if (_shared is not null) { _shared.ReplaceRange(expected, replacement); return; }
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        if (expected.Count != replacement.Count)
            throw new ArgumentException("Paged timeline replacement lengths must match.", nameof(replacement));
        if (expected.Count == 0) return;

        Dictionary<MidoraId, (T Expected, T Replacement)> changes = new(expected.Count);
        Dictionary<Page, Dictionary<MidoraId, (T Expected, T Replacement)>> changesByPage = [];
        for (int index = 0; index < expected.Count; index++)
        {
            T oldValue = expected[index] ?? throw new ArgumentNullException(nameof(expected));
            T newValue = replacement[index] ?? throw new ArgumentNullException(nameof(replacement));
            MidoraId id = _getId(_toValue(oldValue));
            if (id != _getId(_toValue(newValue)))
                throw new InvalidOperationException("A paged timeline replacement must preserve Stable ID.");
            if (!changes.TryAdd(id, (oldValue, newValue)))
                throw new InvalidOperationException("A paged timeline replacement contains duplicate Stable IDs.");
            if (!_entries.TryGetValue(id, out Entry? entry)
                || !ReferenceEquals(entry.Item, oldValue))
            {
                throw new InvalidOperationException("A paged timeline replacement is no longer fully present.");
            }
            if (_duplicateReferenceCounts?.ContainsKey(oldValue) == true)
                throw new InvalidOperationException("A duplicate paged timeline reference cannot be batch-replaced.");
            if (!changesByPage.TryGetValue(entry.Page, out var pageChanges))
            {
                pageChanges = [];
                changesByPage.Add(entry.Page, pageChanges);
            }
            pageChanges.Add(id, (oldValue, newValue));
        }

        PublishPendingValueChanges();
        Dictionary<int, TValue>? publishedReplacements = null;
        if (_publishedSequence is not null)
        {
            EnsurePageDirectory();
            publishedReplacements = new(expected.Count);
            foreach ((Page page, var pageChanges) in changesByPage)
            {
                int pageStart = _pageStarts[_pageIndices[page]];
                for (int localIndex = 0; localIndex < page.Items.Count; localIndex++)
                {
                    T current = page.Items[localIndex];
                    if (pageChanges.TryGetValue(_getId(_toValue(current)), out var change))
                        publishedReplacements.Add(
                            checked(pageStart + localIndex),
                            _toValue(change.Replacement));
                }
            }
        }

        int replaced = 0;
        using IDisposable batch = BeginBatchChange();
        foreach ((Page page, var pageChanges) in changesByPage)
        {
            bool pageChanged = false;
            for (int index = 0; index < page.Items.Count; index++)
            {
                T current = page.Items[index];
                MidoraId id = _getId(_toValue(current));
                if (!pageChanges.TryGetValue(id, out var change)) continue;
                if (!ReferenceEquals(current, change.Expected))
                    throw new InvalidOperationException("A paged timeline value changed before batch replacement.");
                if (ReferenceEquals(current, change.Replacement))
                {
                    replaced++;
                    continue;
                }
                EnsureInsertable(change.Replacement, current);
                page.Items[index] = change.Replacement;
                Detach(current);
                Attach(change.Replacement, page, index);
                pageChanged = true;
                replaced++;
            }
            if (pageChanged) MarkChanged(page);
        }
        if (replaced != changes.Count)
            throw new InvalidOperationException("A paged timeline replacement is no longer fully present.");
        ApplyPublishedMutation(publishedReplacements is null
            ? null
            : _publishedSequence!.ReplaceBatch(publishedReplacements));
    }

    public Action RemoveRangeForExactCollision(IReadOnlyCollection<T> values)
    {
        if (_shared is not null) return _shared.RemoveRangeWithUndo(values);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return static () => { };
        HashSet<T> distinct = new(values, ReferenceEqualityComparer.Instance);
        if (distinct.Count != values.Count)
            throw new ArgumentException("Exact-collision values must be distinct.", nameof(values));

        Dictionary<Page, HashSet<T>> requestedByPage = [];
        foreach (T value in distinct)
        {
            if (!_entries.TryGetValue(_getId(_toValue(value)), out Entry? entry)
                || !ReferenceEquals(entry.Item, value))
            {
                throw new InvalidOperationException(
                    "A conflicting paged timeline value is no longer present.");
            }
            if (_duplicateReferenceCounts?.ContainsKey(value) == true)
                throw new InvalidOperationException(
                    "A duplicate paged timeline reference cannot be removed exactly.");
            if (!requestedByPage.TryGetValue(entry.Page, out HashSet<T>? pageValues))
            {
                pageValues = new(ReferenceEqualityComparer.Instance);
                requestedByPage.Add(entry.Page, pageValues);
            }
            pageValues.Add(value);
        }

        EnsurePageDirectory();
        List<CollisionRemoval> removals = [];
        foreach ((Page page, HashSet<T> requested) in requestedByPage)
        {
            List<CollisionRemoval> matches = [];
            for (int localIndex = 0; localIndex < page.Items.Count; localIndex++)
            {
                T value = page.Items[localIndex];
                if (requested.Contains(value))
                {
                    matches.Add(new(
                        checked(_pageStarts[_pageIndices[page]] + localIndex),
                        value));
                }
            }
            if (matches.Count != requested.Count)
                throw new InvalidOperationException(
                    "A conflicting paged timeline value is no longer present.");
            removals.AddRange(matches);
        }
        removals.Sort(static (left, right) => left.OriginalIndex.CompareTo(right.OriginalIndex));

        int removedCount = RemoveRange(removals.Select(static value => value.Value).ToArray());
        if (removedCount != removals.Count)
            throw new InvalidOperationException("A conflicting paged timeline value changed before removal.");

        return () =>
        {
            foreach (CollisionRemoval removal in removals)
                if (_entries.ContainsKey(_getId(_toValue(removal.Value))))
                    throw new InvalidOperationException("A conflicting paged timeline value is already restored.");

            using IDisposable batch = BeginBatchChange();
            int removalIndex = 0;
            while (removalIndex < removals.Count)
            {
                int runStart = removals[removalIndex].OriginalIndex;
                List<T> run = [removals[removalIndex].Value];
                removalIndex++;
                while (removalIndex < removals.Count
                    && removals[removalIndex].OriginalIndex == runStart + run.Count)
                {
                    run.Add(removals[removalIndex++].Value);
                }
                if ((uint)runStart > (uint)_count)
                    throw new InvalidOperationException(
                        "A paged timeline sequence changed before exact restoration.");
                InsertRange(runStart, run);
            }
        };
    }

    public void RemoveAt(int index)
    {
        if (_shared is not null) { _shared.RemoveRange([_shared.Get(index)]); return; }
        (Page page, int localIndex) = Locate(index, allowEnd: false);
        _ = Remove(page.Items[localIndex]);
    }

    public IDisposable BeginBatchChange()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    public bool TryGetById(MidoraId id, out T? value)
    {
        if (_shared is not null) return _shared.TryGetById(id, out value);
        if (_entries.TryGetValue(id, out Entry? entry))
        {
            value = entry.Item;
            return true;
        }
        value = null;
        return false;
    }

    public IReadOnlyList<T> ResolveByIdsInCollectionOrder(IReadOnlyCollection<MidoraId> ids)
        => ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static match => match.Value)
            .ToArray();

    public IReadOnlyList<(int Index, T Value)> ResolveByIdsWithIndicesInCollectionOrder(
        IReadOnlyCollection<MidoraId> ids)
    {
        if (_shared is not null) return _shared.Resolve(ids);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        HashSet<MidoraId> requested = [.. ids];
        Dictionary<Page, HashSet<MidoraId>> idsByPage = [];
        foreach (MidoraId id in requested)
        {
            if (!_entries.TryGetValue(id, out Entry? entry)) continue;
            if (!idsByPage.TryGetValue(entry.Page, out HashSet<MidoraId>? pageIds))
            {
                pageIds = [];
                idsByPage.Add(entry.Page, pageIds);
            }
            pageIds.Add(id);
        }
        EnsurePageDirectory();
        List<(int Index, T Value)> result = new(requested.Count);
        foreach ((Page page, HashSet<MidoraId> pageIds) in idsByPage
            .OrderBy(pair => _pageIndices[pair.Key]))
        {
            int pageStartIndex = _pageStarts[_pageIndices[page]];
            for (int localIndex = 0; localIndex < page.Items.Count; localIndex++)
            {
                T value = page.Items[localIndex];
                if (pageIds.Contains(_getId(_toValue(value))))
                    result.Add((checked(pageStartIndex + localIndex), value));
            }
        }
        return result;
    }

    public PagedTimelineValueSnapshot<TValue> CreateSnapshot()
    {
        lock (_snapshotPublicationSync)
        {
            _shared?.FlushAll();
            EnsurePublishedState();
            PublishPendingValueChanges();
            if (_publishedSnapshot is not null
                && _publishedSnapshotGeneration == _generation)
            {
                return _publishedSnapshot;
            }
            _publishedSnapshot = new(
                _publishedSequence!,
                _spatialIndex,
                _spatialOverlayIndex,
                _spatialValueOverlay,
                _publishedBaseById!,
                _publishedIdDelta,
                _count,
                _generation,
                _getId,
                _getStart,
                _getEnd,
                _getLane,
                _getFingerprint,
                _getRasterValue,
                _discoveryKeyPageCounts)
            {
                EditableRoot = CaptureEditableRoot()
            };
            _publishedSnapshotGeneration = _generation;
            return _publishedSnapshot;
        }
    }

    private void EnsurePublishedState()
    {
        if (_publishedSequence is not null) return;

        PersistentTimelineSequence<TValue> sequence =
            PersistentTimelineSequence<TValue>.Create(
                this.Select(_toValue),
                _getFingerprint);
        _publishedLeafPages = _publishedLeafPages.Clear();
        _discoveryKeyPageCounts = _discoveryKeyPageCounts.Clear();
        List<PagedTimelineValuePage<TValue>> pages = [];
        foreach (PersistentTimelineSequence<TValue>.Leaf leaf in sequence.EnumerateLeaves())
        {
            PagedTimelineValuePage<TValue> page = CreatePublishedPage(leaf);
            _publishedLeafPages = _publishedLeafPages.Add(leaf, page);
            pages.Add(page);
            AddDiscoveryKeys(page);
        }
        _spatialIndex = PagedTimelineSpatialBlockIndex<TValue>.Create(pages);
        _publishedSequence = sequence;
        // A frozen value dictionary duplicated the complete scalar sequence on
        // first snapshot. Address metadata contains IDs/ordinals only and is
        // shared by all readers of this revision.
        _sharedOrdinalDirectory = new OrdinalDirectory(sequence, _getId);
        _publishedBaseById = new SourceValueDictionary(sequence, _sharedOrdinalDirectory, _getId);
        _publishedIdDelta = PersistentTimelineIdDeltaMap<TValue>.Empty;
        _spatialBaseDelta = PersistentTimelineIdDeltaMap<TValue>.Empty;
        _spatialValueOverlay = PersistentTimelineIdDeltaMap<TValue>.Empty;
        _spatialOverlayPages = _spatialOverlayPages.Clear();
        _spatialOverlayIndex = PagedTimelineSpatialBlockIndex<TValue>.Empty;
        _pendingPublishedValueIds.Clear();
        _dirtyPages.Clear();
        _publishedSnapshot = null;
    }

    private void PublishPendingValueChanges()
    {
        lock (_snapshotPublicationSync)
        {
            _shared?.Flush();
            if (_publishedSequence is null || _pendingPublishedValueIds.Count == 0) return;
            EnsurePageDirectory();
            PersistentTimelineSequence<TValue>.IndexedReplacement[] replacements =
                new PersistentTimelineSequence<TValue>.IndexedReplacement[
                    _pendingPublishedValueIds.Count];
            int replacementCount = 0;
            foreach (MidoraId id in _pendingPublishedValueIds)
            {
                if (!_entries.TryGetValue(id, out Entry? entry)) continue;
                int localIndex = entry.LocalIndex;
                if (localIndex < 0)
                    localIndex = entry.Page.Items.FindIndex(
                        item => ReferenceEquals(item, entry.Item));
                if (localIndex < 0)
                    throw new InvalidOperationException("A pending paged timeline value is no longer present.");
                replacements[replacementCount++] = new(
                    checked(_pageStarts[_pageIndices[entry.Page]] + localIndex),
                    _toValue(entry.Item));
            }
            _pendingPublishedValueIds.Clear();
            if (replacementCount != 0)
            {
                if (replacementCount != replacements.Length)
                    Array.Resize(ref replacements, replacementCount);
                Array.Sort(
                    replacements,
                    static (left, right) => left.Index.CompareTo(right.Index));
                PersistentTimelineSequence<TValue>.Mutation mutation =
                    _publishedSequence.ReplaceBatchSorted(replacements);
                ApplyPublishedMutation(mutation);
            }
        }
    }

    private void ApplyPublishedMutation(
        PersistentTimelineSequence<TValue>.Mutation? optionalMutation)
    {
        if (optionalMutation is not { } mutation) return;
        lock (_snapshotPublicationSync)
        {
            ApplyPublishedMutationCore(mutation);
        }
    }

    private void ApplyPublishedMutationCore(
        PersistentTimelineSequence<TValue>.Mutation mutation)
    {
        bool useValueOverlay = CanPublishThroughValueOverlay(mutation);
        if (useValueOverlay)
        {
            if (mutation.RemovedLeaves.Count != mutation.AddedLeaves.Count)
                throw new InvalidOperationException("A value-only timeline mutation changed its formal leaf count.");
            for (int index = 0; index < mutation.RemovedLeaves.Count; index++)
            {
                PersistentTimelineSequence<TValue>.Leaf removed = mutation.RemovedLeaves[index];
                PersistentTimelineSequence<TValue>.Leaf added = mutation.AddedLeaves[index];
                if (!_publishedLeafPages.TryGetValue(removed, out PagedTimelineValuePage<TValue>? basePage))
                    throw new InvalidOperationException("A published timeline leaf is not indexed.");
                _publishedLeafPages = _publishedLeafPages.Remove(removed).Add(added, basePage);
            }
        }
        else
        {
            List<PagedTimelineValuePage<TValue>> removedPages = new(mutation.RemovedLeaves.Count);
            foreach (PersistentTimelineSequence<TValue>.Leaf leaf in mutation.RemovedLeaves)
            {
                if (!_publishedLeafPages.TryGetValue(leaf, out PagedTimelineValuePage<TValue>? page))
                    throw new InvalidOperationException("A published timeline leaf is not indexed.");
                _publishedLeafPages = _publishedLeafPages.Remove(leaf);
                removedPages.Add(page);
                RemoveDiscoveryKeys(page);
            }
            List<PagedTimelineValuePage<TValue>> addedPages = new(mutation.AddedLeaves.Count);
            foreach (PersistentTimelineSequence<TValue>.Leaf leaf in mutation.AddedLeaves)
            {
                PagedTimelineValuePage<TValue> page = CreatePublishedPage(leaf);
                _publishedLeafPages = _publishedLeafPages.Add(leaf, page);
                addedPages.Add(page);
                AddDiscoveryKeys(page);
            }
            _spatialIndex = _spatialIndex.ReplacePages(removedPages, addedPages);
        }

        bool stableValueIds = mutation.ValueReplacements.Count != 0;
        if (stableValueIds)
        {
            foreach (PersistentTimelineSequence<TValue>.ValueReplacement replacement in
                mutation.ValueReplacements)
            {
                if (_getId(replacement.Expected) == _getId(replacement.Replacement)) continue;
                stableValueIds = false;
                break;
            }
        }
        if (stableValueIds)
        {
            ApplyStableValueDelta(mutation.ValueReplacements, useValueOverlay);
            _publishedSequence = mutation.Sequence;
            _publishedSnapshot = null;
            return;
        }

        _sharedOrdinalDirectory = null;

        HashSet<MidoraId> affectedIds = [];
        Dictionary<MidoraId, TValue> finalValues = [];
        if (mutation.ValueReplacements.Count != 0)
        {
            foreach (PersistentTimelineSequence<TValue>.ValueReplacement replacement in
                mutation.ValueReplacements)
            {
                MidoraId expectedId = _getId(replacement.Expected);
                MidoraId replacementId = _getId(replacement.Replacement);
                affectedIds.Add(expectedId);
                affectedIds.Add(replacementId);
                finalValues[replacementId] = replacement.Replacement;
            }
        }
        else
        {
            foreach (PersistentTimelineSequence<TValue>.Leaf leaf in mutation.RemovedLeaves)
                foreach (TValue value in leaf.Values) affectedIds.Add(_getId(value));
            foreach (PersistentTimelineSequence<TValue>.Leaf leaf in mutation.AddedLeaves)
            {
                foreach (TValue value in leaf.Values)
                {
                    MidoraId id = _getId(value);
                    affectedIds.Add(id);
                    finalValues[id] = value;
                }
            }
        }
        Dictionary<MidoraId, PagedTimelineIdDelta<TValue>?> deltaChanges =
            new(affectedIds.Count);
        foreach (MidoraId id in affectedIds)
        {
            if (finalValues.TryGetValue(id, out TValue? finalValue))
            {
                if (_publishedBaseById!.TryGetValue(id, out TValue? baseValue)
                    && EqualityComparer<TValue>.Default.Equals(baseValue, finalValue))
                {
                    deltaChanges[id] = null;
                }
                else
                {
                    deltaChanges[id] = new(true, finalValue);
                }
            }
            else if (_publishedBaseById!.ContainsKey(id))
            {
                deltaChanges[id] = new(false, default!);
            }
            else
            {
                deltaChanges[id] = null;
            }
        }
        PersistentTimelineIdDeltaMap<TValue> previousPublishedDelta = _publishedIdDelta;
        PersistentTimelineIdDeltaMap<TValue>.Mutation publishedDeltaMutation =
            previousPublishedDelta.ApplyMutation(deltaChanges);
        _publishedIdDelta = publishedDeltaMutation.Map;
        if (useValueOverlay)
        {
            if (_spatialBaseDelta.IsEmpty
                && ReferenceEquals(previousPublishedDelta, _spatialValueOverlay))
            {
                ApplySpatialOverlayMutation(publishedDeltaMutation);
            }
            else
            {
                Dictionary<MidoraId, PagedTimelineIdDelta<TValue>?> overlayChanges =
                    new(affectedIds.Count);
                foreach (MidoraId id in affectedIds)
                {
                    bool baseExists = TryGetSpatialBaseValue(id, out TValue? baseValue);
                    if (finalValues.TryGetValue(id, out TValue? finalValue))
                    {
                        overlayChanges[id] = baseExists
                            && EqualityComparer<TValue>.Default.Equals(baseValue, finalValue)
                                ? null
                                : new(true, finalValue);
                    }
                    else
                    {
                        overlayChanges[id] = baseExists
                            ? new(false, default!)
                            : null;
                    }
                }
                ApplySpatialOverlayChanges(overlayChanges);
            }
        }
        else
        {
            _spatialBaseDelta = _spatialBaseDelta.Apply(deltaChanges);
            Dictionary<MidoraId, PagedTimelineIdDelta<TValue>?> overlayRemovals =
                affectedIds.ToDictionary(static id => id,
                    static _ => (PagedTimelineIdDelta<TValue>?)null);
            ApplySpatialOverlayChanges(overlayRemovals);
        }
        _publishedSequence = mutation.Sequence;
        _publishedSnapshot = null;
    }

    private void ApplyStableValueDelta(
        IReadOnlyList<PersistentTimelineSequence<TValue>.ValueReplacement> replacements,
        bool useValueOverlay)
    {
        PersistentTimelineIdDeltaMap<TValue>.Change[] deltaChanges =
            new PersistentTimelineIdDeltaMap<TValue>.Change[replacements.Count];
        for (int index = 0; index < replacements.Count; index++)
        {
            TValue value = replacements[index].Replacement;
            MidoraId id = _getId(value);
            deltaChanges[index] = new(
                id,
                _publishedBaseById!.TryGetValue(id, out TValue? baseValue)
                    && EqualityComparer<TValue>.Default.Equals(baseValue, value)
                        ? null
                        : new PagedTimelineIdDelta<TValue>(true, value));
        }

        PersistentTimelineIdDeltaMap<TValue> previousPublishedDelta = _publishedIdDelta;
        PersistentTimelineIdDeltaMap<TValue>.Mutation publishedDeltaMutation =
            previousPublishedDelta.ApplyMutation(deltaChanges);
        _publishedIdDelta = publishedDeltaMutation.Map;
        if (useValueOverlay)
        {
            if (_spatialBaseDelta.IsEmpty
                && ReferenceEquals(previousPublishedDelta, _spatialValueOverlay))
            {
                ApplySpatialOverlayMutation(publishedDeltaMutation);
                return;
            }

            PersistentTimelineIdDeltaMap<TValue>.Change[] overlayChanges =
                new PersistentTimelineIdDeltaMap<TValue>.Change[replacements.Count];
            for (int index = 0; index < replacements.Count; index++)
            {
                TValue value = replacements[index].Replacement;
                MidoraId id = _getId(value);
                overlayChanges[index] = new(
                    id,
                    TryGetSpatialBaseValue(id, out TValue? baseValue)
                        && EqualityComparer<TValue>.Default.Equals(baseValue, value)
                            ? null
                            : new PagedTimelineIdDelta<TValue>(true, value));
            }
            ApplySpatialOverlayChanges(overlayChanges);
            return;
        }

        _spatialBaseDelta = _spatialBaseDelta.ApplyMutation(deltaChanges).Map;
        PersistentTimelineIdDeltaMap<TValue>.Change[] removals =
            new PersistentTimelineIdDeltaMap<TValue>.Change[replacements.Count];
        for (int index = 0; index < replacements.Count; index++)
        {
            removals[index] = new(
                _getId(replacements[index].Replacement),
                null);
        }
        ApplySpatialOverlayChanges(removals);
    }

    private bool CanPublishThroughValueOverlay(
        PersistentTimelineSequence<TValue>.Mutation mutation)
    {
        if (mutation.ValueReplacements.Count == 0) return false;
        if (_getDiscoveryKeys is null) return true;
        foreach (PersistentTimelineSequence<TValue>.ValueReplacement replacement in
            mutation.ValueReplacements)
        {
            if (!_getDiscoveryKeys(replacement.Expected)
                .SequenceEqual(_getDiscoveryKeys(replacement.Replacement)))
            {
                return false;
            }
        }
        return true;
    }

    private bool TryGetSpatialBaseValue(MidoraId id, out TValue value)
    {
        if (_spatialBaseDelta.TryGetValue(id, out PagedTimelineIdDelta<TValue> delta))
        {
            value = delta.Value;
            return delta.Exists;
        }
        return _publishedBaseById!.TryGetValue(id, out value!);
    }

    private void ApplySpatialOverlayChanges(
        IReadOnlyDictionary<MidoraId, PagedTimelineIdDelta<TValue>?> changes)
    {
        if (changes.Count == 0) return;
        PersistentTimelineIdDeltaMap<TValue>.Mutation mutation =
            _spatialValueOverlay.ApplyMutation(changes);
        ApplySpatialOverlayMutation(mutation);
    }

    private void ApplySpatialOverlayChanges(
        PersistentTimelineIdDeltaMap<TValue>.Change[] changes)
    {
        if (changes.Length == 0) return;
        PersistentTimelineIdDeltaMap<TValue>.Mutation mutation =
            _spatialValueOverlay.ApplyMutation(changes);
        ApplySpatialOverlayMutation(mutation);
    }

    private void ApplySpatialOverlayMutation(
        PersistentTimelineIdDeltaMap<TValue>.Mutation mutation)
    {
        List<PagedTimelineValuePage<TValue>> removedPages = [];
        foreach (PersistentTimelineIdDeltaMap<TValue>.Bucket bucket in mutation.RemovedBuckets)
        {
            if (_spatialOverlayPages.TryGetValue(bucket, out PagedTimelineValuePage<TValue>? page))
            {
                removedPages.Add(page);
                _spatialOverlayPages = _spatialOverlayPages.Remove(bucket);
            }
        }
        List<PagedTimelineValuePage<TValue>> addedPages = [];
        foreach (PersistentTimelineIdDeltaMap<TValue>.Bucket bucket in mutation.AddedBuckets)
        {
            TValue[] values = bucket.CopyExistingValues();
            if (values.Length == 0) continue;
            PagedTimelineValuePage<TValue> page = CreatePublishedPage(values);
            _spatialOverlayPages = _spatialOverlayPages.Add(bucket, page);
            addedPages.Add(page);
        }
        _spatialOverlayIndex = _spatialOverlayIndex.ReplacePages(removedPages, addedPages);
        _spatialValueOverlay = mutation.Map;
    }

    private PagedTimelineValuePage<TValue> CreatePublishedPage(
        PersistentTimelineSequence<TValue>.Leaf leaf) =>
        new(leaf.Replacements.Count == 0 ? leaf.BaseValues
                : new TimelineValueBuffer<TValue>(new LeafValueSource(leaf), 0, leaf.Count),
            _getStart, _getEnd, _getLane, _getFingerprint, _getCategoryMask,
            _getRasterValue, _getDiscoveryKeys);

    private PagedTimelineValuePage<TValue> CreatePublishedPage(TValue[] values) =>
        new(
            values,
            _getStart,
            _getEnd,
            _getLane,
            _getFingerprint,
            _getCategoryMask,
            _getRasterValue,
            _getDiscoveryKeys);

    private void ResetPublishedStateToEmpty()
    {
        lock (_snapshotPublicationSync)
        {
            _publishedSequence = PersistentTimelineSequence<TValue>.Empty(_getFingerprint);
            _publishedLeafPages = _publishedLeafPages.Clear();
            _spatialIndex = PagedTimelineSpatialBlockIndex<TValue>.Empty;
            _discoveryKeyPageCounts = _discoveryKeyPageCounts.Clear();
            _publishedBaseById = FrozenDictionary<MidoraId, TValue>.Empty;
            _publishedIdDelta = PersistentTimelineIdDeltaMap<TValue>.Empty;
            _spatialBaseDelta = PersistentTimelineIdDeltaMap<TValue>.Empty;
            _spatialValueOverlay = PersistentTimelineIdDeltaMap<TValue>.Empty;
            _spatialOverlayPages = _spatialOverlayPages.Clear();
            _spatialOverlayIndex = PagedTimelineSpatialBlockIndex<TValue>.Empty;
            _pendingPublishedValueIds.Clear();
            _publishedSnapshot = null;
        }
    }

    private Page AddPage()
    {
        Page result = new();
        _pages.Add(result);
        _livePages.Add(result);
        InvalidatePageDirectory();
        return result;
    }

    private void Split(Page page)
    {
        EnsurePageDirectory();
        int index = _pageIndices[page];
        int splitAt = page.Items.Count / 2;
        Page right = new();
        right.Items.AddRange(page.Items.GetRange(splitAt, page.Items.Count - splitAt));
        page.Items.RemoveRange(splitAt, page.Items.Count - splitAt);
        _pages.Insert(index + 1, right);
        _livePages.Add(right);
        InvalidatePageDirectory();
        ReindexPage(page, splitAt);
        for (int localIndex = 0; localIndex < right.Items.Count; localIndex++)
        {
            T item = right.Items[localIndex];
            if (_duplicateReferenceCounts?.ContainsKey(item) == true) continue;
            Entry entry = _entries[_getId(_toValue(item))];
            entry.Page = right;
            entry.LocalIndex = localIndex;
        }
        if (_duplicateReferenceCounts is not null)
        {
            HashSet<T> movedDuplicates = new(ReferenceEqualityComparer.Instance);
            foreach (T item in right.Items)
            {
                if (_duplicateReferenceCounts.ContainsKey(item)) movedDuplicates.Add(item);
            }
            foreach (T item in movedDuplicates) RefreshDuplicateReference(item);
        }
        MarkChanged(page);
        MarkChanged(right);
    }

    private void Attach(T item, Page page, int localIndex)
    {
        MidoraId id = _getId(_toValue(item));
        if (_entries.TryGetValue(id, out Entry? existing))
        {
            if (!ReferenceEquals(existing.Item, item))
                throw new InvalidOperationException("A paged timeline collection cannot contain distinct objects with duplicate Stable IDs.");
            _duplicateReferenceCounts ??= new(ReferenceEqualityComparer.Instance);
            _duplicateReferenceCounts[item] = _duplicateReferenceCounts.TryGetValue(item, out int count)
                ? checked(count + 1)
                : 2;
            existing.LocalIndex = -1;
        }
        else
        {
            _entries.Add(id, new(item, page, localIndex));
        }
        _setChangeSink(item, OnItemChanged);
    }

    private void ReindexPage(Page page, int first)
    {
        for (int localIndex = Math.Max(0, first); localIndex < page.Items.Count; localIndex++)
        {
            T item = page.Items[localIndex];
            if (_duplicateReferenceCounts?.ContainsKey(item) == true) continue;
            if (_entries.TryGetValue(_getId(_toValue(item)), out Entry? entry)
                && ReferenceEquals(entry.Item, item))
            {
                entry.Page = page;
                entry.LocalIndex = localIndex;
            }
        }
    }

    private void Detach(T item)
    {
        if (_duplicateReferenceCounts?.ContainsKey(item) == true)
        {
            RefreshDuplicateReference(item);
            return;
        }
        _entries.Remove(_getId(_toValue(item)));
        _setChangeSink(item, null);
    }

    private void OnItemChanged(T item)
    {
        if (_duplicateReferenceCounts?.ContainsKey(item) == true)
        {
            foreach (Page page in _pages)
            {
                if (page.Items.Any(value => ReferenceEquals(value, item))) MarkChanged(page);
            }
            return;
        }
        MidoraId id = _getId(_toValue(item));
        if (_entries.TryGetValue(id, out Entry? entry)
            && ReferenceEquals(entry.Item, item))
        {
            if (_publishedSequence is not null)
            {
                lock (_snapshotPublicationSync)
                    _pendingPublishedValueIds.Add(id);
            }
            MarkChanged(entry.Page);
        }
    }

    private void EnsureInsertable(T value, T? replacing = null)
    {
        MidoraId id = _getId(_toValue(value));
        if (id == default) throw new ArgumentOutOfRangeException(nameof(id));
        if (_entries.TryGetValue(id, out Entry? existing)
            && !ReferenceEquals(existing.Item, value)
            && (!ReferenceEquals(existing.Item, replacing)
                || replacing is not null
                && _duplicateReferenceCounts?.ContainsKey(replacing) == true))
        {
            throw new InvalidOperationException("A paged timeline collection cannot contain duplicate Stable IDs.");
        }
    }

    private void RefreshDuplicateReference(T item)
    {
        MidoraId id = _getId(_toValue(item));
        Page? firstPage = null;
        int count = 0;
        foreach (Page page in _pages)
        {
            foreach (T candidate in page.Items)
            {
                if (!ReferenceEquals(candidate, item)) continue;
                firstPage ??= page;
                count++;
            }
        }
        if (count == 0)
        {
            _entries.Remove(id);
            _duplicateReferenceCounts!.Remove(item);
            _setChangeSink(item, null);
            return;
        }
        int firstLocalIndex = firstPage!.Items.FindIndex(value => ReferenceEquals(value, item));
        _entries[id] = new(item, firstPage, count == 1 ? firstLocalIndex : -1);
        if (count == 1)
            _duplicateReferenceCounts!.Remove(item);
        else
            _duplicateReferenceCounts![item] = count;
    }

    private (Page Page, int LocalIndex) Locate(int index, bool allowEnd)
    {
        if (index < 0 || index > _count || !allowEnd && index == _count)
            throw new ArgumentOutOfRangeException(nameof(index));
        EnsurePageDirectory();
        if (allowEnd && index == _count)
        {
            if (_pages.Count == 0) throw new ArgumentOutOfRangeException(nameof(index));
            return (_pages[^1], _pages[^1].Items.Count);
        }
        int low = 0;
        int high = _pages.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (_pageStarts[middle + 1] <= index)
                low = middle + 1;
            else
                high = middle;
        }
        if ((uint)low >= (uint)_pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return (_pages[low], checked(index - _pageStarts[low]));
    }

    private void EnsurePageDirectory()
    {
        if (!_pageDirectoryDirty) return;
        int[] starts = new int[_pages.Count + 1];
        Dictionary<Page, int> indices = new(_pages.Count);
        int start = 0;
        for (int index = 0; index < _pages.Count; index++)
        {
            Page page = _pages[index];
            starts[index] = start;
            indices.Add(page, index);
            start = checked(start + page.Items.Count);
        }
        starts[^1] = start;
        if (start != _count)
            throw new InvalidOperationException("The paged timeline page directory is inconsistent.");
        _pageStarts = starts;
        _pageIndices = indices;
        _pageDirectoryDirty = false;
    }

    private void InvalidatePageDirectory() => _pageDirectoryDirty = true;

    private void MarkChanged(Page page)
    {
        _dirtyPages.Add(page);
        Touch();
    }

    private void Touch()
    {
        if (_batchDepth != 0)
        {
            _batchChanged = true;
            return;
        }
        PublishPendingValueChanges();
        _generation++;
        _publishedSnapshot = null;
    }

    private void FlushDirtyPages()
    {
        foreach (Page page in _dirtyPages)
        {
            if (!_livePages.Contains(page)) continue;
            TValue[] values = page.Items.Select(_toValue).ToArray();
            PagedTimelineValuePage<TValue> snapshot = new(
                values,
                _getStart,
                _getEnd,
                _getLane,
                _getFingerprint,
                _getCategoryMask,
                _getRasterValue,
                _getDiscoveryKeys);
            RemoveDiscoveryKeys(page.Snapshot);
            AddDiscoveryKeys(snapshot);
            _spatialIndex = _spatialIndex.ReplacePage(page.Snapshot, snapshot);
            page.Snapshot = snapshot;
        }
        _dirtyPages.Clear();
    }

    private void RetirePage(Page page)
    {
        if (page.Snapshot is null) return;
        RemoveDiscoveryKeys(page.Snapshot);
        _spatialIndex = _spatialIndex.ReplacePage(page.Snapshot, replacement: null);
        page.Snapshot = null;
    }

    private void AddDiscoveryKeys(PagedTimelineValuePage<TValue> page)
    {
        foreach (var (key, occurrences) in page.DiscoveryCounts)
        {
            _discoveryKeyPageCounts = _discoveryKeyPageCounts.SetItem(key,
                _discoveryKeyPageCounts.TryGetValue(key, out int count) ? checked(count + occurrences) : occurrences);
        }
    }

    private void RemoveDiscoveryKeys(PagedTimelineValuePage<TValue>? page)
    {
        if (page is null) return;
        foreach (var (key, occurrences) in page.DiscoveryCounts)
        {
            int count = _discoveryKeyPageCounts[key];
            _discoveryKeyPageCounts = count == occurrences
                ? _discoveryKeyPageCounts.Remove(key)
                : _discoveryKeyPageCounts.SetItem(key, checked(count - occurrences));
        }
    }

    private void EndBatch()
    {
        if (_batchDepth <= 0) throw new InvalidOperationException("Paged timeline batch scope is unbalanced.");
        _batchDepth--;
        if (_batchDepth != 0 || !_batchChanged) return;
        _batchChanged = false;
        PublishPendingValueChanges();
        _generation++;
        _publishedSnapshot = null;
    }

    private sealed class Page
    {
        public List<T> Items { get; } = new(DefaultPageCapacity);
        public PagedTimelineValuePage<TValue>? Snapshot { get; set; }
    }

    private sealed class Entry(T item, Page page, int localIndex)
    {
        public T Item { get; } = item;
        public Page Page { get; set; } = page;
        public int LocalIndex { get; set; } = localIndex;
    }

    private readonly record struct CollisionRemoval(int OriginalIndex, T Value);

    private sealed class BatchScope(PagedTimelineObjectList<T, TValue> owner) : IDisposable
    {
        private PagedTimelineObjectList<T, TValue>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndBatch();
    }
}

internal sealed class PagedTimelineValuePage<TValue>
{
    private static long s_nextSpatialIdentity;
    private static readonly int[][] s_identitySpatialOrders = Enumerable.Range(0, 129)
        .Select(static count => Enumerable.Range(0, count).ToArray()).ToArray();
    private const int FingerprintBlockSize = 128;
    private const int SpatialBlockSize = 128;
    private readonly Func<TValue, long> _getStart;
    private readonly Func<TValue, long> _getEnd;
    private readonly Func<TValue, int> _getLane;
    private readonly Func<TValue, ulong> _getFingerprint;
    private readonly Func<TValue, ulong>? _getCategoryMask;
    private readonly Func<TValue, double>? _getRasterValue;
    private readonly FingerprintBlock[] _fingerprintBlocks;
    private readonly int[] _spatialOrder;

    public PagedTimelineValuePage(
        TValue[] values,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint,
        Func<TValue, ulong>? getCategoryMask,
        Func<TValue, double>? getRasterValue,
        Func<TValue, IEnumerable<long>>? getDiscoveryKeys)
        : this(new TimelineValueBuffer<TValue>(values), getStart, getEnd, getLane,
            getFingerprint, getCategoryMask, getRasterValue, getDiscoveryKeys)
    {
    }

    public PagedTimelineValuePage(
        TimelineValueBuffer<TValue> buffer,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint,
        Func<TValue, ulong>? getCategoryMask,
        Func<TValue, double>? getRasterValue,
        Func<TValue, IEnumerable<long>>? getDiscoveryKeys)
    {
        SpatialIdentity = Interlocked.Increment(ref s_nextSpatialIdentity);
        Values = buffer;
        TValue[] values = buffer.ToArray();
        _getStart = getStart;
        _getEnd = getEnd;
        _getLane = getLane;
        _getFingerprint = getFingerprint;
        _getCategoryMask = getCategoryMask;
        _getRasterValue = getRasterValue;
        if (values.Length == 0)
        {
            MinimumStartTick = long.MaxValue;
            MaximumEndTick = 0;
            MinimumLane = int.MaxValue;
            MaximumLane = int.MinValue;
            ContentFingerprint = PagedTimelineFingerprint.Offset;
            ContentAggregate = default;
            CategoryMask = 0;
            _fingerprintBlocks = [];
            _spatialOrder = [];
            SpatialBlocks = [];
            DiscoveryKeys = [];
            DiscoveryCounts = FrozenDictionary<long, int>.Empty;
            return;
        }
        long minimumStart = long.MaxValue;
        long maximumEnd = 0;
        int minimumLane = int.MaxValue;
        int maximumLane = int.MinValue;
        ulong fingerprint = PagedTimelineFingerprint.Offset;
        PagedTimelineSequenceFingerprintAggregate contentAggregate = default;
        ulong categoryMask = getCategoryMask is null ? ulong.MaxValue : 0;
        foreach (TValue value in values)
        {
            minimumStart = Math.Min(minimumStart, getStart(value));
            maximumEnd = Math.Max(maximumEnd, getEnd(value));
            int lane = getLane(value);
            minimumLane = Math.Min(minimumLane, lane);
            maximumLane = Math.Max(maximumLane, lane);
            ulong valueFingerprint = getFingerprint(value);
            PagedTimelineFingerprint.Add(ref fingerprint, valueFingerprint);
            contentAggregate.Add(valueFingerprint);
            if (getCategoryMask is not null) categoryMask |= getCategoryMask(value);
        }
        PagedTimelineFingerprint.Add(ref fingerprint, unchecked((ulong)values.Length));
        MinimumStartTick = minimumStart;
        MaximumEndTick = maximumEnd;
        MinimumLane = minimumLane;
        MaximumLane = maximumLane;
        ContentFingerprint = fingerprint;
        ContentAggregate = contentAggregate;
        CategoryMask = categoryMask;
        _fingerprintBlocks = BuildFingerprintBlocks(values);
        _spatialOrder = BuildSpatialOrder(values);
        SpatialBlocks = BuildSpatialBlocks(values, _spatialOrder);
        // Count targets while building this bounded page, not all event records
        // again when a Lane directory opens. A value may expose several targets.
        Dictionary<long, int> discovery = [];
        if (getDiscoveryKeys is not null)
            foreach (TValue value in values)
                foreach (long key in getDiscoveryKeys(value))
                    discovery[key] = discovery.TryGetValue(key, out int count) ? checked(count + 1) : 1;
        DiscoveryCounts = discovery.ToFrozenDictionary();
        DiscoveryKeys = discovery.Keys.Order().ToArray();
    }

    public TimelineValueBuffer<TValue> Values { get; }
    public long SpatialIdentity { get; }
    public long MinimumStartTick { get; }
    public long MaximumEndTick { get; }
    public int MinimumLane { get; }
    public int MaximumLane { get; }
    public ulong ContentFingerprint { get; }
    public PagedTimelineSequenceFingerprintAggregate ContentAggregate { get; }
    public ulong CategoryMask { get; }
    public PagedTimelineSpatialBlockMetadata[] SpatialBlocks { get; }
    public long[] DiscoveryKeys { get; }
    public IReadOnlyDictionary<long, int> DiscoveryCounts { get; }

    public void AppendSpatialRangeValues(
        List<PagedTimelineOrderedValue<TValue>> destination,
        int pageIndex,
        PagedTimelineSpatialBlockMetadata block,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        int end = checked(block.First + block.Count);
        for (int spatialIndex = block.First; spatialIndex < end; spatialIndex++)
        {
            int localIndex = _spatialOrder[spatialIndex];
            TValue value = Values[localIndex];
            if (_getStart(value) >= endTick) break;
            int lane = _getLane(value);
            ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
            if (_getEnd(value) > startTick
                && lane >= minimumLane
                && lane <= maximumLane
                && (categoryMask & requiredCategoryMask) != 0)
            {
                destination.Add(new(pageIndex, localIndex, value));
            }
        }
    }

    public long GetMaximumEndTickExcluding(
        PagedTimelineSpatialBlockMetadata block,
        PersistentTimelineIdDeltaMap<TValue> excluded,
        Func<TValue, MidoraId> getId)
    {
        long maximum = 0;
        int end = checked(block.First + block.Count);
        for (int index = block.First; index < end; index++)
        {
            TValue value = Values[_spatialOrder[index]];
            if (excluded.TryGetValue(getId(value), out _)) continue;
            maximum = Math.Max(maximum, _getEnd(value));
        }
        return maximum;
    }

    public void AppendExactStartValues(
        List<TValue> destination,
        PagedTimelineSpatialBlockMetadata block,
        long tick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        int end = checked(block.First + block.Count);
        for (int spatialIndex = block.First; spatialIndex < end; spatialIndex++)
        {
            int localIndex = _spatialOrder[spatialIndex];
            TValue value = Values[localIndex];
            long start = _getStart(value);
            if (start < tick) continue;
            if (start > tick) break;
            int lane = _getLane(value);
            ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
            if (lane >= minimumLane
                && lane <= maximumLane
                && (categoryMask & requiredCategoryMask) != 0)
            {
                destination.Add(value);
            }
        }
    }

    public void AccumulateSpatialRangeFingerprint(
        ref PagedTimelineRangeFingerprintAggregate aggregate,
        PagedTimelineSpatialBlockMetadata block,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        if (startTick <= block.MinimumStartTick
            && endTick >= block.MaximumEndTick
            && minimumLane <= block.MinimumLane
            && maximumLane >= block.MaximumLane
            && (requiredCategoryMask == ulong.MaxValue
                || (block.CategoryMask & ~requiredCategoryMask) == 0))
        {
            aggregate.Combine(block.Aggregate);
            return;
        }
        int end = checked(block.First + block.Count);
        for (int spatialIndex = block.First; spatialIndex < end; spatialIndex++)
        {
            int localIndex = _spatialOrder[spatialIndex];
            TValue value = Values[localIndex];
            if (_getStart(value) >= endTick) break;
            int lane = _getLane(value);
            ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
            if (_getEnd(value) > startTick
                && lane >= minimumLane
                && lane <= maximumLane
                && (categoryMask & requiredCategoryMask) != 0)
            {
                aggregate.Add(_getFingerprint(value));
            }
        }
    }

    public void AccumulateSpatialRangeFingerprintExcluding(
        ref PagedTimelineRangeFingerprintAggregate aggregate,
        PagedTimelineSpatialBlockMetadata block,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        PersistentTimelineIdDeltaMap<TValue> excluded,
        Func<TValue, MidoraId> getId)
    {
        int blockEnd = checked(block.First + block.Count);
        bool hasExclusion = false;
        for (int spatialIndex = block.First; spatialIndex < blockEnd; spatialIndex++)
        {
            TValue value = Values[_spatialOrder[spatialIndex]];
            if (!excluded.TryGetValue(getId(value), out _)) continue;
            hasExclusion = true;
            break;
        }
        if (!hasExclusion)
        {
            AccumulateSpatialRangeFingerprint(
                ref aggregate,
                block,
                startTick,
                endTick,
                minimumLane,
                maximumLane,
                requiredCategoryMask);
            return;
        }
        for (int spatialIndex = block.First; spatialIndex < blockEnd; spatialIndex++)
        {
            TValue value = Values[_spatialOrder[spatialIndex]];
            if (excluded.TryGetValue(getId(value), out _)) continue;
            if (_getStart(value) >= endTick) break;
            int lane = _getLane(value);
            ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
            if (_getEnd(value) > startTick
                && lane >= minimumLane
                && lane <= maximumLane
                && (categoryMask & requiredCategoryMask) != 0)
            {
                aggregate.Add(_getFingerprint(value));
            }
        }
    }

    public int AccumulateSpatialBlockRasterColumns(
        PagedTimelineSpatialBlockMetadata block,
        TimelineRasterColumnProjection projection,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int visited = 0;
        int end = checked(block.First + block.Count);
        for (int spatialIndex = block.First; spatialIndex < end; spatialIndex++)
        {
            if ((spatialIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            int localIndex = _spatialOrder[spatialIndex];
            TValue value = Values[localIndex];
            if (_getStart(value) >= projection.EndTick) break;
            int lane = _getLane(value);
            ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
            if (_getEnd(value) <= projection.StartTick
                || lane < minimumLane
                || lane > maximumLane
                || (categoryMask & requiredCategoryMask) == 0)
            {
                continue;
            }
            double rasterValue = _getRasterValue?.Invoke(value) ?? 0;
            if (!double.IsFinite(rasterValue)) rasterValue = 0;
            PagedTimelineRasterProjection.IncludeExact(
                destination,
                projection,
                _getStart(value),
                _getEnd(value),
                lane is >= 0 and < 64 ? 1UL << lane : 0,
                lane is >= 64 and < 128 ? 1UL << (lane - 64) : 0,
                rasterValue,
                rasterValue,
                1);
            visited++;
        }
        return visited;
    }

    public int AccumulateSpatialBlockRasterColumnsExcluding(
        PagedTimelineSpatialBlockMetadata block,
        TimelineRasterColumnProjection projection,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        PersistentTimelineIdDeltaMap<TValue> excluded,
        Func<TValue, MidoraId> getId,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int visited = 0;
        int end = checked(block.First + block.Count);
        for (int spatialIndex = block.First; spatialIndex < end; spatialIndex++)
        {
            if ((spatialIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            TValue value = Values[_spatialOrder[spatialIndex]];
            if (excluded.TryGetValue(getId(value), out _)) continue;
            if (_getStart(value) >= projection.EndTick) break;
            int lane = _getLane(value);
            ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
            if (_getEnd(value) <= projection.StartTick
                || lane < minimumLane
                || lane > maximumLane
                || (categoryMask & requiredCategoryMask) == 0)
            {
                continue;
            }
            double rasterValue = _getRasterValue?.Invoke(value) ?? 0;
            if (!double.IsFinite(rasterValue)) rasterValue = 0;
            PagedTimelineRasterProjection.IncludeExact(
                destination,
                projection,
                _getStart(value),
                _getEnd(value),
                lane is >= 0 and < 64 ? 1UL << lane : 0,
                lane is >= 64 and < 128 ? 1UL << (lane - 64) : 0,
                rasterValue,
                rasterValue,
                1);
            visited++;
        }
        return visited;
    }

    public void AccumulateRangeFingerprint(
        ref PagedTimelineRangeFingerprintAggregate aggregate,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        foreach (FingerprintBlock block in _fingerprintBlocks)
        {
            if (block.MaximumEndTick <= startTick
                || block.MinimumStartTick >= endTick
                || block.MaximumLane < minimumLane
                || block.MinimumLane > maximumLane
                || (block.CategoryMask & requiredCategoryMask) == 0)
            {
                continue;
            }
            if (startTick <= block.MinimumStartTick
                && endTick >= block.MaximumEndTick
                && minimumLane <= block.MinimumLane
                && maximumLane >= block.MaximumLane
                && (requiredCategoryMask == ulong.MaxValue
                    || (block.CategoryMask & ~requiredCategoryMask) == 0))
            {
                aggregate.Combine(block.Aggregate);
                continue;
            }
            int end = checked(block.First + block.Count);
            for (int index = block.First; index < end; index++)
            {
                TValue value = Values[index];
                int lane = _getLane(value);
                ulong categoryMask = _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
                if (_getStart(value) < endTick
                    && _getEnd(value) > startTick
                    && lane >= minimumLane
                    && lane <= maximumLane
                    && (categoryMask & requiredCategoryMask) != 0)
                {
                    aggregate.Add(_getFingerprint(value));
                }
            }
        }
    }

    private FingerprintBlock[] BuildFingerprintBlocks(TValue[] values)
    {
        FingerprintBlock[] result = new FingerprintBlock[
            (values.Length + FingerprintBlockSize - 1) / FingerprintBlockSize];
        for (int blockIndex = 0; blockIndex < result.Length; blockIndex++)
        {
            int first = checked(blockIndex * FingerprintBlockSize);
            int count = Math.Min(FingerprintBlockSize, values.Length - first);
            long minimumStartTick = long.MaxValue;
            long maximumEndTick = 0;
            int minimumLane = int.MaxValue;
            int maximumLane = int.MinValue;
            ulong categoryMask = _getCategoryMask is null ? ulong.MaxValue : 0;
            PagedTimelineRangeFingerprintAggregate aggregate = default;
            for (int index = first; index < first + count; index++)
            {
                TValue value = values[index];
                minimumStartTick = Math.Min(minimumStartTick, _getStart(value));
                maximumEndTick = Math.Max(maximumEndTick, _getEnd(value));
                int lane = _getLane(value);
                minimumLane = Math.Min(minimumLane, lane);
                maximumLane = Math.Max(maximumLane, lane);
                categoryMask |= _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
                aggregate.Add(_getFingerprint(value));
            }
            result[blockIndex] = new(
                first,
                count,
                minimumStartTick,
                maximumEndTick,
                minimumLane,
                maximumLane,
                categoryMask,
                aggregate);
        }
        return result;
    }

    private int[] BuildSpatialOrder(TValue[] values)
    {
        bool alreadyOrdered = true;
        for (int index = 1; index < values.Length; index++)
        {
            int start = _getStart(values[index - 1]).CompareTo(_getStart(values[index]));
            if (start < 0) continue;
            if (start == 0
                && _getEnd(values[index - 1]) <= _getEnd(values[index]))
            {
                continue;
            }
            alreadyOrdered = false;
            break;
        }
        if (alreadyOrdered && values.Length < s_identitySpatialOrders.Length)
            return s_identitySpatialOrders[values.Length];
        int[] result = Enumerable.Range(0, values.Length).ToArray();
        if (alreadyOrdered) return result;
        Array.Sort(result, (left, right) =>
        {
            int start = _getStart(values[left]).CompareTo(_getStart(values[right]));
            if (start != 0) return start;
            int end = _getEnd(values[left]).CompareTo(_getEnd(values[right]));
            return end != 0 ? end : left.CompareTo(right);
        });
        return result;
    }

    private PagedTimelineSpatialBlockMetadata[] BuildSpatialBlocks(
        TValue[] values,
        int[] spatialOrder)
    {
        PagedTimelineSpatialBlockMetadata[] result = new PagedTimelineSpatialBlockMetadata[
            (spatialOrder.Length + SpatialBlockSize - 1) / SpatialBlockSize];
        for (int blockIndex = 0; blockIndex < result.Length; blockIndex++)
        {
            int first = checked(blockIndex * SpatialBlockSize);
            int count = Math.Min(SpatialBlockSize, spatialOrder.Length - first);
            long minimumStartTick = long.MaxValue;
            long maximumStartTick = long.MinValue;
            long maximumEndTick = 0;
            int minimumLane = int.MaxValue;
            int maximumLane = int.MinValue;
            ulong laneMaskLow = 0;
            ulong laneMaskHigh = 0;
            double minimumRasterValue = double.PositiveInfinity;
            double maximumRasterValue = double.NegativeInfinity;
            ulong categoryMask = _getCategoryMask is null ? ulong.MaxValue : 0;
            PagedTimelineRangeFingerprintAggregate aggregate = default;
            for (int index = first; index < first + count; index++)
            {
                TValue value = values[spatialOrder[index]];
                minimumStartTick = Math.Min(minimumStartTick, _getStart(value));
                maximumStartTick = Math.Max(maximumStartTick, _getStart(value));
                maximumEndTick = Math.Max(maximumEndTick, _getEnd(value));
                int lane = _getLane(value);
                minimumLane = Math.Min(minimumLane, lane);
                maximumLane = Math.Max(maximumLane, lane);
                if ((uint)lane < 64)
                    laneMaskLow |= 1UL << lane;
                else if ((uint)(lane - 64) < 64)
                    laneMaskHigh |= 1UL << (lane - 64);
                double rasterValue = _getRasterValue?.Invoke(value) ?? 0;
                if (double.IsFinite(rasterValue))
                {
                    minimumRasterValue = Math.Min(minimumRasterValue, rasterValue);
                    maximumRasterValue = Math.Max(maximumRasterValue, rasterValue);
                }
                categoryMask |= _getCategoryMask?.Invoke(value) ?? ulong.MaxValue;
                aggregate.Add(_getFingerprint(value));
            }
            if (!double.IsFinite(minimumRasterValue)) minimumRasterValue = 0;
            if (!double.IsFinite(maximumRasterValue)) maximumRasterValue = minimumRasterValue;
            result[blockIndex] = new(
                first,
                count,
                minimumStartTick,
                maximumStartTick,
                maximumEndTick,
                minimumLane,
                maximumLane,
                laneMaskLow,
                laneMaskHigh,
                minimumRasterValue,
                maximumRasterValue,
                categoryMask,
                aggregate);
        }
        return result;
    }

    private readonly record struct FingerprintBlock(
        int First,
        int Count,
        long MinimumStartTick,
        long MaximumEndTick,
        int MinimumLane,
        int MaximumLane,
        ulong CategoryMask,
        PagedTimelineRangeFingerprintAggregate Aggregate);
}

internal readonly record struct PagedTimelineSpatialBlockMetadata(
    int First,
    int Count,
    long MinimumStartTick,
    long MaximumStartTick,
    long MaximumEndTick,
    int MinimumLane,
    int MaximumLane,
    ulong LaneMaskLow,
    ulong LaneMaskHigh,
    double MinimumRasterValue,
    double MaximumRasterValue,
    ulong CategoryMask,
    PagedTimelineRangeFingerprintAggregate Aggregate);

internal static class PagedTimelineRasterProjection
{
    public static void Include(
        Span<TimelineRasterColumnSummary> destination,
        TimelineRasterColumnProjection projection,
        long contentStartTick,
        long contentEndTick,
        ulong laneMaskLow,
        ulong laneMaskHigh,
        double minimumValue,
        double maximumValue,
        int approximateSourceCount)
    {
        if (destination.IsEmpty
            || (laneMaskLow | laneMaskHigh) == 0)
        {
            return;
        }
        if (!projection.TryGetColumns(
                contentStartTick,
                contentEndTick,
                out int first,
                out int lastExclusive)) return;
        for (int column = first; column < lastExclusive; column++)
        {
            destination[column].Include(
                laneMaskLow,
                laneMaskHigh,
                minimumValue,
                maximumValue,
                approximateSourceCount);
        }
    }

    public static void IncludeExact(
        Span<TimelineRasterColumnSummary> destination,
        TimelineRasterColumnProjection projection,
        long contentStartTick,
        long contentEndTick,
        ulong laneMaskLow,
        ulong laneMaskHigh,
        double minimumValue,
        double maximumValue,
        int approximateSourceCount)
    {
        if (destination.IsEmpty
            || (laneMaskLow | laneMaskHigh) == 0
            || !projection.TryGetColumns(
                contentStartTick,
                contentEndTick,
                out int first,
                out int lastExclusive))
        {
            return;
        }
        for (int column = first; column < lastExclusive; column++)
        {
            destination[column].Include(
                laneMaskLow,
                laneMaskHigh,
                minimumValue,
                maximumValue,
                approximateSourceCount);
        }
        if (contentStartTick >= projection.StartTick
            && contentStartTick < projection.EndTick)
        {
            destination[first].IncludeStartBoundary(
                laneMaskLow,
                laneMaskHigh);
        }
        if (contentEndTick > projection.StartTick
            && contentEndTick <= projection.EndTick)
        {
            destination[lastExclusive - 1].IncludeEndBoundary(
                laneMaskLow,
                laneMaskHigh);
        }
    }

    public static void IncludeBoundaries(
        Span<TimelineRasterColumnSummary> destination,
        TimelineRasterColumnProjection projection,
        long contentStartTick,
        long contentEndTick,
        ulong laneMaskLow,
        ulong laneMaskHigh)
    {
        if (!projection.TryGetColumns(
                contentStartTick,
                contentEndTick,
                out int first,
                out int lastExclusive))
        {
            return;
        }
        if (contentStartTick >= projection.StartTick
            && contentStartTick < projection.EndTick)
        {
            destination[first].IncludeStartBoundary(
                laneMaskLow,
                laneMaskHigh);
        }
        if (contentEndTick > projection.StartTick
            && contentEndTick <= projection.EndTick)
        {
            destination[lastExclusive - 1].IncludeEndBoundary(
                laneMaskLow,
                laneMaskHigh);
        }
    }

    public static (ulong Low, ulong High) LaneRangeMask(int minimumLane, int maximumLane)
    {
        minimumLane = Math.Clamp(minimumLane, 0, 127);
        maximumLane = Math.Clamp(maximumLane, 0, 127);
        if (maximumLane < minimumLane) return default;
        ulong low = minimumLane >= 64
            ? 0
            : RangeBits(minimumLane, Math.Min(63, maximumLane));
        ulong high = maximumLane < 64
            ? 0
            : RangeBits(Math.Max(64, minimumLane) - 64, maximumLane - 64);
        return (low, high);
    }

    public static int ColumnSpan(
        TimelineRasterColumnProjection projection,
        long contentStartTick,
        long contentEndTick) => projection.ColumnSpan(contentStartTick, contentEndTick);

    private static ulong RangeBits(int first, int last)
    {
        ulong upper = last == 63 ? ulong.MaxValue : (1UL << (last + 1)) - 1;
        ulong lower = first == 0 ? 0 : (1UL << first) - 1;
        return upper & ~lower;
    }
}

internal readonly record struct PagedTimelineOrderedValue<TValue>(
    int PageIndex,
    int LocalIndex,
    TValue Value);

internal readonly record struct PagedTimelineIdDelta<TValue>(bool Exists, TValue Value);

/// <summary>
/// Immutable, structurally shared Stable-ID delta map. Timeline edits commonly
/// change tens of thousands of values in one command. Updating an
/// ImmutableDictionary one key at a time made that publication dominate the
/// edit and Undo paths. Fixed hash partitions let a batch rebuild each touched
/// partition once with a linear sorted merge while old snapshots retain all
/// untouched partitions by reference.
/// </summary>
internal sealed class PersistentTimelineIdDeltaMap<TValue>
{
    private const int BucketCount = 2048;
    private const int BucketMask = BucketCount - 1;
    private readonly Bucket?[] _buckets;
    private readonly int _count;

    private PersistentTimelineIdDeltaMap(Bucket?[] buckets, int count = 0)
    {
        _buckets = buckets;
        _count = count;
    }

    public static PersistentTimelineIdDeltaMap<TValue> Empty { get; } =
        new(new Bucket?[BucketCount]);

    public bool IsEmpty => _count == 0;

    public bool TryGetValue(MidoraId id, out PagedTimelineIdDelta<TValue> value)
    {
        Bucket? bucket = _buckets[GetBucketIndex(id)];
        if (bucket is not null)
            return bucket.TryGetValue(id, out value);
        value = default;
        return false;
    }

    public PersistentTimelineIdDeltaMap<TValue> Apply(
        IReadOnlyDictionary<MidoraId, PagedTimelineIdDelta<TValue>?> changes)
        => ApplyMutation(changes).Map;

    public Mutation ApplyMutation(
        IReadOnlyDictionary<MidoraId, PagedTimelineIdDelta<TValue>?> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return new(this, [], []);
        Change[] ordered = new Change[changes.Count];
        int write = 0;
        foreach ((MidoraId id, PagedTimelineIdDelta<TValue>? value) in changes)
            ordered[write++] = new(id, value);
        return ApplyMutation(ordered);
    }

    internal Mutation ApplyMutation(Change[] changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Length == 0) return new(this, [], []);

        int[] bucketOffsets = new int[BucketCount + 1];
        foreach (Change change in changes)
            bucketOffsets[GetBucketIndex(change.Id) + 1]++;
        for (int index = 1; index < bucketOffsets.Length; index++)
            bucketOffsets[index] = checked(bucketOffsets[index] + bucketOffsets[index - 1]);
        int[] writeOffsets = (int[])bucketOffsets.Clone();
        for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++)
        {
            int end = bucketOffsets[bucketIndex + 1];
            while (writeOffsets[bucketIndex] < end)
            {
                int position = writeOffsets[bucketIndex];
                int targetBucket = GetBucketIndex(changes[position].Id);
                if (targetBucket == bucketIndex)
                {
                    writeOffsets[bucketIndex]++;
                    continue;
                }
                int targetPosition = writeOffsets[targetBucket]++;
                (changes[position], changes[targetPosition]) =
                    (changes[targetPosition], changes[position]);
            }
        }

        Bucket?[] buckets = (Bucket?[])_buckets.Clone();
        int count = _count;
        List<Bucket> removed = [];
        List<Bucket> added = [];
        for (int bucketIndex = 0; bucketIndex < BucketCount; bucketIndex++)
        {
            int first = bucketOffsets[bucketIndex];
            int changeCount = bucketOffsets[bucketIndex + 1] - first;
            if (changeCount == 0) continue;
            Array.Sort(changes, first, changeCount, ChangeIdComparer.Instance);
            for (int index = first + 1; index < first + changeCount; index++)
            {
                if (changes[index - 1].Id == changes[index].Id)
                    throw new InvalidOperationException(
                        "A persistent timeline ID delta batch contains a duplicate ID.");
            }
            Bucket? oldBucket = _buckets[bucketIndex];
            Bucket? newBucket = Bucket.Merge(oldBucket, changes, first, changeCount);
            count = checked(count - (oldBucket?.Count ?? 0) + (newBucket?.Count ?? 0));
            if (oldBucket is not null) removed.Add(oldBucket);
            if (newBucket is not null) added.Add(newBucket);
            buckets[bucketIndex] = newBucket;
        }
        return new(new(buckets, count), removed, added);
    }

    private sealed class ChangeIdComparer : IComparer<Change>
    {
        public static ChangeIdComparer Instance { get; } = new();

        public int Compare(Change left, Change right) => left.Id.CompareTo(right.Id);
    }

    private static int GetBucketIndex(MidoraId id)
    {
        ulong value = unchecked((ulong)id.Value);
        value ^= value >> 33;
        value *= 0xff51afd7ed558ccdUL;
        value ^= value >> 33;
        return unchecked((int)value) & BucketMask;
    }

    internal readonly record struct Change(
        MidoraId Id,
        PagedTimelineIdDelta<TValue>? Value);

    private readonly record struct Entry(
        MidoraId Id,
        PagedTimelineIdDelta<TValue> Value);

    internal sealed class Bucket
    {
        private readonly Entry[] _entries;

        private Bucket(Entry[] entries) => _entries = entries;

        public int Count => _entries.Length;

        public IEnumerable<TValue> EnumerateExistingValues()
        {
            foreach (Entry entry in _entries)
                if (entry.Value.Exists) yield return entry.Value.Value;
        }

        public TValue[] CopyExistingValues()
        {
            int count = 0;
            foreach (Entry entry in _entries)
                if (entry.Value.Exists) count++;
            if (count == 0) return [];
            TValue[] result = new TValue[count];
            int write = 0;
            foreach (Entry entry in _entries)
            {
                if (entry.Value.Exists) result[write++] = entry.Value.Value;
            }
            return result;
        }

        public bool TryGetValue(MidoraId id, out PagedTimelineIdDelta<TValue> value)
        {
            int low = 0;
            int high = _entries.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                int comparison = _entries[middle].Id.CompareTo(id);
                if (comparison < 0) low = middle + 1;
                else high = middle;
            }
            if (low < _entries.Length && _entries[low].Id == id)
            {
                value = _entries[low].Value;
                return true;
            }
            value = default;
            return false;
        }

        internal static Bucket? Merge(
            Bucket? current,
            Change[] changes,
            int first,
            int count)
        {
            Entry[] existing = current?._entries ?? [];
            Entry[] result = new Entry[checked(existing.Length + count)];
            int existingIndex = 0;
            int changeIndex = first;
            int changeEnd = checked(first + count);
            int writeIndex = 0;
            while (existingIndex < existing.Length || changeIndex < changeEnd)
            {
                if (changeIndex == changeEnd)
                {
                    result[writeIndex++] = existing[existingIndex++];
                    continue;
                }
                Change change = changes[changeIndex];
                if (existingIndex == existing.Length)
                {
                    if (change.Value is { } inserted)
                        result[writeIndex++] = new(change.Id, inserted);
                    changeIndex++;
                    continue;
                }

                int comparison = existing[existingIndex].Id.CompareTo(change.Id);
                if (comparison < 0)
                {
                    result[writeIndex++] = existing[existingIndex++];
                    continue;
                }
                if (comparison == 0)
                {
                    if (change.Value is { } replacement)
                        result[writeIndex++] = new(change.Id, replacement);
                    existingIndex++;
                    changeIndex++;
                    continue;
                }
                if (change.Value is { } added)
                    result[writeIndex++] = new(change.Id, added);
                changeIndex++;
            }
            if (writeIndex == 0) return null;
            if (writeIndex != result.Length) Array.Resize(ref result, writeIndex);
            return new(result);
        }
    }

    internal readonly record struct Mutation(
        PersistentTimelineIdDeltaMap<TValue> Map,
        IReadOnlyList<Bucket> RemovedBuckets,
        IReadOnlyList<Bucket> AddedBuckets);
}

internal sealed partial class PagedTimelineValueSnapshot<TValue>
{
    internal object? EditableRoot { get; init; }
    public bool UsesExternalStorage => _sequence.UsesExternalStorage;

    public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = 0;
        foreach (TValue value in EnumerateRangeValues(query.StartTick, query.EndTick,
                     query.MinimumLane, query.MaximumLane, query.CategoryMask))
        {
            _ = value;
            if ((++count & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
    }
    private readonly PersistentTimelineSequence<TValue> _sequence;
    private readonly PagedTimelineSpatialBlockIndex<TValue> _spatialIndex;
    private readonly PagedTimelineSpatialBlockIndex<TValue> _spatialOverlayIndex;
    private readonly PersistentTimelineIdDeltaMap<TValue> _spatialValueOverlay;
    private readonly IReadOnlyDictionary<MidoraId, TValue> _baseById;
    private readonly PersistentTimelineIdDeltaMap<TValue> _idDelta;
    private readonly Func<TValue, MidoraId> _getId;
    private readonly Func<TValue, long> _getStart;
    private readonly Func<TValue, long> _getEnd;
    private readonly Func<TValue, int> _getLane;
    private readonly Func<TValue, ulong> _getFingerprint;
    private readonly Func<TValue, double>? _getRasterValue;

    public PagedTimelineValueSnapshot(
        PersistentTimelineSequence<TValue> sequence,
        PagedTimelineSpatialBlockIndex<TValue> spatialIndex,
        PagedTimelineSpatialBlockIndex<TValue> spatialOverlayIndex,
        PersistentTimelineIdDeltaMap<TValue> spatialValueOverlay,
        IReadOnlyDictionary<MidoraId, TValue> baseById,
        PersistentTimelineIdDeltaMap<TValue> idDelta,
        int count,
        long generation,
        Func<TValue, MidoraId> getId,
        Func<TValue, long> getStart,
        Func<TValue, long> getEnd,
        Func<TValue, int> getLane,
        Func<TValue, ulong> getFingerprint,
        Func<TValue, double>? getRasterValue,
        ImmutableDictionary<long, int> discoveryCounts)
    {
        _sequence = sequence;
        _spatialOverlayIndex = spatialOverlayIndex;
        _spatialValueOverlay = spatialValueOverlay;
        _baseById = baseById;
        _idDelta = idDelta;
        _getId = getId;
        _getStart = getStart;
        _getEnd = getEnd;
        _getLane = getLane;
        _getFingerprint = getFingerprint;
        _getRasterValue = getRasterValue;
        DiscoveryCounts = discoveryCounts;
        DiscoveryKeys = discoveryCounts.Keys.Order().ToArray();
        _spatialIndex = spatialIndex;
        Count = count;
        Generation = generation;
        long baseMaximumEndTick = spatialValueOverlay.IsEmpty
            ? spatialIndex.MaximumEndTick
            : spatialIndex.GetMaximumEndTickExcluding(spatialValueOverlay, getId);
        MaximumEndTick = Math.Max(baseMaximumEndTick, spatialOverlayIndex.MaximumEndTick);
        ContentFingerprint = sequence.ContentFingerprint;
    }

    public int Count { get; }
    public long Generation { get; }
    public long MaximumEndTick { get; }
    public ulong ContentFingerprint { get; }
    public IReadOnlyList<long> DiscoveryKeys { get; }
    public IReadOnlyDictionary<long, int> DiscoveryCounts { get; }
    internal int StorageLeafCount => _sequence.EnumerateLeaves().Count();
    internal void CollectSequenceStorageNodes(ISet<object> nodes) => _sequence.CollectStorageNodes(nodes);

    public IEnumerable<TValue> Query(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        if (endTick <= startTick || maximumLane < minimumLane) yield break;
        List<PagedTimelineOrderedValue<TValue>> matches = [];
        AppendRangeMatches(
            _spatialIndex,
            pageIndex: 0,
            matches,
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask);
        AppendRangeMatches(
            _spatialOverlayIndex,
            pageIndex: 1,
            matches,
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask);
        matches.Sort((left, right) =>
        {
            int start = _getStart(left.Value).CompareTo(_getStart(right.Value));
            if (start != 0) return start;
            int lane = _getLane(left.Value).CompareTo(_getLane(right.Value));
            if (lane != 0) return lane;
            int end = _getEnd(left.Value).CompareTo(_getEnd(right.Value));
            if (end != 0) return end;
            return _getId(left.Value).CompareTo(_getId(right.Value));
        });
        foreach (PagedTimelineOrderedValue<TValue> match in matches)
        {
            if (match.PageIndex == 0
                && _spatialValueOverlay.TryGetValue(_getId(match.Value), out _))
            {
                continue;
            }
            yield return match.Value;
        }
    }

    private static void AppendRangeMatches(
        PagedTimelineSpatialBlockIndex<TValue> index,
        int pageIndex,
        List<PagedTimelineOrderedValue<TValue>> destination,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        foreach (PagedTimelineSpatialBlockReference<TValue> candidate in index.Query(
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask))
        {
            candidate.Page.AppendSpatialRangeValues(
                destination,
                pageIndex,
                candidate.Block,
                startTick,
                endTick,
                minimumLane,
                maximumLane,
                requiredCategoryMask);
        }
    }

    public IEnumerable<TValue> EnumerateAll()
        => _sequence.Enumerate();

    public TValue GetByOrdinal(int ordinal) => _sequence[ordinal];
    public void PrepareOrdinalLookup(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (EditableRoot is ITimelineOrdinalLookup lookup) lookup.Prepare(token);
    }
    public void PrepareOrdinalLookup(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        token.ThrowIfCancellationRequested();
        if (EditableRoot is ITimelineOrdinalLookup lookup) lookup.Prepare(builder, token);
    }

    public bool TryGetPageByOrdinal(
        int firstOrdinal,
        int count,
        out TimelineObjectPage<TValue> page)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstOrdinal);
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (firstOrdinal >= Count)
        {
            page = default;
            return false;
        }
        int actualCount = Math.Min(count, Count - firstOrdinal);
        page = new(
            Generation,
            firstOrdinal,
            _sequence.CopyRange(firstOrdinal, actualCount));
        return true;
    }

    public int FindOrdinalAtOrAfterTick(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        return _sequence.FindIndex(value => _getStart(value) >= tick);
    }

    public bool TryFindOrdinalById(MidoraId id, out int ordinal)
    {
        if (id == default)
        {
            ordinal = -1;
            return false;
        }
        ordinal = EditableRoot is ITimelineOrdinalLookup index
            ? index.Find(id)
            : _sequence.FindIndex(value => _getId(value) == id);
        return ordinal >= 0;
    }

    public IReadOnlyList<TValue> ResolveByIds(IReadOnlyCollection<MidoraId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        List<TValue> result = new(ids.Count);
        foreach (MidoraId id in ids.Order())
        {
            if (_idDelta.TryGetValue(id, out PagedTimelineIdDelta<TValue> delta))
            {
                if (delta.Exists) result.Add(delta.Value);
                continue;
            }
            if (_baseById.TryGetValue(id, out TValue? value)) result.Add(value);
        }
        return result;
    }

    public IReadOnlyList<TValue> QueryExactStarts(
        IReadOnlySet<TimelineStartLaneKey> keys,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return [];
        List<TValue> result = [];
        foreach (TimelineStartLaneKey key in keys)
        {
            AppendExactStartValues(
                _spatialIndex,
                result,
                key.Tick,
                key.Lane,
                key.Lane,
                requiredCategoryMask,
                excludeOverlayValues: true);
            AppendExactStartValues(
                _spatialOverlayIndex,
                result,
                key.Tick,
                key.Lane,
                key.Lane,
                requiredCategoryMask,
                excludeOverlayValues: false);
        }
        return result;
    }

    public IReadOnlyList<TValue> QueryExactTicks(
        IReadOnlySet<long> ticks,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (ticks.Count == 0 || maximumLane < minimumLane) return [];
        List<TValue> result = [];
        foreach (long tick in ticks)
        {
            AppendExactStartValues(
                _spatialIndex,
                result,
                tick,
                minimumLane,
                maximumLane,
                requiredCategoryMask,
                excludeOverlayValues: true);
            AppendExactStartValues(
                _spatialOverlayIndex,
                result,
                tick,
                minimumLane,
                maximumLane,
                requiredCategoryMask,
                excludeOverlayValues: false);
        }
        return result;
    }

    internal IEnumerable<TValue> EnumerateListCandidates(long endTick, CancellationToken token)
    {
        List<PagedTimelineOrderedValue<TValue>> buffer = [];
        foreach (var value in Read(_spatialIndex, true)) yield return value;
        foreach (var value in Read(_spatialOverlayIndex, false)) yield return value;
        IEnumerable<TValue> Read(PagedTimelineSpatialBlockIndex<TValue> index, bool exclude)
        {
            foreach (var candidate in index.EnumerateListBlocks(endTick, token))
            {
                token.ThrowIfCancellationRequested(); buffer.Clear();
                candidate.Page.AppendSpatialRangeValues(buffer, 0, candidate.Block, 0, endTick,
                    int.MinValue, int.MaxValue, ulong.MaxValue);
                foreach (var entry in buffer)
                    if (!exclude || !_spatialValueOverlay.TryGetValue(_getId(entry.Value), out _)) yield return entry.Value;
            }
        }
    }

    internal IEnumerable<TValue> EnumerateRangeValues(long start, long end, int minimumLane, int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        List<PagedTimelineOrderedValue<TValue>> buffer = [];
        foreach (TValue value in Enumerate(_spatialIndex, true)) yield return value;
        foreach (TValue value in Enumerate(_spatialOverlayIndex, false)) yield return value;
        IEnumerable<TValue> Enumerate(PagedTimelineSpatialBlockIndex<TValue> index, bool exclude)
        {
            foreach (var candidate in index.Query(start, end, minimumLane, maximumLane, requiredCategoryMask))
            {
                buffer.Clear();
                candidate.Page.AppendSpatialRangeValues(buffer, 0, candidate.Block, start, end,
                    minimumLane, maximumLane, requiredCategoryMask);
                foreach (var entry in buffer)
                    if (!exclude || !_spatialValueOverlay.TryGetValue(_getId(entry.Value), out _)) yield return entry.Value;
            }
        }
    }

    internal IEnumerable<TValue> EnumerateExactStart(long tick, int minimumLane, int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        // A spatial block has a bounded number of records. Do not gather a
        // million coincident imported notes into a temporary result array.
        List<TValue> buffer = [];
        foreach (TValue value in Enumerate(_spatialIndex, true)) yield return value;
        foreach (TValue value in Enumerate(_spatialOverlayIndex, false)) yield return value;
        IEnumerable<TValue> Enumerate(PagedTimelineSpatialBlockIndex<TValue> index, bool exclude)
        {
            foreach (var candidate in index.QueryStarts(tick, minimumLane, maximumLane, requiredCategoryMask))
            {
                buffer.Clear();
                candidate.Page.AppendExactStartValues(buffer, candidate.Block, tick,
                    minimumLane, maximumLane, requiredCategoryMask);
                foreach (TValue value in buffer)
                    if (!exclude || !_spatialValueOverlay.TryGetValue(_getId(value), out _)) yield return value;
            }
        }
    }

    private void AppendExactStartValues(
        PagedTimelineSpatialBlockIndex<TValue> index,
        List<TValue> destination,
        long tick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        bool excludeOverlayValues)
    {
        foreach (PagedTimelineSpatialBlockReference<TValue> candidate in
            index.QueryStarts(
                tick,
                minimumLane,
                maximumLane,
                requiredCategoryMask))
        {
            int first = destination.Count;
            candidate.Page.AppendExactStartValues(
                destination,
                candidate.Block,
                tick,
                minimumLane,
                maximumLane,
                requiredCategoryMask);
            if (!excludeOverlayValues) continue;
            int write = first;
            for (int read = first; read < destination.Count; read++)
            {
                TValue value = destination[read];
                if (_spatialValueOverlay.TryGetValue(_getId(value), out _)) continue;
                destination[write++] = value;
            }
            if (write != destination.Count)
                destination.RemoveRange(write, destination.Count - write);
        }
    }

    public ulong GetRangeFingerprint(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue)
    {
        PagedTimelineRangeFingerprintAggregate aggregate = default;
        if (endTick <= startTick || maximumLane < minimumLane)
        {
            return aggregate.ToFingerprint();
        }
        if (!_spatialValueOverlay.IsEmpty)
        {
            foreach (PagedTimelineSpatialBlockReference<TValue> candidate in _spatialIndex.Query(
                startTick,
                endTick,
                minimumLane,
                maximumLane,
                requiredCategoryMask))
            {
                candidate.Page.AccumulateSpatialRangeFingerprintExcluding(
                    ref aggregate,
                    candidate.Block,
                    startTick,
                    endTick,
                    minimumLane,
                    maximumLane,
                    requiredCategoryMask,
                    _spatialValueOverlay,
                    _getId);
            }
            foreach (PagedTimelineSpatialBlockReference<TValue> candidate in
                _spatialOverlayIndex.Query(
                    startTick,
                    endTick,
                    minimumLane,
                    maximumLane,
                    requiredCategoryMask))
            {
                candidate.Page.AccumulateSpatialRangeFingerprint(
                    ref aggregate,
                    candidate.Block,
                    startTick,
                    endTick,
                    minimumLane,
                    maximumLane,
                    requiredCategoryMask);
            }
            return aggregate.ToFingerprint();
        }
        foreach (PagedTimelineSpatialBlockReference<TValue> candidate in _spatialIndex.Query(
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask))
        {
            candidate.Page.AccumulateSpatialRangeFingerprint(
                ref aggregate,
                candidate.Block,
                startTick,
                endTick,
                minimumLane,
                maximumLane,
                requiredCategoryMask);
        }
        return aggregate.ToFingerprint();
    }

    public int AccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumLane,
        int maximumLane,
        Span<TimelineRasterColumnSummary> destination,
        ulong requiredCategoryMask = ulong.MaxValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        destination.Clear();
        if (destination.IsEmpty || maximumLane < minimumLane)
            return 0;
        if (!_spatialValueOverlay.IsEmpty)
        {
            int work = 0;
            foreach (PagedTimelineSpatialBlockReference<TValue> candidate in _spatialIndex.Query(
                projection.StartTick,
                projection.EndTick,
                minimumLane,
                maximumLane,
                requiredCategoryMask,
                cancellationToken))
            {
                work += candidate.Page.AccumulateSpatialBlockRasterColumnsExcluding(
                    candidate.Block,
                    projection,
                    minimumLane,
                    maximumLane,
                    requiredCategoryMask,
                    _spatialValueOverlay,
                    _getId,
                    destination,
                    cancellationToken);
            }
            work += _spatialOverlayIndex.AccumulateRasterColumns(
                projection,
                minimumLane,
                maximumLane,
                requiredCategoryMask,
                destination,
                cancellationToken);
            return work;
        }
        return _spatialIndex.AccumulateRasterColumns(
            projection,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            destination,
            cancellationToken);
    }

    public void AccumulateStartColumns(long extent, Span<byte> destination)
    {
        if (extent <= 0) throw new ArgumentOutOfRangeException(nameof(extent));
        foreach (TValue value in EnumerateAll()) MarkColumn(destination, _getStart(value), extent);
    }

    internal int CountCandidateSpatialBlocks(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask = ulong.MaxValue) =>
        _spatialIndex.Query(
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask).Count
        + _spatialOverlayIndex.Query(
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask).Count;

    internal static void MarkColumn(Span<byte> destination, long tick, long extent)
    {
        if (destination.IsEmpty) return;
        int column = Math.Clamp(
            (int)(Math.Max(0, tick) / (double)extent * destination.Length),
            0,
            destination.Length - 1);
        destination[column] = 1;
    }
}

internal sealed partial class PagedTimelineSpatialBlockIndex<TValue>
{
    private readonly Node? _root;

    private PagedTimelineSpatialBlockIndex(Node? root) => _root = root;

    public static PagedTimelineSpatialBlockIndex<TValue> Empty { get; } = new(root: null);

    public long MaximumEndTick => _root?.MaximumEndTick ?? 0;

    public long GetMaximumEndTickExcluding(
        PersistentTimelineIdDeltaMap<TValue> excluded,
        Func<TValue, MidoraId> getId)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        ArgumentNullException.ThrowIfNull(getId);
        long maximum = 0;
        AccumulateMaximumEndTick(_root, excluded, getId, ref maximum);
        return maximum;
    }

    private static void AccumulateMaximumEndTick(
        Node? node,
        PersistentTimelineIdDeltaMap<TValue> excluded,
        Func<TValue, MidoraId> getId,
        ref long maximum)
    {
        if (node is null || node.MaximumEndTick <= maximum) return;
        Node? first = node.Left;
        Node? second = node.Right;
        if ((second?.MaximumEndTick ?? long.MinValue)
            > (first?.MaximumEndTick ?? long.MinValue))
        {
            (first, second) = (second, first);
        }
        AccumulateMaximumEndTick(first, excluded, getId, ref maximum);
        if (node.Entry.Block.MaximumEndTick > maximum)
        {
            maximum = Math.Max(
                maximum,
                node.Entry.Page.GetMaximumEndTickExcluding(
                    node.Entry.Block,
                    excluded,
                    getId));
        }
        AccumulateMaximumEndTick(second, excluded, getId, ref maximum);
    }

    public static PagedTimelineSpatialBlockIndex<TValue> Create(
        IEnumerable<PagedTimelineValuePage<TValue>> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        PagedTimelineSpatialBlockReference<TValue>[] entries = pages
            .SelectMany(static page => page.SpatialBlocks.Select(block => new PagedTimelineSpatialBlockReference<TValue>(page, block)))
            .ToArray();
        Array.Sort(entries, Compare);
        return entries.Length == 0
            ? Empty
            : new(BuildBalanced(entries, 0, entries.Length));
    }

    public PagedTimelineSpatialBlockIndex<TValue> ReplacePage(
        PagedTimelineValuePage<TValue>? expected,
        PagedTimelineValuePage<TValue>? replacement)
    {
        Node? root = _root;
        if (expected is not null)
        {
            foreach (PagedTimelineSpatialBlockMetadata block in expected.SpatialBlocks)
                root = Remove(root, new(expected, block));
        }
        if (replacement is not null)
        {
            foreach (PagedTimelineSpatialBlockMetadata block in replacement.SpatialBlocks)
                root = Insert(root, new(replacement, block));
        }
        return ReferenceEquals(root, _root) ? this : new(root);
    }

    public PagedTimelineSpatialBlockIndex<TValue> ReplacePages(
        IReadOnlyCollection<PagedTimelineValuePage<TValue>> expected,
        IReadOnlyCollection<PagedTimelineValuePage<TValue>> replacements)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacements);
        if (expected.Count == 0 && replacements.Count == 0) return this;

        int changedBlockCount = 0;
        foreach (PagedTimelineValuePage<TValue> page in expected)
            changedBlockCount = checked(changedBlockCount + page.SpatialBlocks.Length);
        foreach (PagedTimelineValuePage<TValue> page in replacements)
            changedBlockCount = checked(changedBlockCount + page.SpatialBlocks.Length);

        // Path-copying the AVL is cheaper for a handful of pages and retains
        // maximum sharing. A wide batch, however, must not perform thousands
        // of independent remove/insert traversals. Merge the already ordered
        // old root with the ordered replacement blocks and build one balanced
        // immutable root in linear time instead.
        if (changedBlockCount <= 64)
        {
            PagedTimelineSpatialBlockIndex<TValue> result = this;
            foreach (PagedTimelineValuePage<TValue> page in expected)
                result = result.ReplacePage(page, replacement: null);
            foreach (PagedTimelineValuePage<TValue> page in replacements)
                result = result.ReplacePage(expected: null, page);
            return result;
        }

        HashSet<PagedTimelineValuePage<TValue>> removed =
            new(expected, ReferenceEqualityComparer.Instance);
        if (removed.Count != expected.Count)
            throw new InvalidOperationException("A paged timeline spatial page is duplicated in a batch replacement.");

        List<PagedTimelineSpatialBlockReference<TValue>> retained =
            new(Math.Max(0, (_root?.Count ?? 0) - changedBlockCount));
        AppendInOrder(_root, removed, retained);

        PagedTimelineSpatialBlockReference<TValue>[] added = replacements
            .SelectMany(static page => page.SpatialBlocks.Select(
                block => new PagedTimelineSpatialBlockReference<TValue>(page, block)))
            .ToArray();
        Array.Sort(added, Compare);

        PagedTimelineSpatialBlockReference<TValue>[] merged =
            new PagedTimelineSpatialBlockReference<TValue>[retained.Count + added.Length];
        int retainedIndex = 0;
        int addedIndex = 0;
        int writeIndex = 0;
        while (retainedIndex < retained.Count || addedIndex < added.Length)
        {
            if (retainedIndex == retained.Count)
            {
                merged[writeIndex++] = added[addedIndex++];
                continue;
            }
            if (addedIndex == added.Length)
            {
                merged[writeIndex++] = retained[retainedIndex++];
                continue;
            }
            int comparison = Compare(retained[retainedIndex], added[addedIndex]);
            if (comparison == 0)
                throw new InvalidOperationException("A paged timeline spatial block is already indexed.");
            merged[writeIndex++] = comparison < 0
                ? retained[retainedIndex++]
                : added[addedIndex++];
        }
        return merged.Length == 0
            ? Empty
            : new(BuildBalanced(merged, 0, merged.Length));
    }

    private static void AppendInOrder(
        Node? node,
        IReadOnlySet<PagedTimelineValuePage<TValue>> excludedPages,
        List<PagedTimelineSpatialBlockReference<TValue>> destination)
    {
        if (node is null) return;
        AppendInOrder(node.Left, excludedPages, destination);
        if (!excludedPages.Contains(node.Entry.Page)) destination.Add(node.Entry);
        AppendInOrder(node.Right, excludedPages, destination);
    }

    public IReadOnlyList<PagedTimelineSpatialBlockReference<TValue>> Query(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_root is null || endTick <= startTick || maximumLane < minimumLane)
            return [];
        List<PagedTimelineSpatialBlockReference<TValue>> result = [];
        Query(
            _root,
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            result,
            cancellationToken);
        return result;
    }

    // Object-list-only traversal: stack depth follows the balanced metadata
    // tree, not the number of matched blocks. Existing raster queries unchanged.
    public IEnumerable<PagedTimelineSpatialBlockReference<TValue>> EnumerateListBlocks(long endTick, CancellationToken token)
    {
        Stack<Node> pending = new();
        Node? current = _root;
        while (current is not null || pending.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            while (current is not null)
            {
                if (current.MaximumEndTick <= 0 || current.MinimumStartTick >= endTick) { current = null; break; }
                pending.Push(current); current = current.Left;
            }
            if (pending.Count == 0) yield break;
            current = pending.Pop();
            var entry = current.Entry;
            if (entry.Block.MinimumStartTick < endTick && entry.Block.MaximumEndTick > 0) yield return entry;
            current = current.Right;
        }
    }

    public IReadOnlyList<PagedTimelineSpatialBlockReference<TValue>> QueryStarts(
        long tick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask)
    {
        if (_root is null || maximumLane < minimumLane) return [];
        List<PagedTimelineSpatialBlockReference<TValue>> result = [];
        QueryStarts(
            _root,
            tick,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            result);
        return result;
    }

    public int AccumulateRasterColumns(
        TimelineRasterColumnProjection projection,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_root is null
            || destination.IsEmpty
            || maximumLane < minimumLane)
        {
            return 0;
        }
        (ulong laneMaskLow, ulong laneMaskHigh) =
            PagedTimelineRasterProjection.LaneRangeMask(minimumLane, maximumLane);
        int work = 0;
        AccumulateRasterColumns(
            _root,
            projection,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            laneMaskLow,
            laneMaskHigh,
            ref work,
            destination,
            cancellationToken);
        return work;
    }

    private static void AccumulateRasterColumns(
        Node? node,
        TimelineRasterColumnProjection projection,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        ulong queryLaneMaskLow,
        ulong queryLaneMaskHigh,
        ref int work,
        Span<TimelineRasterColumnSummary> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null
            || node.MaximumEndTick <= projection.StartTick
            || node.MinimumStartTick >= projection.EndTick
            || node.MaximumLane < minimumLane
            || node.MinimumLane > maximumLane
            || (node.CategoryMask & requiredCategoryMask) == 0)
        {
            return;
        }
        work++;
        int columnSpan = PagedTimelineRasterProjection.ColumnSpan(
            projection,
            node.MinimumStartTick,
            node.MaximumEndTick);
        bool categoryExact = requiredCategoryMask == ulong.MaxValue
            || (node.CategoryMask & ~requiredCategoryMask) == 0;
        if (columnSpan <= 1 && categoryExact)
        {
            ulong low = node.LaneMaskLow & queryLaneMaskLow;
            ulong high = node.LaneMaskHigh & queryLaneMaskHigh;
            PagedTimelineRasterProjection.Include(
                destination,
                projection,
                node.MinimumStartTick,
                node.MaximumEndTick,
                low,
                high,
                node.MinimumRasterValue,
                node.MaximumRasterValue,
                node.ApproximateSourceCount);
            if (projection.StartTick <= node.MinimumStartTick
                && projection.EndTick >= node.MaximumEndTick)
            {
                PagedTimelineRasterProjection.IncludeBoundaries(
                    destination,
                    projection,
                    node.MinimumStartTick,
                    node.MaximumEndTick,
                    low,
                    high);
            }
            return;
        }

        AccumulateRasterColumns(
            node.Left,
            projection,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            queryLaneMaskLow,
            queryLaneMaskHigh,
            ref work,
            destination,
            cancellationToken);

        PagedTimelineSpatialBlockMetadata block = node.Entry.Block;
        if (block.MaximumEndTick > projection.StartTick
            && block.MinimumStartTick < projection.EndTick
            && block.MaximumLane >= minimumLane
            && block.MinimumLane <= maximumLane
            && (block.CategoryMask & requiredCategoryMask) != 0)
        {
            int blockColumnSpan = PagedTimelineRasterProjection.ColumnSpan(
                projection,
                block.MinimumStartTick,
                block.MaximumEndTick);
            bool blockCategoryExact = requiredCategoryMask == ulong.MaxValue
                || (block.CategoryMask & ~requiredCategoryMask) == 0;
            if (blockColumnSpan <= 1 && blockCategoryExact)
            {
                ulong low = block.LaneMaskLow & queryLaneMaskLow;
                ulong high = block.LaneMaskHigh & queryLaneMaskHigh;
                PagedTimelineRasterProjection.Include(
                    destination,
                    projection,
                    block.MinimumStartTick,
                    block.MaximumEndTick,
                    low,
                    high,
                    block.MinimumRasterValue,
                    block.MaximumRasterValue,
                    block.Count);
                if (projection.StartTick <= block.MinimumStartTick
                    && projection.EndTick >= block.MaximumEndTick)
                {
                    PagedTimelineRasterProjection.IncludeBoundaries(
                        destination,
                        projection,
                        block.MinimumStartTick,
                        block.MaximumEndTick,
                        low,
                        high);
                }
            }
            else
            {
                work += node.Entry.Page.AccumulateSpatialBlockRasterColumns(
                    block,
                    projection,
                    minimumLane,
                    maximumLane,
                    requiredCategoryMask,
                    destination,
                    cancellationToken);
            }
        }

        AccumulateRasterColumns(
            node.Right,
            projection,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            queryLaneMaskLow,
            queryLaneMaskHigh,
            ref work,
            destination,
            cancellationToken);
    }

    private void Query(
        Node? node,
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        List<PagedTimelineSpatialBlockReference<TValue>> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) return;
        if (node.MaximumEndTick <= startTick
            || node.MinimumStartTick >= endTick
            || node.MaximumLane < minimumLane
            || node.MinimumLane > maximumLane
            || (node.CategoryMask & requiredCategoryMask) == 0)
        {
            return;
        }
        Query(
            node.Left,
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            destination,
            cancellationToken);
        PagedTimelineSpatialBlockMetadata block = node.Entry.Block;
        if (block.MinimumStartTick < endTick
            && block.MaximumEndTick > startTick
            && block.MaximumLane >= minimumLane
            && block.MinimumLane <= maximumLane
            && (block.CategoryMask & requiredCategoryMask) != 0)
        {
            destination.Add(node.Entry);
        }
        Query(
            node.Right,
            startTick,
            endTick,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            destination,
            cancellationToken);
    }

    private static void QueryStarts(
        Node? node,
        long tick,
        int minimumLane,
        int maximumLane,
        ulong requiredCategoryMask,
        List<PagedTimelineSpatialBlockReference<TValue>> destination)
    {
        if (node is null
            || node.MinimumStartTick > tick
            || node.MaximumStartTick < tick
            || node.MaximumLane < minimumLane
            || node.MinimumLane > maximumLane
            || (node.CategoryMask & requiredCategoryMask) == 0)
        {
            return;
        }
        QueryStarts(
            node.Left,
            tick,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            destination);
        PagedTimelineSpatialBlockMetadata block = node.Entry.Block;
        if (block.MinimumStartTick <= tick
            && block.MaximumStartTick >= tick
            && block.MaximumLane >= minimumLane
            && block.MinimumLane <= maximumLane
            && (block.CategoryMask & requiredCategoryMask) != 0)
        {
            destination.Add(node.Entry);
        }
        QueryStarts(
            node.Right,
            tick,
            minimumLane,
            maximumLane,
            requiredCategoryMask,
            destination);
    }

    private static Node Insert(Node? node, PagedTimelineSpatialBlockReference<TValue> entry)
    {
        if (node is null) return new(entry, left: null, right: null);
        int comparison = Compare(entry, node.Entry);
        if (comparison == 0)
            throw new InvalidOperationException("A paged timeline spatial block is already indexed.");
        return Balance(comparison < 0
            ? new(node.Entry, Insert(node.Left, entry), node.Right)
            : new(node.Entry, node.Left, Insert(node.Right, entry)));
    }

    private static Node? BuildBalanced(
        PagedTimelineSpatialBlockReference<TValue>[] entries,
        int first,
        int count)
    {
        if (count == 0) return null;
        int leftCount = count / 2;
        return new(
            entries[first + leftCount],
            BuildBalanced(entries, first, leftCount),
            BuildBalanced(entries, first + leftCount + 1, count - leftCount - 1));
    }

    private static Node? Remove(Node? node, PagedTimelineSpatialBlockReference<TValue> entry)
    {
        if (node is null)
            throw new InvalidOperationException("A paged timeline spatial block is not indexed.");
        int comparison = Compare(entry, node.Entry);
        if (comparison < 0)
            return Balance(new(node.Entry, Remove(node.Left, entry), node.Right));
        if (comparison > 0)
            return Balance(new(node.Entry, node.Left, Remove(node.Right, entry)));
        if (node.Left is null) return node.Right;
        if (node.Right is null) return node.Left;
        Node successor = Minimum(node.Right);
        return Balance(new(successor.Entry, node.Left, RemoveMinimum(node.Right)));
    }

    private static Node Minimum(Node node)
    {
        while (node.Left is not null) node = node.Left;
        return node;
    }

    private static Node? RemoveMinimum(Node node) => node.Left is null
        ? node.Right
        : Balance(new(node.Entry, RemoveMinimum(node.Left), node.Right));

    private static Node Balance(Node node)
    {
        int balance = Height(node.Left) - Height(node.Right);
        if (balance > 1)
        {
            if (Height(node.Left!.Left) < Height(node.Left.Right))
                node = new(node.Entry, RotateLeft(node.Left), node.Right);
            return RotateRight(node);
        }
        if (balance < -1)
        {
            if (Height(node.Right!.Right) < Height(node.Right.Left))
                node = new(node.Entry, node.Left, RotateRight(node.Right));
            return RotateLeft(node);
        }
        return node;
    }

    private static Node RotateLeft(Node node)
    {
        Node pivot = node.Right!;
        return new(pivot.Entry, new(node.Entry, node.Left, pivot.Left), pivot.Right);
    }

    private static Node RotateRight(Node node)
    {
        Node pivot = node.Left!;
        return new(pivot.Entry, pivot.Left, new(node.Entry, pivot.Right, node.Right));
    }

    private static int Height(Node? node) => node?.Height ?? 0;

    private static int Compare(
        PagedTimelineSpatialBlockReference<TValue> left,
        PagedTimelineSpatialBlockReference<TValue> right)
    {
        int start = left.Block.MinimumStartTick.CompareTo(right.Block.MinimumStartTick);
        if (start != 0) return start;
        int page = left.Page.SpatialIdentity.CompareTo(right.Page.SpatialIdentity);
        return page != 0 ? page : left.Block.First.CompareTo(right.Block.First);
    }

    private sealed class Node
    {
        public Node(
            PagedTimelineSpatialBlockReference<TValue> entry,
            Node? left,
            Node? right)
        {
            Entry = entry;
            Left = left;
            Right = right;
            Height = checked(1 + Math.Max(PagedTimelineSpatialBlockIndex<TValue>.Height(left), PagedTimelineSpatialBlockIndex<TValue>.Height(right)));
            Count = checked(1 + (left?.Count ?? 0) + (right?.Count ?? 0));
            MinimumStartTick = Math.Min(
                entry.Block.MinimumStartTick,
                Math.Min(left?.MinimumStartTick ?? long.MaxValue, right?.MinimumStartTick ?? long.MaxValue));
            MaximumStartTick = Math.Max(
                entry.Block.MaximumStartTick,
                Math.Max(left?.MaximumStartTick ?? long.MinValue, right?.MaximumStartTick ?? long.MinValue));
            MaximumEndTick = Math.Max(
                entry.Block.MaximumEndTick,
                Math.Max(left?.MaximumEndTick ?? long.MinValue, right?.MaximumEndTick ?? long.MinValue));
            MinimumLane = Math.Min(
                entry.Block.MinimumLane,
                Math.Min(left?.MinimumLane ?? int.MaxValue, right?.MinimumLane ?? int.MaxValue));
            MaximumLane = Math.Max(
                entry.Block.MaximumLane,
                Math.Max(left?.MaximumLane ?? int.MinValue, right?.MaximumLane ?? int.MinValue));
            LaneMaskLow = entry.Block.LaneMaskLow
                | (left?.LaneMaskLow ?? 0)
                | (right?.LaneMaskLow ?? 0);
            LaneMaskHigh = entry.Block.LaneMaskHigh
                | (left?.LaneMaskHigh ?? 0)
                | (right?.LaneMaskHigh ?? 0);
            MinimumRasterValue = Math.Min(
                entry.Block.MinimumRasterValue,
                Math.Min(
                    left?.MinimumRasterValue ?? double.PositiveInfinity,
                    right?.MinimumRasterValue ?? double.PositiveInfinity));
            MaximumRasterValue = Math.Max(
                entry.Block.MaximumRasterValue,
                Math.Max(
                    left?.MaximumRasterValue ?? double.NegativeInfinity,
                    right?.MaximumRasterValue ?? double.NegativeInfinity));
            ApproximateSourceCount = SaturatingAdd(
                entry.Block.Count,
                SaturatingAdd(
                    left?.ApproximateSourceCount ?? 0,
                    right?.ApproximateSourceCount ?? 0));
            CategoryMask = entry.Block.CategoryMask
                | (left?.CategoryMask ?? 0)
                | (right?.CategoryMask ?? 0);
            PagedTimelineRangeFingerprintAggregate metadataAggregate = entry.Block.Aggregate;
            metadataAggregate.Combine(left?.MetadataAggregate ?? default);
            metadataAggregate.Combine(right?.MetadataAggregate ?? default);
            MetadataAggregate = metadataAggregate;
        }

        public PagedTimelineSpatialBlockReference<TValue> Entry { get; }
        public Node? Left { get; }
        public Node? Right { get; }
        public int Height { get; }
        public int Count { get; }
        public long MinimumStartTick { get; }
        public long MaximumStartTick { get; }
        public long MaximumEndTick { get; }
        public int MinimumLane { get; }
        public int MaximumLane { get; }
        public ulong LaneMaskLow { get; }
        public ulong LaneMaskHigh { get; }
        public double MinimumRasterValue { get; }
        public double MaximumRasterValue { get; }
        public int ApproximateSourceCount { get; }
        public ulong CategoryMask { get; }
        public PagedTimelineRangeFingerprintAggregate MetadataAggregate { get; }

        private static int SaturatingAdd(int left, int right) =>
            left > int.MaxValue - right ? int.MaxValue : left + right;
    }
}

internal readonly record struct PagedTimelineSpatialBlockReference<TValue>(
    PagedTimelineValuePage<TValue> Page,
    PagedTimelineSpatialBlockMetadata Block);

internal struct PagedTimelineRangeFingerprintAggregate
{
    private ulong _xor;
    private ulong _sum;
    private ulong _rotatedSum;
    private ulong _count;

    public void Add(ulong value)
    {
        ulong mixed = Mix(value);
        _xor ^= mixed;
        _sum = unchecked(_sum + mixed);
        _rotatedSum = unchecked(_rotatedSum + BitOperations.RotateLeft(mixed, 23));
        _count++;
    }

    public void Combine(PagedTimelineRangeFingerprintAggregate other)
    {
        _xor ^= other._xor;
        _sum = unchecked(_sum + other._sum);
        _rotatedSum = unchecked(_rotatedSum + other._rotatedSum);
        _count = unchecked(_count + other._count);
    }

    public readonly ulong ToFingerprint()
    {
        ulong fingerprint = PagedTimelineFingerprint.Offset;
        PagedTimelineFingerprint.Add(ref fingerprint, _xor);
        PagedTimelineFingerprint.Add(ref fingerprint, _sum);
        PagedTimelineFingerprint.Add(ref fingerprint, _rotatedSum);
        PagedTimelineFingerprint.Add(ref fingerprint, _count);
        return fingerprint;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}

/// <summary>
/// Order-sensitive polynomial sequence hash with an associative combine
/// operation. Page aggregates can therefore be recombined after a page split
/// or merge without making the fingerprint depend on those internal page
/// boundaries.
/// </summary>
internal struct PagedTimelineSequenceFingerprintAggregate
{
    private const ulong Base = 0x9e3779b185ebca87UL;
    private static readonly ulong[] s_powers = CreatePowers(4096);
    private ulong _hash;
    private ulong _power;
    private ulong _count;

    public void Add(ulong value)
    {
        if (_count == 0) _power = 1;
        _hash = unchecked(_hash * Base + Mix(value));
        _power = unchecked(_power * Base);
        _count++;
    }

    public void Combine(PagedTimelineSequenceFingerprintAggregate other)
    {
        if (other._count == 0) return;
        if (_count == 0)
        {
            this = other;
            return;
        }
        _hash = unchecked(_hash * other._power + other._hash);
        _power = unchecked(_power * other._power);
        _count = unchecked(_count + other._count);
    }

    public void ReplaceAt(
        int index,
        int count,
        ulong expectedValue,
        ulong replacementValue)
    {
        if ((uint)index >= (uint)count || unchecked((ulong)count) != _count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (expectedValue == replacementValue) return;
        int exponent = checked(count - index - 1);
        ulong weight = exponent < s_powers.Length
            ? s_powers[exponent]
            : Pow(Base, exponent);
        _hash = unchecked(
            _hash
            + unchecked(Mix(replacementValue) - Mix(expectedValue)) * weight);
    }

    public readonly ulong ToFingerprint()
    {
        ulong fingerprint = PagedTimelineFingerprint.Offset;
        PagedTimelineFingerprint.Add(ref fingerprint, _hash);
        PagedTimelineFingerprint.Add(ref fingerprint, _count);
        return fingerprint;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private static ulong[] CreatePowers(int maximumExponent)
    {
        ulong[] result = new ulong[checked(maximumExponent + 1)];
        result[0] = 1;
        for (int index = 1; index < result.Length; index++)
            result[index] = unchecked(result[index - 1] * Base);
        return result;
    }

    private static ulong Pow(ulong value, int exponent)
    {
        ulong result = 1;
        while (exponent != 0)
        {
            if ((exponent & 1) != 0) result = unchecked(result * value);
            exponent >>= 1;
            if (exponent != 0) value = unchecked(value * value);
        }
        return result;
    }

}

internal static class PagedTimelineFingerprint
{
    internal const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    internal static void Add(ref ulong hash, ulong value)
    {
        hash ^= value;
        hash *= Prime;
    }

    internal static ulong ForLogicalNote(LogicalNoteSnapshotValue value)
    {
        ulong hash = Offset;
        Add(ref hash, unchecked((ulong)value.Id.Value));
        Add(ref hash, unchecked((ulong)value.StartTick));
        Add(ref hash, unchecked((ulong)value.LengthTicks));
        Add(ref hash, unchecked((ulong)value.Note));
        Add(ref hash, unchecked((ulong)value.Velocity));
        return hash;
    }

    internal static ulong ForTemplateEvent(TemplateEventSnapshotValue value)
    {
        ulong hash = Offset;
        Add(ref hash, unchecked((ulong)value.Id.Value));
        Add(ref hash, unchecked((ulong)value.Kind));
        Add(ref hash, unchecked((ulong)value.Tick));
        Add(ref hash, unchecked((ulong)value.LengthTicks));
        Add(ref hash, unchecked((ulong)value.Number));
        Add(ref hash, unchecked((ulong)value.Value));
        Add(ref hash, unchecked((ulong)value.SecondaryValue));
        Add(ref hash, value.HasBankMsb ? 1UL : 0UL);
        Add(ref hash, value.HasBankLsb ? 1UL : 0UL);
        Add(ref hash, value.FollowPitchDelta ? 1UL : 0UL);
        return hash;
    }

    internal static ulong ForCurvePoint(CurvePointSnapshotValue value)
    {
        ulong hash = Offset;
        Add(ref hash, unchecked((ulong)value.Id.Value));
        Add(ref hash, unchecked((ulong)value.Tick));
        Add(ref hash, unchecked((ulong)BitConverter.DoubleToInt64Bits(value.Value)));
        Add(ref hash, unchecked((ulong)value.Interpolation));
        return hash;
    }
}
