using Midora.Domain;
using System.Runtime.CompilerServices;

namespace Midora.Desktop.Presentation.Interaction;

public enum WorkspaceKind
{
    Arrangement,
    EventInstrumentLibrary,
    ProjectSettings,
    Diagnostics,
    ConductorTrack,
    SegmentEditor,
    EventInstrumentEditor,
    AllTracks
}

public readonly record struct WorkspaceKey(WorkspaceKind Kind, MidoraId? ObjectId)
{
    public static WorkspaceKey ForType(WorkspaceKind kind)
    {
        if (kind is WorkspaceKind.SegmentEditor
            or WorkspaceKind.EventInstrumentEditor)
        {
            throw new ArgumentException("This workspace kind requires an object identity.", nameof(kind));
        }
        return new(kind, null);
    }

    public static WorkspaceKey ForObject(WorkspaceKind kind, MidoraId objectId)
    {
        if (kind is not (WorkspaceKind.SegmentEditor
            or WorkspaceKind.EventInstrumentEditor))
        {
            throw new ArgumentException("This workspace kind is unique by type.", nameof(kind));
        }
        if (objectId.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(objectId));
        }
        return new(kind, objectId);
    }
}

public enum WorkspaceSelectionRangeMode
{
    Replace,
    Add,
    Toggle,
    Remove
}

/// <summary>
/// Describes the formal Timeline source that owns selected stable IDs without
/// resolving those IDs back through a potentially paged Project collection.
/// The value is deliberately presentation-only: it is a menu/gesture routing
/// hint and never replaces stable IDs as the formal edit input.
/// </summary>
public enum WorkspaceTimelineSelectionKind
{
    ArrangementSegment,
    LogicalNote,
    LogicalParameterPoint,
    DirectMidiNote,
    DirectMidiEventPoint,
    TemplateNote,
    SubVoiceEventPoint
}

public readonly record struct WorkspaceTimelineSelectionSource(
    WorkspaceTimelineSelectionKind Kind,
    MidoraId? OwnerId = null,
    MidoraId? SecondaryOwnerId = null,
    MidiValueTarget? MidiTarget = null,
    DirectMidiChannelEventKind? DirectMidiEventKind = null,
    int DirectMidiData1 = 0,
    double PointMinimum = 0,
    double PointMaximum = 127)
{
    // QuantizeScope returns this same record type; synthesized record formatting
    // would recurse indefinitely through that computed property.
    public override string ToString() => $"WorkspaceTimelineSelectionSource {{ Kind = {Kind} }}";

    /// <summary>
    /// Quantize may operate across several event lanes owned by the same
    /// Segment/SubVoice. Other edits still require the exact lane source.
    /// </summary>
    public WorkspaceTimelineSelectionSource QuantizeScope => Kind switch
    {
        WorkspaceTimelineSelectionKind.LogicalParameterPoint => this with
        {
            SecondaryOwnerId = null,
            PointMinimum = 0,
            PointMaximum = 127
        },
        WorkspaceTimelineSelectionKind.DirectMidiEventPoint => this with
        {
            DirectMidiEventKind = null,
            DirectMidiData1 = 0,
            PointMinimum = 0,
            PointMaximum = 127
        },
        WorkspaceTimelineSelectionKind.SubVoiceEventPoint => this with
        {
            MidiTarget = null,
            PointMinimum = 0,
            PointMaximum = 127
        },
        _ => this
    };
}

/// <summary>
/// Immutable selection representation prepared away from the Dispatcher and
/// adopted after a Project root publication without enumeration or allocation.
/// </summary>
public sealed class PreparedWorkspaceSelectionProjection
{
    private PreparedWorkspaceSelectionProjection(
        CompressedMidoraIdSet ids,
        MidoraId? primary,
        MidoraId? anchor,
        Dictionary<WorkspaceTimelineSelectionSource, int>? timelineSources,
        WorkspaceTimelineSelectionSource? homogeneousTimelineSource,
        WorkspaceTimelineSelectionSource? homogeneousTimelineQuantizeScope)
    {
        Ids = ids;
        Primary = primary;
        Anchor = anchor;
        TimelineSources = timelineSources;
        HomogeneousTimelineSource = homogeneousTimelineSource;
        HomogeneousTimelineQuantizeScope = homogeneousTimelineQuantizeScope;
    }

    internal CompressedMidoraIdSet Ids { get; }
    public int Count => Ids.Count;
    public MidoraId? Primary { get; }
    public MidoraId? Anchor { get; }
    internal Dictionary<WorkspaceTimelineSelectionSource, int>? TimelineSources { get; }
    internal WorkspaceTimelineSelectionSource? HomogeneousTimelineSource { get; }
    internal WorkspaceTimelineSelectionSource? HomogeneousTimelineQuantizeScope { get; }

    public static PreparedWorkspaceSelectionProjection Create(
        IReadOnlyList<MidoraId> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        CompressedMidoraIdSet compressed =
            CompressedMidoraIdSet.Create(ids, cancellationToken);
        MidoraId? primary = ids.Count == 0 ? null : ids[0];
        if (primary is MidoraId primaryId && !compressed.Contains(primaryId))
        {
            throw new InvalidOperationException(
                "The prepared primary selection is absent from its selection set.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(compressed, primary, primary, null, null, null);
    }

    internal static PreparedWorkspaceSelectionProjection CreateTrusted(
        CompressedMidoraIdSet ids,
        MidoraId? primary,
        Dictionary<WorkspaceTimelineSelectionSource, int>? timelineSources,
        WorkspaceTimelineSelectionSource? homogeneousTimelineSource,
        WorkspaceTimelineSelectionSource? homogeneousTimelineQuantizeScope) =>
        new(
            ids,
            primary,
            primary,
            timelineSources,
            homogeneousTimelineSource,
            homogeneousTimelineQuantizeScope);
}

public sealed class WorkspaceSelection
{
    private CompressedMidoraIdSet _ids = CompressedMidoraIdSet.Empty;
    private static readonly ConditionalWeakTable<CompressedMidoraIdSet, TimelineSourceSnapshot>
        _timelineSourceBySelectionRoot = new();
    private Dictionary<WorkspaceTimelineSelectionSource, int>? _timelineSources = [];
    private WorkspaceTimelineSelectionSource? _homogeneousTimelineSource;
    private WorkspaceTimelineSelectionSource? _homogeneousTimelineQuantizeScope;

    public IReadOnlyCollection<MidoraId> Ids => _ids;
    public IReadOnlySet<MidoraId> IdSet => _ids;
    public CompressedMidoraIdSet SharedIds => _ids;
    public MidoraId? Primary { get; private set; }
    public MidoraId? Anchor { get; private set; }
    public long Revision { get; private set; }
    public WorkspaceTimelineSelectionSource? HomogeneousTimelineSource =>
        _homogeneousTimelineSource;
    public WorkspaceTimelineSelectionSource? HomogeneousTimelineQuantizeScope =>
        _homogeneousTimelineQuantizeScope;

    public void Replace(MidoraId id)
    {
        Validate(id);
        if (_ids.Count == 1 && _ids.Contains(id) && Primary == id && Anchor == id)
        {
            InvalidateTimelineSources();
            return;
        }
        _ids = CompressedMidoraIdSet.Create([id]);
        Primary = id;
        Anchor = id;
        Revision = checked(Revision + 1);
        InvalidateTimelineSources();
    }

    public void Replace(MidoraId id, WorkspaceTimelineSelectionSource source)
    {
        Validate(id);
        bool changed = !(_ids.Count == 1
            && _ids.Contains(id)
            && Primary == id
            && Anchor == id);
        _ids = CompressedMidoraIdSet.Create([id]);
        Primary = id;
        Anchor = id;
        if (changed) Revision = checked(Revision + 1);
        SetTimelineSources(new Dictionary<WorkspaceTimelineSelectionSource, int>
        {
            [source] = 1
        });
    }

    public void Add(MidoraId id, bool makePrimary = true)
    {
        Validate(id);
        CompressedMidoraIdSet replacement = _ids.Add(id);
        bool changed = !ReferenceEquals(replacement, _ids);
        _ids = replacement;
        if (makePrimary || Primary is null)
        {
            changed |= Primary != id;
            Primary = id;
        }
        if (Anchor is null)
        {
            Anchor = id;
            changed = true;
        }
        if (changed)
        {
            Revision = checked(Revision + 1);
            InvalidateTimelineSources();
        }
    }

    public void Add(
        MidoraId id,
        WorkspaceTimelineSelectionSource source,
        bool makePrimary = true)
    {
        Validate(id);
        bool existed = _ids.Contains(id);
        Dictionary<WorkspaceTimelineSelectionSource, int>? sources = CloneTimelineSources();
        // A one-item selection becomes fully known once that same item is
        // explicitly invoked on a typed Timeline surface. This also retargets
        // a SubVoice event exposed by more than one lane. Larger selections
        // keep their composition: one clicked member cannot prove that every
        // other member belongs to the new lane.
        if (existed && _ids.Count == 1)
        {
            sources = new Dictionary<WorkspaceTimelineSelectionSource, int>
            {
                [source] = 1
            };
        }
        CompressedMidoraIdSet replacement = _ids.Add(id);
        bool changed = !ReferenceEquals(replacement, _ids);
        _ids = replacement;
        if (makePrimary || Primary is null)
        {
            changed |= Primary != id;
            Primary = id;
        }
        if (Anchor is null)
        {
            Anchor = id;
            changed = true;
        }
        if (changed) Revision = checked(Revision + 1);
        if (!existed && sources is not null)
            IncrementSource(sources, source, 1);
        SetTimelineSources(sources);
    }

    public void Toggle(MidoraId id)
    {
        Validate(id);
        if (!_ids.Contains(id))
        {
            Add(id);
            return;
        }
        _ids = _ids.Remove(id);
        if (Primary == id)
        {
            Primary = GetMinimumOrNull();
        }
        if (Anchor == id)
        {
            Anchor = Primary;
        }
        Revision = checked(Revision + 1);
        InvalidateTimelineSources();
    }

    public void Toggle(MidoraId id, WorkspaceTimelineSelectionSource source)
    {
        Validate(id);
        bool existed = _ids.Contains(id);
        Dictionary<WorkspaceTimelineSelectionSource, int>? sources = CloneTimelineSources();
        if (!existed)
        {
            _ids = _ids.Add(id);
            Primary = id;
            Anchor ??= id;
            if (sources is not null) IncrementSource(sources, source, 1);
        }
        else
        {
            _ids = _ids.Remove(id);
            if (Primary == id) Primary = GetMinimumOrNull();
            if (Anchor == id) Anchor = Primary;
            if (sources is not null && !IncrementSource(sources, source, -1))
                sources = null;
        }
        Revision = checked(Revision + 1);
        SetTimelineSources(sources);
    }

    public void Remove(MidoraId id)
    {
        CompressedMidoraIdSet replacement = _ids.Remove(id);
        if (ReferenceEquals(replacement, _ids))
        {
            return;
        }
        _ids = replacement;
        if (Primary == id)
        {
            Primary = GetMinimumOrNull();
        }
        if (Anchor == id)
        {
            Anchor = Primary;
        }
        Revision = checked(Revision + 1);
        InvalidateTimelineSources();
    }

    /// <summary>
    /// Atomically removes every selected ID not present in <paramref name="validIds"/>.
    /// Large post-edit selection pruning must publish one immutable root and one
    /// revision instead of allocating a new tree once per deleted object.
    /// </summary>
    public bool RetainOnly(IReadOnlySet<MidoraId> validIds)
    {
        ArgumentNullException.ThrowIfNull(validIds);
        CompressedMidoraIdSet replacement = _ids.Intersect(validIds);
        if (replacement.Count == _ids.Count) return false;

        _ids = replacement;
        if (Primary is MidoraId primary && !_ids.Contains(primary))
            Primary = GetMinimumOrNull();
        if (Anchor is MidoraId anchor && !_ids.Contains(anchor)) Anchor = Primary;
        Revision = checked(Revision + 1);
        if (_ids.Count == 0)
        {
            SetTimelineSources([]);
        }
        else if (_timelineSources is { Count: 1 })
        {
            WorkspaceTimelineSelectionSource source = _timelineSources.Keys.Single();
            SetTimelineSources(new Dictionary<WorkspaceTimelineSelectionSource, int>
            {
                [source] = _ids.Count
            });
        }
        else
        {
            InvalidateTimelineSources();
        }
        return true;
    }

    public void Clear()
    {
        // Revision is also the selection-intent epoch used to fence asynchronous
        // range materializations.  Clearing an already-empty set is therefore
        // not a semantic no-op: it means that the user explicitly rejected any
        // older selection gesture which may still be running on another surface.
        // Without this epoch advance, a pending Replace marquee can repopulate a
        // selection after an explicit Deselect command.
        _ids = CompressedMidoraIdSet.Empty;
        Primary = null;
        Anchor = null;
        Revision = checked(Revision + 1);
        SetTimelineSources([]);
    }

    /// <summary>
    /// Atomically adopts a fully materialized immutable selection. This allows
    /// an exact, potentially very large selection to be built away from the UI
    /// thread and installed without copying it again on the Dispatcher thread.
    /// </summary>
    public void AdoptMaterialized(
        CompressedMidoraIdSet ids,
        MidoraId? primary = null,
        MidoraId? anchor = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (primary is MidoraId primaryId && !ids.Contains(primaryId))
        {
            throw new ArgumentException(
                "Primary selection must belong to the selection set.",
                nameof(primary));
        }
        if (anchor is MidoraId anchorId && !ids.Contains(anchorId))
        {
            throw new ArgumentException(
                "Selection anchor must belong to the selection set.",
                nameof(anchor));
        }
        _ids = ids;
        Primary = primary;
        Anchor = anchor ?? primary;
        Revision = checked(Revision + 1);
        RestoreTimelineSources(ids);
    }

    /// <summary>
    /// Adopts a projection whose validation and potentially large compression
    /// were completed before the corresponding Project publication point.
    /// This method deliberately performs only fixed-cost field publication.
    /// </summary>
    public void AdoptPrepared(PreparedWorkspaceSelectionProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        _ids = projection.Ids;
        Primary = projection.Primary;
        Anchor = projection.Anchor;
        Revision = unchecked(Revision + 1);
        _timelineSources = projection.TimelineSources;
        _homogeneousTimelineSource = projection.HomogeneousTimelineSource;
        _homogeneousTimelineQuantizeScope =
            projection.HomogeneousTimelineQuantizeScope;
    }

    /// <summary>
    /// Builds the exact compressed result and all optional menu-routing state
    /// before Project publication. <see cref="AdoptPrepared"/> can consequently
    /// remain a fixed-cost, non-allocating field publication.
    /// </summary>
    public PreparedWorkspaceSelectionProjection PrepareProjection(
        IReadOnlyList<MidoraId> resultIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultIds);
        cancellationToken.ThrowIfCancellationRequested();
        CompressedMidoraIdSet ids =
            CompressedMidoraIdSet.Create(resultIds, cancellationToken);
        MidoraId? primary = resultIds.Count == 0 ? null : resultIds[0];
        Dictionary<WorkspaceTimelineSelectionSource, int>? sources = null;
        WorkspaceTimelineSelectionSource? exact = null;
        WorkspaceTimelineSelectionSource? quantize = null;
        if (ids.Count == 0)
        {
            sources = [];
        }
        else if (ids.Count == _ids.Count && ids.SetEquals(_ids))
        {
            ids = _ids;
            sources = _timelineSources;
            exact = _homogeneousTimelineSource;
            quantize = _homogeneousTimelineQuantizeScope;
        }
        else if (_homogeneousTimelineSource is WorkspaceTimelineSelectionSource source)
        {
            sources = new Dictionary<WorkspaceTimelineSelectionSource, int>
            {
                [source] = ids.Count
            };
            exact = source;
            quantize = source.QuantizeScope;
        }
        else
        {
            // A cross-lane event-point selection may lose members to exact-tick
            // collision resolution while still remaining in the same owner-wide
            // quantize scope.  Preserve that bounded routing fact even though an
            // exact per-lane composition can no longer be inferred.
            quantize = _homogeneousTimelineQuantizeScope;
        }
        RememberTimelineSources(ids, new TimelineSourceSnapshot(
            sources,
            exact,
            quantize));
        cancellationToken.ThrowIfCancellationRequested();
        return PreparedWorkspaceSelectionProjection.CreateTrusted(
            ids,
            primary,
            sources,
            exact,
            quantize);
    }

    /// <summary>
    /// Prepares a replacement selection whose destination is known independently
    /// of the old selection (for example Paste after Cut, or a cross-owner Paste).
    /// A null source is intentional and must not inherit the previous kind/lane.
    /// </summary>
    public PreparedWorkspaceSelectionProjection PrepareProjection(
        IReadOnlyList<MidoraId> resultIds,
        WorkspaceTimelineSelectionSource? resultSource,
        WorkspaceTimelineSelectionSource? resultQuantizeScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resultIds);
        cancellationToken.ThrowIfCancellationRequested();
        CompressedMidoraIdSet ids = CompressedMidoraIdSet.Create(resultIds, cancellationToken);
        MidoraId? primary = resultIds.Count == 0 ? null : resultIds[0];
        Dictionary<WorkspaceTimelineSelectionSource, int>? sources = null;
        if (ids.Count == 0)
        {
            sources = [];
            resultSource = null;
            resultQuantizeScope = null;
        }
        else if (resultSource is WorkspaceTimelineSelectionSource source)
        {
            sources = new() { [source] = ids.Count };
            resultQuantizeScope = source.QuantizeScope;
        }
        RememberTimelineSources(ids, new TimelineSourceSnapshot(
            sources, resultSource, resultQuantizeScope));
        cancellationToken.ThrowIfCancellationRequested();
        return PreparedWorkspaceSelectionProjection.CreateTrusted(
            ids, primary, sources, resultSource, resultQuantizeScope);
    }

    public void AdoptMaterialized(
        IEnumerable<MidoraId> ids,
        MidoraId? primary = null,
        MidoraId? anchor = null) =>
        AdoptMaterialized(CompressedMidoraIdSet.Create(ids), primary, anchor);

    public bool ReplaceAll(IEnumerable<MidoraId> ids, MidoraId? primary)
    {
        ArgumentNullException.ThrowIfNull(ids);
        CompressedMidoraIdSet.Builder builder = CompressedMidoraIdSet.CreateBuilder();
        MidoraId? first = null;
        foreach (MidoraId id in ids)
        {
            Validate(id);
            if (builder.Add(id)) first ??= id;
        }
        CompressedMidoraIdSet replacement = builder.Build();
        if (primary is MidoraId primaryId && !replacement.Contains(primaryId))
        {
            throw new ArgumentException(
                "Primary selection must belong to the replacement set.",
                nameof(primary));
        }
        MidoraId? normalizedPrimary = primary
            ?? first;
        if (Primary == normalizedPrimary && _ids.SetEquals(replacement))
        {
            return false;
        }
        _ids = replacement;
        Primary = normalizedPrimary;
        Anchor = normalizedPrimary;
        Revision = checked(Revision + 1);
        InvalidateTimelineSources();
        return true;
    }

    public void ApplyRange(
        IEnumerable<MidoraId> ids,
        WorkspaceSelectionRangeMode mode)
    {
        ArgumentNullException.ThrowIfNull(ids);
        CompressedMidoraIdSet.Builder builder = CompressedMidoraIdSet.CreateBuilder();
        MidoraId? first = null;
        foreach (MidoraId id in ids)
        {
            Validate(id);
            if (builder.Add(id)) first ??= id;
        }
        CompressedMidoraIdSet materialized = builder.Build();
        bool changed = false;
        switch (mode)
        {
            case WorkspaceSelectionRangeMode.Replace:
                MidoraId? replacementPrimary = first;
                if (_ids.Count == materialized.Count
                    && _ids.SetEquals(materialized)
                    && Primary == replacementPrimary
                    && Anchor == replacementPrimary)
                {
                    return;
                }
                _ids = materialized;
                Primary = replacementPrimary;
                Anchor = Primary;
                changed = true;
                break;
            case WorkspaceSelectionRangeMode.Add:
                CompressedMidoraIdSet added = _ids.Union(materialized);
                changed = !ReferenceEquals(added, _ids);
                _ids = added;
                if (Primary is null && first is MidoraId firstAdded)
                {
                    Primary = firstAdded;
                    Anchor ??= Primary;
                    changed = true;
                }
                break;
            case WorkspaceSelectionRangeMode.Toggle:
                if (materialized.Count != 0)
                {
                    _ids = _ids.SymmetricExcept(materialized);
                    changed = true;
                }
                NormalizeEndpoints();
                break;
            case WorkspaceSelectionRangeMode.Remove:
                CompressedMidoraIdSet removed = _ids.Except(materialized);
                changed = !ReferenceEquals(removed, _ids);
                _ids = removed;
                NormalizeEndpoints();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (changed)
        {
            Revision = checked(Revision + 1);
            InvalidateTimelineSources();
        }

        void NormalizeEndpoints()
        {
            if (Primary is MidoraId primary && !_ids.Contains(primary))
            {
                Primary = GetMinimumOrNull();
            }
            if (Anchor is MidoraId anchor && !_ids.Contains(anchor))
            {
                Anchor = Primary;
            }
            if (_ids.Count == 0)
            {
                Primary = null;
                Anchor = null;
            }
        }
    }

    public void ApplyRange(
        IEnumerable<MidoraId> ids,
        WorkspaceSelectionRangeMode mode,
        WorkspaceTimelineSelectionSource source)
    {
        ArgumentNullException.ThrowIfNull(ids);
        CompressedMidoraIdSet.Builder builder = CompressedMidoraIdSet.CreateBuilder();
        MidoraId? first = null;
        foreach (MidoraId id in ids)
        {
            Validate(id);
            if (builder.Add(id)) first ??= id;
        }
        CompressedMidoraIdSet materialized = builder.Build();
        int intersectionCount = CountIntersection(_ids, materialized);
        CompressedMidoraIdSet result = mode switch
        {
            WorkspaceSelectionRangeMode.Replace => materialized,
            WorkspaceSelectionRangeMode.Add => _ids.Union(materialized),
            WorkspaceSelectionRangeMode.Toggle => _ids.SymmetricExcept(materialized),
            WorkspaceSelectionRangeMode.Remove => _ids.Except(materialized),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        MidoraId? primary = mode == WorkspaceSelectionRangeMode.Replace
            ? first
            : Primary is MidoraId currentPrimary && result.Contains(currentPrimary)
                ? currentPrimary
                : result.TryGetMinimum(out MidoraId minimum) ? minimum : null;
        MidoraId? anchor = mode == WorkspaceSelectionRangeMode.Replace
            ? primary
            : Anchor is MidoraId currentAnchor && result.Contains(currentAnchor)
                ? currentAnchor
                : primary;
        bool changed = !ReferenceEquals(result, _ids)
            || Primary != primary
            || Anchor != anchor;
        RegisterTimelineMaterialization(
            result,
            source,
            mode,
            materialized.Count,
            intersectionCount);
        _ids = result;
        Primary = primary;
        Anchor = anchor;
        if (changed) Revision = checked(Revision + 1);
        RestoreTimelineSources(_ids);
    }

    /// <summary>
    /// Attaches source composition to an immutable result root before another
    /// layer atomically adopts that root. No selected ID is enumerated here.
    /// </summary>
    public void RegisterTimelineMaterialization(
        CompressedMidoraIdSet result,
        WorkspaceTimelineSelectionSource source,
        WorkspaceSelectionRangeMode mode,
        int rangeCount,
        int baseIntersectionCount)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfNegative(rangeCount);
        ArgumentOutOfRangeException.ThrowIfNegative(baseIntersectionCount);
        if (baseIntersectionCount > rangeCount)
            throw new ArgumentOutOfRangeException(nameof(baseIntersectionCount));

        Dictionary<WorkspaceTimelineSelectionSource, int>? sources =
            mode == WorkspaceSelectionRangeMode.Replace
                ? []
                : CloneTimelineSources();
        if (sources is not null)
        {
            int delta = mode switch
            {
                WorkspaceSelectionRangeMode.Replace => result.Count,
                WorkspaceSelectionRangeMode.Add => rangeCount - baseIntersectionCount,
                WorkspaceSelectionRangeMode.Remove => -baseIntersectionCount,
                WorkspaceSelectionRangeMode.Toggle => rangeCount - (2 * baseIntersectionCount),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            if (delta != 0 && !IncrementSource(sources, source, delta))
                sources = null;
            if (sources is not null && sources.Values.Sum() != result.Count)
                sources = null;
        }
        RememberTimelineSources(result, sources);
    }

    private void RestoreTimelineSources(CompressedMidoraIdSet root)
    {
        if (_timelineSourceBySelectionRoot.TryGetValue(
                root,
                out TimelineSourceSnapshot? snapshot))
        {
            AdoptTimelineSourceSnapshot(snapshot);
        }
        else if (root.Count == 0)
        {
            AdoptTimelineSourceSnapshot(TimelineSourceSnapshot.Empty);
        }
        else
        {
            AdoptTimelineSourceSnapshot(snapshot: null);
        }
    }

    private void InvalidateTimelineSources() => SetTimelineSources(null);

    private Dictionary<WorkspaceTimelineSelectionSource, int>? CloneTimelineSources() =>
        _timelineSources is null
            ? null
            : new Dictionary<WorkspaceTimelineSelectionSource, int>(_timelineSources);

    private void SetTimelineSources(
        Dictionary<WorkspaceTimelineSelectionSource, int>? sources)
    {
        // Empty is a complete, unambiguous source composition. Normalizing it
        // here lets a subsequent typed Add/Toggle establish a fresh context
        // even when the preceding removal started from an unknown legacy set.
        if (_ids.Count == 0) sources ??= [];
        if (sources is not null)
        {
            foreach (WorkspaceTimelineSelectionSource key in sources
                         .Where(static pair => pair.Value <= 0)
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                sources.Remove(key);
            }
            if (sources.Values.Sum() != _ids.Count) sources = null;
        }
        _timelineSources = sources;
        _homogeneousTimelineSource = sources is { Count: 1 }
            ? sources.Keys.Single()
            : null;
        _homogeneousTimelineQuantizeScope = null;
        if (sources is { Count: > 0 })
        {
            WorkspaceTimelineSelectionSource scope = sources.Keys.First().QuantizeScope;
            if (sources.Keys.All(value => value.QuantizeScope == scope))
                _homogeneousTimelineQuantizeScope = scope;
        }
        RememberTimelineSources(_ids, sources);
    }

    private void RememberTimelineSources(
        CompressedMidoraIdSet root,
        Dictionary<WorkspaceTimelineSelectionSource, int>? sources)
    {
        RememberTimelineSources(
            root,
            sources is null ? null : new TimelineSourceSnapshot(sources));
    }

    private void RememberTimelineSources(
        CompressedMidoraIdSet root,
        TimelineSourceSnapshot? snapshot)
    {
        _timelineSourceBySelectionRoot.Remove(root);
        if (snapshot is not null) _timelineSourceBySelectionRoot.Add(root, snapshot);
    }

    private void AdoptTimelineSourceSnapshot(TimelineSourceSnapshot? snapshot)
    {
        _timelineSources = snapshot?.Sources;
        _homogeneousTimelineSource = snapshot?.HomogeneousTimelineSource;
        _homogeneousTimelineQuantizeScope =
            snapshot?.HomogeneousTimelineQuantizeScope;
    }

    private static bool IncrementSource(
        Dictionary<WorkspaceTimelineSelectionSource, int> sources,
        WorkspaceTimelineSelectionSource source,
        int delta)
    {
        sources.TryGetValue(source, out int count);
        long updated = (long)count + delta;
        if (updated < 0 || updated > int.MaxValue) return false;
        if (updated == 0) sources.Remove(source);
        else sources[source] = (int)updated;
        return true;
    }

    private static int CountIntersection(
        CompressedMidoraIdSet left,
        CompressedMidoraIdSet right)
    {
        if (left.Count == 0 || right.Count == 0) return 0;
        CompressedMidoraIdSet smaller = left.Count <= right.Count ? left : right;
        CompressedMidoraIdSet larger = ReferenceEquals(smaller, left) ? right : left;
        int count = 0;
        foreach (MidoraId id in smaller)
        {
            if (larger.Contains(id)) count++;
        }
        return count;
    }

    private sealed class TimelineSourceSnapshot
    {
        public static TimelineSourceSnapshot Empty { get; } = new(
            new Dictionary<WorkspaceTimelineSelectionSource, int>(),
            homogeneousTimelineSource: null,
            homogeneousTimelineQuantizeScope: null);

        public TimelineSourceSnapshot(
            IReadOnlyDictionary<WorkspaceTimelineSelectionSource, int> sources)
            : this(
                new Dictionary<WorkspaceTimelineSelectionSource, int>(sources),
                sources.Count == 1 ? sources.Keys.Single() : null,
                GetHomogeneousQuantizeScope(sources))
        {
        }

        public TimelineSourceSnapshot(
            Dictionary<WorkspaceTimelineSelectionSource, int>? sources,
            WorkspaceTimelineSelectionSource? homogeneousTimelineSource,
            WorkspaceTimelineSelectionSource? homogeneousTimelineQuantizeScope)
        {
            Sources = sources;
            HomogeneousTimelineSource = homogeneousTimelineSource;
            HomogeneousTimelineQuantizeScope = homogeneousTimelineQuantizeScope;
        }

        public Dictionary<WorkspaceTimelineSelectionSource, int>? Sources { get; }
        public WorkspaceTimelineSelectionSource? HomogeneousTimelineSource { get; }
        public WorkspaceTimelineSelectionSource? HomogeneousTimelineQuantizeScope { get; }

        private static WorkspaceTimelineSelectionSource? GetHomogeneousQuantizeScope(
            IReadOnlyDictionary<WorkspaceTimelineSelectionSource, int> sources)
        {
            if (sources.Count == 0) return null;
            WorkspaceTimelineSelectionSource scope = sources.Keys.First().QuantizeScope;
            return sources.Keys.All(value => value.QuantizeScope == scope)
                ? scope
                : null;
        }
    }

    private static void Validate(MidoraId id)
    {
        if (id.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }
    }

    private MidoraId? GetMinimumOrNull() =>
        _ids.TryGetMinimum(out MidoraId minimum) ? minimum : null;
}

public static class TimelineSnap
{
    public static long Snap(long tick, long gridStep, int movementDirection) =>
        ProjectTimelineGrid.Snap(tick, gridStep, movementDirection);
}

public static class TimelineGridQuantization
{
    public readonly record struct SnappedRange(long StartTick, long EndTick);

    public static long SnapAbsolute(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap,
        int movementDirection)
        => ProjectTimelineGrid.SnapAbsolute(
            tick,
            fixedStepTicks,
            useBars,
            timeSignatureMap,
            movementDirection);

    public static long SnapDelta(
        long delta,
        long targetTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
        => ProjectTimelineGrid.SnapDelta(
            delta,
            targetTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);

    public static long GetGridTickAtOrAfter(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
        => ProjectTimelineGrid.GetGridTickAtOrAfter(
            tick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);

    public static long GetNextGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
        => ProjectTimelineGrid.GetNextGridTick(
            tick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);

    public static long GetPreviousGridTick(
        long tick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
        => ProjectTimelineGrid.GetPreviousGridTick(
            tick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);

    public static SnappedRange SnapRangeFromAnchor(
        long rawAnchorTick,
        long rawMovingTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ProjectTimelineGrid.SnappedRange result = ProjectTimelineGrid.SnapRangeFromAnchor(
            rawAnchorTick,
            rawMovingTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);
        return new(result.StartTick, result.EndTick);
    }

    public static SnappedRange SnapPositiveRange(
        long rawStartTick,
        long rawEndTick,
        long fixedStepTicks,
        bool useBars,
        ProjectTimeSignatureMap? timeSignatureMap)
    {
        ProjectTimelineGrid.SnappedRange result = ProjectTimelineGrid.SnapPositiveRange(
            rawStartTick,
            rawEndTick,
            fixedStepTicks,
            useBars,
            timeSignatureMap);
        return new(result.StartTick, result.EndTick);
    }
}
