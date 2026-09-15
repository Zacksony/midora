using System.Globalization;
using System.Collections.Frozen;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Rendering;

public enum ConductorProjectionKind { Tempo, Meta, Arrangement, Ruler }

/// <summary>Small immutable adapters, not a materialized copy of the Conductor.</summary>
public sealed class ConductorTimelineProjection
{
    public ConductorTimelineProjection(
        ConductorTrack conductor, long semanticRevision, double tempoMinimum, double tempoMaximum)
    {
        ArgumentNullException.ThrowIfNull(conductor);
        if (!double.IsFinite(tempoMinimum) || !double.IsFinite(tempoMaximum)
            || tempoMaximum <= tempoMinimum)
            throw new ArgumentOutOfRangeException(nameof(tempoMaximum));
        var data = new ConductorProjectionData(conductor);
        TempoSource = new(data, ConductorProjectionKind.Tempo, tempoMinimum, tempoMaximum);
        MetaSource = new(data, ConductorProjectionKind.Meta, tempoMinimum, tempoMaximum);
        ArrangementSource = new(data, ConductorProjectionKind.Arrangement, tempoMinimum, tempoMaximum);
        var ruler = new ConductorRenderItemSource(data, ConductorProjectionKind.Ruler, tempoMinimum, tempoMaximum);
        TempoSnapshot = new(semanticRevision, "conductor:tempo", [], ["Tempo"], itemSource: TempoSource);
        MetaSnapshot = new(semanticRevision, "conductor:meta", [],
            ["Time Signature", "Key Signature", "Marker", "Project End"], itemSource: MetaSource);
        RulerSnapshot = new(semanticRevision, "conductor:ruler", [], itemSource: ruler);
    }

    public TimelineRenderSnapshot TempoSnapshot { get; }
    public TimelineRenderSnapshot MetaSnapshot { get; }
    public TimelineRenderSnapshot RulerSnapshot { get; }
    public ConductorRenderItemSource TempoSource { get; }
    public ConductorRenderItemSource MetaSource { get; }
    public ConductorRenderItemSource ArrangementSource { get; }
}

internal sealed class ConductorProjectionData
{
    public ConductorProjectionData(ConductorTrack conductor)
    {
        Tempos = conductor.Tempos.CreateQuerySnapshot();
        Signatures = conductor.TimeSignatures.CreateQuerySnapshot();
        Keys = conductor.KeySignatures.CreateQuerySnapshot();
        Markers = conductor.Markers.CreateQuerySnapshot();
        End = conductor.EndMarker is { } end ? (end.Id, end.Tick) : null;
    }
    public ConductorQuerySnapshot<TempoChange> Tempos { get; }
    public ConductorQuerySnapshot<TimeSignatureChange> Signatures { get; }
    public ConductorQuerySnapshot<KeySignatureChange> Keys { get; }
    public ConductorQuerySnapshot<ProjectMarker> Markers { get; }
    public (MidoraId Id, long Tick)? End { get; }
}

public sealed class ConductorRenderItemSource :
    IPreparedTimelineRenderItemSource, INonBlockingTimelineFingerprintSource
{
    // The approved palette's Color.Success, also used by the Tempo editor.
    public const uint TempoAccentColor = 0xff58c487;

    private readonly ConductorProjectionData _data;
    private readonly double _minimum;
    private readonly double _span;

    internal ConductorRenderItemSource(ConductorProjectionData data, ConductorProjectionKind kind,
        double minimum, double maximum)
    {
        _data = data;
        Kind = kind;
        _minimum = minimum;
        _span = maximum - minimum;
    }

    public ConductorProjectionKind Kind { get; }
    public bool HasHitTestableItems => Kind is not (ConductorProjectionKind.Arrangement or ConductorProjectionKind.Ruler) && Count > 0;
    public long Count => Kind == ConductorProjectionKind.Ruler ? (long)_data.Markers.Count + (_data.End.HasValue ? 1 : 0)
        : Kind == ConductorProjectionKind.Tempo ? _data.Tempos.Count
        : (long)_data.Signatures.Count + _data.Keys.Count + _data.Markers.Count
            + (_data.End.HasValue ? 1 : 0)
            + (Kind == ConductorProjectionKind.Arrangement ? _data.Tempos.Count : 0);
    public long MaximumEndTick
    {
        get
        {
            long maximum = Kind == ConductorProjectionKind.Ruler ? Math.Max(_data.Markers.MaximumTick, _data.End?.Tick ?? 0)
                : Kind == ConductorProjectionKind.Tempo ? _data.Tempos.MaximumTick
                : Math.Max(Math.Max(_data.Signatures.MaximumTick, _data.Keys.MaximumTick),
                    Math.Max(_data.Markers.MaximumTick, _data.End?.Tick ?? 0));
            if (Kind == ConductorProjectionKind.Arrangement) maximum = Math.Max(maximum, _data.Tempos.MaximumTick);
            return maximum == long.MaxValue ? maximum : maximum + 1;
        }
    }
    public ulong ContentFingerprint => GetRangeFingerprint(0, long.MaxValue, 0, 4);

    public ulong GetRangeFingerprint(long startTick, long endTick, int firstLane, int lastLaneExclusive)
    {
        ulong value = unchecked((ulong)Kind + 1);
        if (Kind is ConductorProjectionKind.Tempo or ConductorProjectionKind.Arrangement)
            value = TimelineContentFingerprint.Combine(value, Kind == ConductorProjectionKind.Tempo
                ? _data.Tempos.GetStepRangeFingerprint(startTick, endTick)
                : _data.Tempos.GetRangeFingerprint(startTick, endTick));
        if (Kind != ConductorProjectionKind.Tempo)
        {
            if (Kind != ConductorProjectionKind.Ruler && Includes(0)) value = TimelineContentFingerprint.Combine(value, _data.Signatures.GetRangeFingerprint(startTick, endTick));
            if (Kind != ConductorProjectionKind.Ruler && Includes(1)) value = TimelineContentFingerprint.Combine(value, _data.Keys.GetRangeFingerprint(startTick, endTick));
            if (Includes(2)) value = TimelineContentFingerprint.Combine(value, _data.Markers.GetRangeFingerprint(startTick, endTick));
            if (Includes(3) && _data.End is { } end && end.Tick >= startTick && end.Tick < endTick)
                value = TimelineContentFingerprint.Combine(value, TimelineContentFingerprint.Combine((ulong)end.Id.Value, (ulong)end.Tick));
        }
        if (Kind == ConductorProjectionKind.Tempo)
        {
            value = TimelineContentFingerprint.Combine(value, unchecked((ulong)BitConverter.DoubleToInt64Bits(_minimum)));
            value = TimelineContentFingerprint.Combine(value, unchecked((ulong)BitConverter.DoubleToInt64Bits(_span)));
        }
        return value;
        bool Includes(int lane) => Kind is ConductorProjectionKind.Arrangement or ConductorProjectionKind.Ruler || firstLane <= lane && lastLaneExclusive > lane;
    }

    public void QueryInto(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        List<TimelineRenderItem> destination) =>
        VisitInto(startTick, endTick, firstLane, lastLaneExclusive, destination.Add);

    public void VisitInto(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        Action<TimelineRenderItem> visitor) =>
        Visit(startTick, endTick, firstLane, lastLaneExclusive, visitor, CancellationToken.None);

    public void Visit(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        Action<TimelineRenderItem> visitor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        if (endTick <= startTick || firstLane >= lastLaneExclusive) return;
        if (Kind is ConductorProjectionKind.Tempo or ConductorProjectionKind.Arrangement && Includes(0))
            foreach (TempoChange value in _data.Tempos.QueryTickRange(startTick, endTick, cancellationToken)) visitor(Project(value));
        if (Kind == ConductorProjectionKind.Tempo) return;
        if (Kind != ConductorProjectionKind.Ruler && Includes(0))
            foreach (TimeSignatureChange value in _data.Signatures.QueryTickRange(startTick, endTick, cancellationToken)) visitor(Project(value));
        if (Kind != ConductorProjectionKind.Ruler && Includes(1))
            foreach (KeySignatureChange value in _data.Keys.QueryTickRange(startTick, endTick, cancellationToken)) visitor(Project(value));
        if (Includes(2))
            foreach (ProjectMarker value in _data.Markers.QueryTickRange(startTick, endTick, cancellationToken)) visitor(Project(value));
        if (Includes(3) && _data.End is { } end && end.Tick >= startTick && end.Tick < endTick) visitor(ProjectEnd(end));
        return;
        bool Includes(int lane) => Kind is ConductorProjectionKind.Arrangement or ConductorProjectionKind.Ruler
            ? firstLane <= 0 && lastLaneExclusive > 0
            : firstLane <= lane && lastLaneExclusive > lane;
    }

    public bool TryGetTempoBefore(long tick, out TimelineRenderItem item)
    {
        if (_data.Tempos.TryGetBeforeTick(tick, out TempoChange value))
        {
            item = Project(value);
            return true;
        }
        item = default;
        return false;
    }

    public bool TryGetById(MidoraId id, out TimelineRenderItem item)
    {
        if (Kind is ConductorProjectionKind.Tempo or ConductorProjectionKind.Arrangement
            && _data.Tempos.TryGetById(id, out TempoChange tempo)) { item = Project(tempo); return true; }
        if (Kind != ConductorProjectionKind.Tempo)
        {
            if (Kind != ConductorProjectionKind.Ruler && _data.Signatures.TryGetById(id, out TimeSignatureChange signature)) { item = Project(signature); return true; }
            if (Kind != ConductorProjectionKind.Ruler && _data.Keys.TryGetById(id, out KeySignatureChange key)) { item = Project(key); return true; }
            if (_data.Markers.TryGetById(id, out ProjectMarker marker)) { item = Project(marker); return true; }
            if (_data.End is { } end && end.Id == id) { item = ProjectEnd(end); return true; }
        }
        item = default;
        return false;
    }

    public void QueryByIds(IReadOnlySet<MidoraId> ids, List<TimelineRenderItem> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        VisitByIds(ids, destination.Add);
    }

    public void VisitByIds(IReadOnlySet<MidoraId> ids, Action<TimelineRenderItem> visitor)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(visitor);
        if (ids.Count >= 4096 && ids.Count >= Count / 8
            && ids is ITimelineInMemoryIdSet or HashSet<MidoraId> or FrozenSet<MidoraId>)
        {
            foreach (TimelineRenderItem item in EnumerateAll())
                if (ids.Contains(item.Id)) visitor(item);
        }
        else
        {
            foreach (MidoraId id in ids)
                if (TryGetById(id, out TimelineRenderItem item)) visitor(item);
        }
    }

    public void AccumulateOverviewDensity(long extent, Span<int> destination)
    {
        if (extent <= 0 || destination.IsEmpty) return;
        foreach (TimelineRenderItem item in EnumerateAll())
        {
            int column = (int)Math.Clamp(Math.Floor(item.StartTick / (double)extent * destination.Length), 0, destination.Length - 1);
            if (destination[column] != int.MaxValue) destination[column]++;
        }
    }

    public IEnumerable<TimelineRenderItem> EnumerateAll()
    {
        if (Kind is ConductorProjectionKind.Tempo or ConductorProjectionKind.Arrangement)
            foreach (TempoChange value in _data.Tempos.EnumerateAll()) yield return Project(value);
        if (Kind == ConductorProjectionKind.Tempo) yield break;
        if (Kind != ConductorProjectionKind.Ruler)
        {
            foreach (TimeSignatureChange value in _data.Signatures.EnumerateAll()) yield return Project(value);
            foreach (KeySignatureChange value in _data.Keys.EnumerateAll()) yield return Project(value);
        }
        foreach (ProjectMarker value in _data.Markers.EnumerateAll()) yield return Project(value);
        if (_data.End is { } end) yield return ProjectEnd(end);
    }

    public bool TryQueryIntoCached(long startTick, long endTick, int firstLane, int lastLaneExclusive,
        List<TimelineRenderItem> destination)
    {
        int initialCount = destination.Count;
        try
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            QueryInto(startTick, endTick, firstLane, lastLaneExclusive, destination);
            return true;
        }
        catch (TimelineValueReadPendingException)
        {
            if (destination.Count > initialCount) destination.RemoveRange(initialCount, destination.Count - initialCount);
            return false;
        }
    }
    public void PrefetchRange(long startTick, long endTick, int firstLane, int lastLaneExclusive, CancellationToken cancellationToken)
    {
        Visit(startTick, endTick, firstLane, lastLaneExclusive, static _ => { }, cancellationToken);
        if (Kind == ConductorProjectionKind.Tempo) TryGetTempoBefore(startTick, out _);
    }
    public bool TryQueryByIdsCached(IReadOnlySet<MidoraId> ids, List<TimelineRenderItem> destination)
    {
        int initialCount = destination.Count;
        try
        {
            using var scope = TimelineValueReadScope.EnterCacheOnly();
            foreach (MidoraId id in ids) if (TryGetById(id, out TimelineRenderItem item)) destination.Add(item);
            return true;
        }
        catch (TimelineValueReadPendingException)
        {
            if (destination.Count > initialCount) destination.RemoveRange(initialCount, destination.Count - initialCount);
            return false;
        }
    }
    public void PrefetchIds(IReadOnlySet<MidoraId> ids, CancellationToken cancellationToken)
    {
        foreach (MidoraId id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryGetById(id, out _);
        }
    }

    public TimelineRenderItem? FindNearest(long tick, long tolerance, int lane, double normalizedValue,
        double valueTolerance, CancellationToken cancellationToken) =>
        FindNearestCore(tick, tolerance, lane, normalizedValue, valueTolerance, int.MaxValue, cancellationToken);

    public bool TryFindNearestCached(long tick, long tolerance, int lane, double normalizedValue,
        double valueTolerance, out TimelineRenderItem? item)
    {
        using var scope = TimelineValueReadScope.EnterCacheOnly();
        try { item = FindNearestCore(tick, tolerance, lane, normalizedValue, valueTolerance, 256, CancellationToken.None); return true; }
        catch (TimelineValueReadPendingException) { item = null; return false; }
    }

    private TimelineRenderItem? FindNearestCore(long tick, long tolerance, int lane, double normalizedValue,
        double valueTolerance, int maximumWork, CancellationToken cancellationToken)
    {
        TimelineRenderItem? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        int work = 0;
        long start = Math.Max(0, tick - tolerance);
        long end = tick >= long.MaxValue - tolerance - 1 ? long.MaxValue : tick + tolerance + 1;
        Visit(start, end, lane, lane + 1, item =>
        {
            if (++work > maximumWork) throw new TimelineValueReadPendingException();
            double dy = Kind == ConductorProjectionKind.Tempo ? Math.Abs(item.Value - normalizedValue) : 0;
            if (dy > valueTolerance) return;
            double dx = Math.Abs(item.StartTick - (double)tick) / Math.Max(1, tolerance);
            double distance = dx * dx + dy * dy / Math.Max(double.Epsilon, valueTolerance * valueTolerance);
            if (distance < nearestDistance || distance == nearestDistance && item.Id.CompareTo(nearest?.Id ?? item.Id) < 0)
            { nearestDistance = distance; nearest = item; }
        }, cancellationToken);
        return nearest;
    }

    private TimelineRenderItem Project(TempoChange value) => Item(value.Id, value.Tick, 0,
        Kind == ConductorProjectionKind.Tempo ? TimelineItemKind.TempoPoint : TimelineItemKind.ConductorEvent,
        ((double)value.BeatsPerMinute - _minimum) / _span, 0, TempoAccentColor, string.Empty)
        with { SecondaryValue = (double)value.BeatsPerMinute };
    private TimelineRenderItem Project(TimeSignatureChange value) => Item(value.Id, value.Tick, 0,
        TimelineItemKind.ConductorEvent, 0, 1, 0xff62a6f6, string.Empty)
        with { SecondaryValue = value.Numerator * 128 + value.Denominator };
    private TimelineRenderItem Project(KeySignatureChange value) => Item(value.Id, value.Tick, 1,
        TimelineItemKind.ConductorEvent, 0, 2, 0xffaf7ac5, string.Empty)
        with { SecondaryValue = value.SharpsFlats + 7 + (value.IsMinor ? 16 : 0) };
    private TimelineRenderItem Project(ProjectMarker value) => Item(value.Id, value.Tick, 2,
        TimelineItemKind.Marker, 0, 3, 0xffe8b34b, value.Name);
    private TimelineRenderItem ProjectEnd((MidoraId Id, long Tick) value) => Item(value.Id, value.Tick, 3,
        TimelineItemKind.ProjectEndMarker, 0, 4, 0xffe5e7eb, "Project End");

    public static string GetDisplayLabel(TimelineRenderItem item) => item.ZIndex switch
    {
        0 => item.SecondaryValue.ToString("0.########", CultureInfo.InvariantCulture) + " BPM",
        1 => $"{(int)item.SecondaryValue / 128}/{(int)item.SecondaryValue % 128}",
        2 => $"{(int)item.SecondaryValue % 16 - 7:+0;-0;0} {(item.SecondaryValue >= 16 ? "minor" : "major")}",
        _ => item.Label
    };
    private TimelineRenderItem Item(MidoraId id, long tick, int lane, TimelineItemKind kind,
        double value, int rank, uint color, string label) => new(id, kind, tick,
            tick == long.MaxValue ? long.MaxValue : tick + 1,
            Kind is ConductorProjectionKind.Arrangement or ConductorProjectionKind.Ruler ? 0 : lane,
            value, rank, Kind is ConductorProjectionKind.Arrangement or ConductorProjectionKind.Ruler
                ? TimelineItemState.HitTestDisabled : TimelineItemState.None)
            { AccentColor = color, Label = label };
}
