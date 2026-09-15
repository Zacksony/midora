using System.Collections.Immutable;

namespace Midora.Domain;

/// <summary>
/// Stable category of a timeline owner whose immutable content root changed.
/// </summary>
public enum ProjectTimelineOwnerKind
{
    LogicalSegment,
    DirectMidiSegment,
    SubVoice
}

/// <summary>
/// Stable source category inside a timeline owner.  A lane/curve Stable ID is
/// carried separately when the category can occur more than once per owner.
/// </summary>
public enum ProjectTimelineSourceKind
{
    LogicalNotes,
    LogicalParameterPoints,
    DirectMidiNotes,
    DirectMidiChannelEvents,
    DirectMidiOpaqueEvents,
    SubVoiceEvents,
    SubVoiceCurvePoints
}

/// <summary>
/// Exact half-open content range affected by one source-root exchange.  Tick
/// coordinates are owner-local and may be negative for cropped Segment data.
/// Lane bounds are inclusive.
/// </summary>
public readonly record struct ProjectTimelineContentChangeRange
{
    public ProjectTimelineContentChangeRange(
        long startTick,
        long endTick,
        int minimumLane,
        int maximumLane)
    {
        if (endTick <= startTick) throw new ArgumentOutOfRangeException(nameof(endTick));
        if (maximumLane < minimumLane) throw new ArgumentOutOfRangeException(nameof(maximumLane));
        StartTick = startTick;
        EndTick = endTick;
        MinimumLane = minimumLane;
        MaximumLane = maximumLane;
    }

    public long StartTick { get; }
    public long EndTick { get; }
    public int MinimumLane { get; }
    public int MaximumLane { get; }
}

/// <summary>
/// Compressed source trace and exact invalidation footprint for one timeline
/// source.  Ordinals are meaningful only at <see cref="PreviousRevision"/>;
/// page indices identify the immutable source pages replaced by the edit.
/// </summary>
public sealed record ProjectTimelineSourceChange(
    ProjectTimelineSourceKind SourceKind,
    MidoraId? LaneOrCurveId,
    long PreviousRevision,
    long CurrentRevision,
    IReadOnlyList<TimelineOrdinalRange> OrdinalRanges,
    ImmutableArray<int> PageIndices,
    IReadOnlyList<ProjectTimelineContentChangeRange> ContentRanges)
{
    /// <summary>
    /// Exact footprint in <see cref="PreviousRevision"/>.  This is kept
    /// separately from the invalidation union so a consumer does not need to
    /// infer the old extent by reopening or rescanning the owner.
    /// </summary>
    public IReadOnlyList<ProjectTimelineContentChangeRange> PreviousContentRanges { get; init; } = [];

    /// <summary>
    /// Exact footprint in <see cref="CurrentRevision"/>.
    /// </summary>
    public IReadOnlyList<ProjectTimelineContentChangeRange> CurrentContentRanges { get; init; } = [];

    /// <summary>
    /// Ordinal trace of surviving/created values in the current revision.
    /// <see cref="OrdinalRanges"/> remains the source trace for the previous
    /// revision.  Both are revision-local and must never be persisted.
    /// </summary>
    public IReadOnlyList<TimelineOrdinalRange> CurrentOrdinalRanges { get; init; } = [];

    /// <summary>
    /// Physical immutable pages in the current revision when the source can
    /// expose their real identities.  Empty is intentional for sources that
    /// only expose ordinal pages; callers must not manufacture page numbers
    /// from ordinal/page-capacity arithmetic.
    /// </summary>
    public ImmutableArray<int> CurrentPageIndices { get; init; } = [];
}

/// <summary>
/// Exact, owner-scoped source changes published with one atomic Project edit.
/// Empty means that the command has not supplied a fine-grained footprint and
/// consumers must continue to honor the ordinary coarse ProjectChangeSet.
/// </summary>
public sealed record ProjectTimelineOwnerChangeSet(
    MidoraId OwnerId,
    ProjectTimelineOwnerKind OwnerKind,
    ImmutableArray<ProjectTimelineSourceChange> Sources);
