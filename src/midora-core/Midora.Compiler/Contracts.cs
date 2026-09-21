using Midora.Domain;
using Midora.Midi;
using Midora.Common;

namespace Midora.Compiler;

internal static class CanonicalMidiOrdering
{
    public static int DirectEndpointOrder(MidiMessage message) =>
        message.MessageType == MidiMessageType.NoteOff
            || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0
                ? 0
                : 1;
}

public enum DiagnosticSeverity
{
    Debug,
    Info,
    Warning,
    Error
}

public enum SourceOrigin
{
    Unspecified,
    TemplateEvent,
    ValueCurve,
    LogicalParameterMapping,
    MergedInitialState,
    ProjectResetDefaults,
    RangeRestore,
    CompilerBoundaryCleanup,
    DirectMidiNote,
    DirectMidiChannelEvent,
    OpaqueMidiEvent,
    MidiChannelRootLifecycle
}

public readonly record struct SourceReference(
    MidoraId TrackId = default,
    MidoraId SegmentId = default,
    MidoraId LogicalNoteId = default,
    MidoraId EventInstrumentId = default,
    MidoraId SubVoiceId = default,
    MidoraId SourceEventId = default,
    long Tick = -1,
    MidoraId LogicalParameterId = default,
    MidoraId LogicalParameterMappingId = default,
    MidoraId MappingStepId = default,
    MidoraId MappingFunctionId = default,
    MidoraId ValueCurveId = default,
    MidoraId EnvelopeId = default,
    SourceOrigin Origin = SourceOrigin.Unspecified,
    MidoraId MidiChannelRootId = default,
    MidoraId PureMidiTrackId = default,
    MidoraId MidiSegmentId = default,
    MidoraId DirectMidiObjectId = default,
    MidoraId ExportTrackId = default,
    MidoraId EventInstrumentUsageId = default);

public sealed record CompilerDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    SourceReference Source);

public enum CompilationPurpose
{
    FullProject,
    Range,
    Playback,
    SegmentPreview,
    EventInstrumentPreview,
    MidiExport,
    AudioRender,
    LogicalTrackAudioRender
}

public enum CompilationFailureStage
{
    SemanticValidation,
    InstanceExpansion,
    OverlapValidation,
    ResourceAllocation,
    WarningPolicy
}

public sealed class CompilationRequest
{
    public CompilationProgress? Progress { get; init; }
    public CompilationPurpose Purpose { get; init; } = CompilationPurpose.FullProject;
    public long StartTick { get; init; }
    public long? EndTick { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
    public bool CollectDebugDiagnostics { get; init; }
    public HashSet<MidoraId>? IncludedTrackIds { get; init; }
    public HashSet<MidoraId>? IncludedSubVoiceIds { get; init; }

    /// <summary>
    /// Internal held-preview causal window. The instance gate remains open for the whole window,
    /// MappingContext.GateLength is Int64.MaxValue, and the range end must not synthesize cleanup.
    /// </summary>
    public bool HeldPreviewGateOpen { get; init; }

    /// <summary>
    /// Internal held-preview Gate End override. The instance ends at its source Note length, while
    /// MappingContext.GateLength observes the frozen user/draft Gate length from Gate Start.
    /// </summary>
    public long? HeldPreviewFinalGateLengthTicks { get; init; }
}

public enum CompilationEndTickSource
{
    ExplicitRequest,
    ProjectEndMarker,
    NaturalContent
}

public sealed partial class CompilationContextSummary
{
    private readonly MidoraId[] _includedTrackIds;
    private readonly MidoraId[] _includedSubVoiceIds;

    internal CompilationContextSummary(
        CompilationRequest request,
        long resolvedEndTick,
        CompilationEndTickSource endTickSource)
    {
        ArgumentNullException.ThrowIfNull(request);
        Purpose = request.Purpose;
        StartTick = request.StartTick;
        RequestedEndTick = request.EndTick;
        EndTick = resolvedEndTick;
        EndTickSource = endTickSource;
        IncludesAllTracks = request.IncludedTrackIds is null;
        IncludesAllSubVoices = request.IncludedSubVoiceIds is null;
        _includedTrackIds = request.IncludedTrackIds?
            .OrderBy(id => id)
            .ToArray() ?? [];
        _includedSubVoiceIds = request.IncludedSubVoiceIds?
            .OrderBy(id => id)
            .ToArray() ?? [];
        TreatWarningsAsErrors = request.TreatWarningsAsErrors;
        CollectDebugDiagnostics = request.CollectDebugDiagnostics;
    }

    public CompilationPurpose Purpose { get; }
    public long StartTick { get; }
    public long? RequestedEndTick { get; }
    public long EndTick { get; }
    public CompilationEndTickSource EndTickSource { get; }
    public bool IncludesAllTracks { get; }
    public bool IncludesAllSubVoices { get; }
    public bool TreatWarningsAsErrors { get; }
    public bool CollectDebugDiagnostics { get; }
    public bool IsFullProject => Purpose == CompilationPurpose.FullProject && IncludesAllTracks;
    public bool IsPlayback => Purpose == CompilationPurpose.Playback;
    public bool IsPreview => Purpose is CompilationPurpose.SegmentPreview
        or CompilationPurpose.EventInstrumentPreview;
    public bool IsMidiExportPreparation => Purpose == CompilationPurpose.MidiExport;
    public bool IsAudioRenderPreparation => Purpose is CompilationPurpose.AudioRender
        or CompilationPurpose.LogicalTrackAudioRender;
    public bool UsesProjectEndMarkerAsDefault => EndTickSource == CompilationEndTickSource.ProjectEndMarker;
    public ReadOnlySpan<MidoraId> IncludedTrackIds => _includedTrackIds;
    public ReadOnlySpan<MidoraId> IncludedSubVoiceIds => _includedSubVoiceIds;
}

public enum CanonicalEventRole : byte
{
    NoteOff = 0,
    Reset = 1,
    RangeRestore = 2,
    InitialState = 3,
    Bank = 4,
    Program = 5,
    Parameter = 6,
    ControlChange = 7,
    PitchBend = 8,
    LogicalParameter = 9,
    NoteOn = 10,
    DirectMidi = 11,
    RootBoundaryCleanup = 12
}

public readonly record struct CanonicalMidiEvent(
    long Tick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    MidiMessage Message,
    CanonicalEventRole Role,
    long StableOrder,
    long SemanticTargetKey,
    long SemanticGroup,
    SourceReference Source,
    MidoraId ExportTrackId = default,
    int SmfTrackOrder = int.MaxValue,
    long SmfEventOrder = long.MaxValue);

public sealed class CanonicalMidiEventPage
{
    public const int MaximumRecordCount = 16_384;
    private readonly CanonicalMidiEvent[] _events;

    public CanonicalMidiEventPage(CanonicalMidiEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Length is <= 0 or > MaximumRecordCount)
            throw new ArgumentOutOfRangeException(nameof(events));
        _events = events;
        StartTick = events[0].Tick;
        EndTick = events[^1].Tick == long.MaxValue ? long.MaxValue : events[^1].Tick + 1;
    }

    public long StartTick { get; }
    public long EndTick { get; }
    public ReadOnlyMemory<CanonicalMidiEvent> Events => _events;
    public IReadOnlyList<CanonicalMidiEvent> Items => _events;
}

public interface ICanonicalMidiEventPageSource
{
    long EventCount { get; }
    long NoteOnEventCount { get; }
    string ContentFingerprint { get; }

    IEnumerable<CanonicalMidiEventPage> QueryPages(
        long startTick,
        long endTick,
        bool includeStateAtStart,
        CancellationToken cancellationToken = default);
}

/// <summary>An immutable, already ordered Logical result; queries never reinterpret source music.</summary>
public interface ILogicalCanonicalEventSource : IRetainedStorageSource
{
    long EventCount { get; }
    long NoteOnEventCount { get; }
    IEnumerable<CanonicalMidiEvent> Enumerate(
        long startTick, long endTick, bool includeEnd,
        CancellationToken cancellationToken = default);
    IEnumerable<CanonicalMidiEvent> EnumerateUnit(
        byte zeroBasedPort, byte zeroBasedChannel, long startTick, long endTick, bool includeEnd,
        CancellationToken cancellationToken = default) =>
        Enumerate(startTick, endTick, includeEnd, cancellationToken)
            .Where(value => value.ZeroBasedPort == zeroBasedPort && value.ZeroBasedChannel == zeroBasedChannel);
}

public readonly record struct CanonicalMidiRenderEvent(
    long Tick,
    byte ZeroBasedPort,
    MidiMessage Message,
    MidoraId TrackId,
    MidoraId MonitoringSourceId,
    CanonicalEventRole Role = CanonicalEventRole.DirectMidi,
    long StableOrder = long.MaxValue,
    int SmfTrackOrder = int.MaxValue,
    long SmfEventOrder = long.MaxValue,
    MidoraId StableObjectId = default,
    MidiChannelModeSystemExclusive? ChannelModeSystemExclusive = null);

public sealed class CanonicalMidiRenderEventPage
{
    public const int MaximumRecordCount = 16_384;
    private readonly CanonicalMidiRenderEvent[] _events;

    public CanonicalMidiRenderEventPage(CanonicalMidiRenderEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Length is <= 0 or > MaximumRecordCount)
            throw new ArgumentOutOfRangeException(nameof(events));
        _events = events;
    }

    public IReadOnlyList<CanonicalMidiRenderEvent> Items => _events;
}

public interface ICanonicalMidiRenderPageSource
{
    IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPages(
        long startTick,
        long endTick,
        bool includeStateAtStart,
        CancellationToken cancellationToken = default);
}

public interface ICanonicalDemandFilteredMidiRenderPageSource
{
    IEnumerable<CanonicalMidiRenderEventPage> QueryRenderPages(
        long startTick,
        long endTick,
        bool includeStateAtStart,
        IReadOnlySet<MidoraId> demandedMonitoringSourceIds,
        CancellationToken cancellationToken = default);
}

public sealed class CanonicalOpaqueMidiEventPage
{
    public const int MaximumRecordCount = 16_384;
    private readonly CanonicalOpaqueMidiEvent[] _events;

    public CanonicalOpaqueMidiEventPage(CanonicalOpaqueMidiEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Length is <= 0 or > MaximumRecordCount)
            throw new ArgumentOutOfRangeException(nameof(events));
        _events = events;
    }

    public ReadOnlyMemory<CanonicalOpaqueMidiEvent> Events => _events;
    public IReadOnlyList<CanonicalOpaqueMidiEvent> Items => _events;
}

/// <summary>A channel event in a canonical SMF track projection.</summary>
/// <param name="ExportTrackId">Required stable identity of the owning SMF track.</param>
/// <param name="SourceObjectId">
/// Direct source object identity, or default for generated events without a
/// direct source (for example Root lifecycle/default state). Not a new Project ID.
/// </param>
public readonly record struct CanonicalSmfTrackChannelEvent(
    MidoraId ExportTrackId,
    long Tick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    MidiMessage Message,
    CanonicalEventRole Role,
    long EventOrder,
    MidoraId SourceObjectId);

public sealed class CanonicalSmfTrackChannelEventPage
{
    public const int MaximumRecordCount = 16_384;
    private readonly CanonicalSmfTrackChannelEvent[] _events;

    public CanonicalSmfTrackChannelEventPage(CanonicalSmfTrackChannelEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Length is <= 0 or > MaximumRecordCount)
            throw new ArgumentOutOfRangeException(nameof(events));
        _events = events;
    }

    public IReadOnlyList<CanonicalSmfTrackChannelEvent> Items => _events;
}

public interface ICanonicalSmfTrackPageSource
{
    IEnumerable<CanonicalSmfTrackChannelEventPage> QueryTrackChannelEventPages(
        MidoraId exportTrackId,
        long startTick,
        long endTick,
        CancellationToken cancellationToken = default);

    IEnumerable<CanonicalOpaqueMidiEventPage> QueryTrackOpaqueEventPages(
        MidoraId exportTrackId,
        long startTick,
        long endTick,
        CancellationToken cancellationToken = default);
}

public readonly record struct CanonicalMidiPresetReference(
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    byte Bank,
    byte Program);

public sealed record CanonicalPureMidiAudioFragmentDescriptor(
    MidoraId MidiChannelRootId,
    MidoraId GroupId,
    long StartTick,
    long EndTick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    MidiChannelMode ChannelMode,
    string SemanticFingerprint,
    IReadOnlyList<MidoraId> ContributingTrackIds);

public interface ICanonicalPureMidiAudioMetadataSource
{
    IReadOnlyList<CanonicalPureMidiAudioFragmentDescriptor> PureMidiAudioFragments { get; }
    IReadOnlyList<CanonicalMidiPresetReference> PureMidiPresetReferences { get; }
}

public readonly record struct ChannelUnitAllocation(
    MidoraId TrackId,
    MidoraId SegmentId,
    MidoraId EventInstrumentId,
    MidoraId InstanceId,
    MidoraId InstanceGroupId,
    MidoraId SubVoiceId,
    long StartTick,
    long EndTick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    MidoraId MidiChannelRootId = default,
    MidoraId PureMidiTrackId = default,
    MidiChannelMode ChannelMode = MidiChannelMode.Melodic,
    MidoraId EventInstrumentUsageId = default);

public enum CanonicalSmfTrackKind
{
    PureMidiTrack,
    LogicalChannelUnit
}

public readonly record struct CanonicalSmfTrackDescriptor(
    MidoraId ExportTrackId,
    CanonicalSmfTrackKind Kind,
    string Name,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    long EndTick,
    MidoraId MidiChannelRootId = default,
    MidoraId SourceTrackId = default,
    MidiChannelMode ChannelMode = MidiChannelMode.Melodic,
    string MidiChannelRootName = "",
    int MidiChannelRootOrder = -1,
    int SourceTrackOrder = -1,
    MidiChannelRootRoutingMode RoutingMode = MidiChannelRootRoutingMode.Auto);

public readonly record struct CanonicalOpaqueMidiEvent(
    MidoraId ExportTrackId,
    long Tick,
    OpaqueMidiEventKind Kind,
    byte MetaType,
    ReadOnlyMemory<byte> Payload,
    long StableOrder,
    SourceReference Source,
    int SmfTrackOrder = int.MaxValue);

public readonly record struct CanonicalMidiChannelModeSystemExclusiveEvent(
    long Tick,
    byte ZeroBasedPort,
    byte ZeroBasedChannel,
    MidiChannelModeSystemExclusive Value,
    CanonicalEventRole Role,
    long StableOrder,
    SourceReference Source,
    MidoraId ExportTrackId,
    int SmfTrackOrder,
    long SmfEventOrder);

public readonly record struct CanonicalTempo(
    MidoraId SourceId,
    long Tick,
    decimal BeatsPerMinute,
    bool IsRangeRestore = false)
{
    public CanonicalTempo(long tick, decimal beatsPerMinute)
        : this(default, tick, beatsPerMinute)
    {
    }
}
public readonly record struct CanonicalTimeSignature(
    MidoraId SourceId,
    long Tick,
    int Numerator,
    int Denominator,
    bool IsRangeRestore = false);
public readonly record struct CanonicalKeySignature(
    MidoraId SourceId,
    long Tick,
    int SharpsFlats,
    bool IsMinor,
    bool IsRangeRestore = false);
public readonly record struct CanonicalMarker(MidoraId Id, long Tick, string Name);
public readonly record struct CanonicalEndMarker(MidoraId Id, long Tick);

public sealed partial class CanonicalConductor
{
    private readonly CanonicalTempo[] _tempos;
    private readonly CanonicalTimeSignature[] _timeSignatures;
    private readonly CanonicalTimeSignature[] _sourceTimeSignatureMap;
    private readonly CanonicalKeySignature[] _keySignatures;
    private readonly CanonicalMarker[] _markers;

    internal CanonicalConductor(
        CanonicalTempo[] tempos,
        CanonicalTimeSignature[] timeSignatures,
        CanonicalTimeSignature[] sourceTimeSignatureMap,
        CanonicalKeySignature[] keySignatures,
        CanonicalMarker[] markers,
        CanonicalEndMarker? endMarker)
    {
        _tempos = tempos;
        _timeSignatures = timeSignatures;
        _sourceTimeSignatureMap = sourceTimeSignatureMap;
        _keySignatures = keySignatures;
        _markers = markers;
        EndMarker = endMarker;
    }

    public ReadOnlySpan<CanonicalTempo> Tempos => _tempos;
    public ReadOnlySpan<CanonicalTimeSignature> TimeSignatures => _timeSignatures;
    public ReadOnlySpan<CanonicalTimeSignature> SourceTimeSignatureMap =>
        _sourceTimeSignatureMap;
    public ReadOnlySpan<CanonicalKeySignature> KeySignatures => _keySignatures;
    public ReadOnlySpan<CanonicalMarker> Markers => _markers;
    public CanonicalEndMarker? EndMarker { get; }
    public long? EndMarkerTick => EndMarker?.Tick;
}

public sealed partial class CanonicalCompiledResult
{
    private readonly CanonicalMidiEvent[] _events;
    private readonly ChannelUnitAllocation[] _allocations;
    private readonly CompilerDiagnosticList _diagnostics;
    private readonly CanonicalSmfTrackDescriptor[] _smfTracks;
    private readonly CanonicalOpaqueMidiEvent[] _opaqueMidiEvents;
    private readonly CanonicalMidiChannelModeSystemExclusiveEvent[]
        _channelModeSystemExclusiveEvents;
    private readonly ICanonicalMidiEventPageSource? _pagedEventSource;
    private readonly ICanonicalMidiRenderPageSource? _pagedRenderSource;
    private readonly ICanonicalSmfTrackPageSource? _pagedSmfTrackSource;
    private readonly ICanonicalPureMidiAudioMetadataSource? _pureMidiAudioMetadataSource;
    private readonly ILogicalCanonicalEventSource? _logicalEventSource;
    private readonly CanonicalConsumerMetadataCache _consumerCacheIdentity;

    internal CanonicalCompiledResult(
        int ticksPerQuarterNote,
        CompilationContextSummary context,
        CanonicalMidiEvent[] events,
        CanonicalConductor conductor,
        ChannelUnitAllocation[] allocations,
        IEnumerable<CompilerDiagnostic> diagnostics,
        bool isPartial,
        bool isConsumable,
        CompilationFailureStage? failureStage,
        long fingerprint,
        CompilationStatistics statistics,
        CanonicalSmfTrackDescriptor[]? smfTracks = null,
        CanonicalOpaqueMidiEvent[]? opaqueMidiEvents = null,
        ICanonicalMidiEventPageSource? pagedEventSource = null,
        CanonicalMidiChannelModeSystemExclusiveEvent[]? channelModeSystemExclusiveEvents = null,
        ILogicalCanonicalEventSource? logicalEventSource = null,
        object? consumerCacheIdentity = null)
    {
        TicksPerQuarterNote = ticksPerQuarterNote;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        StartTick = context.StartTick;
        EndTick = context.EndTick;
        _events = events;
        Conductor = conductor;
        _allocations = allocations;
        _diagnostics = CompilerDiagnosticList.FromFrozen(diagnostics);
        Purpose = context.Purpose;
        IsPartial = isPartial;
        IsConsumable = isConsumable;
        FailureStage = failureStage;
        Fingerprint = fingerprint;
        Statistics = statistics;
        _smfTracks = smfTracks ?? [];
        _opaqueMidiEvents = opaqueMidiEvents ?? [];
        _channelModeSystemExclusiveEvents = channelModeSystemExclusiveEvents ?? [];
        _pagedEventSource = pagedEventSource;
        _pagedRenderSource = pagedEventSource as ICanonicalMidiRenderPageSource;
        _pagedSmfTrackSource = pagedEventSource as ICanonicalSmfTrackPageSource;
        _pureMidiAudioMetadataSource = pagedEventSource as ICanonicalPureMidiAudioMetadataSource;
        _logicalEventSource = logicalEventSource;
        _consumerCacheIdentity = consumerCacheIdentity as CanonicalConsumerMetadataCache ?? new();
    }

    public int TicksPerQuarterNote { get; }
    public CompilationContextSummary Context { get; }
    public long StartTick { get; }
    public long EndTick { get; }
    public CompilationPurpose Purpose { get; }
    public bool IsPartial { get; }
    public bool IsConsumable { get; }
    public CompilationFailureStage? FailureStage { get; }
    public long Fingerprint { get; }
    public CompilationStatistics Statistics { get; }
    public CanonicalConductor Conductor { get; }
    public ReadOnlySpan<CanonicalMidiEvent> Events => _events;
    public bool HasPagedEvents => HasPagedPureMidiEvents || HasPagedLogicalEvents;
    public bool HasPagedPureMidiEvents => _pagedEventSource is not null;
    public bool HasPagedLogicalEvents => _logicalEventSource is not null;
    /// <summary>Identity shared only by semantically identical views of this frozen result.</summary>
    public object ConsumerCacheIdentity => _consumerCacheIdentity;
    public long TotalEventCount => checked(_events.LongLength + (_pagedEventSource?.EventCount ?? 0)
        + (_logicalEventSource?.EventCount ?? 0));
    public long TotalNoteOnEventCount => checked(
        CountNoteOns(_events) + (_pagedEventSource?.NoteOnEventCount ?? 0)
        + (_logicalEventSource?.NoteOnEventCount ?? 0));
    public ReadOnlySpan<CanonicalTempo> Tempos => Conductor.Tempos;
    public ReadOnlySpan<ChannelUnitAllocation> Allocations => _allocations;
    public ICompilerDiagnosticSequence Diagnostics => _diagnostics;
    public ReadOnlySpan<CanonicalSmfTrackDescriptor> SmfTracks => _smfTracks;
    public ReadOnlySpan<CanonicalOpaqueMidiEvent> OpaqueMidiEvents => _opaqueMidiEvents;
    public ReadOnlySpan<CanonicalMidiChannelModeSystemExclusiveEvent>
        ChannelModeSystemExclusiveEvents => _channelModeSystemExclusiveEvents;
    public IReadOnlyList<CanonicalPureMidiAudioFragmentDescriptor> PureMidiAudioFragments =>
        _pureMidiAudioMetadataSource?.PureMidiAudioFragments ?? [];
    public IReadOnlyList<CanonicalMidiPresetReference> PureMidiPresetReferences =>
        _pureMidiAudioMetadataSource?.PureMidiPresetReferences ?? [];

    public IEnumerable<CanonicalMidiEventPage> QueryEventPages(
        long startTick,
        long endTick,
        bool includeStateAtStart = false,
        CancellationToken cancellationToken = default)
    {
        if (startTick < StartTick || endTick > EndTick || endTick <= startTick)
            throw new ArgumentOutOfRangeException(nameof(startTick));

        IEnumerable<CanonicalMidiEvent> inMemory = EnumerateResidentAndLogicalEvents(
            startTick, endTick, endTick == EndTick, cancellationToken);
        IEnumerable<CanonicalMidiEvent> paged = _pagedEventSource is null
            ? []
            : _pagedEventSource.QueryPages(
                    startTick,
                    endTick,
                    includeStateAtStart,
                    cancellationToken)
                .SelectMany(value => value.Items);
        using IEnumerator<CanonicalMidiEvent> left = inMemory.GetEnumerator();
        using IEnumerator<CanonicalMidiEvent> right = paged.GetEnumerator();
        bool hasLeft = left.MoveNext();
        bool hasRight = right.MoveNext();
        List<CanonicalMidiEvent> page = new(CanonicalMidiEventPage.MaximumRecordCount);
        while (hasLeft || hasRight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool takeLeft = !hasRight || hasLeft && Compare(left.Current, right.Current) <= 0;
            page.Add(takeLeft ? left.Current : right.Current);
            if (takeLeft) hasLeft = left.MoveNext();
            else hasRight = right.MoveNext();
            if (page.Count == CanonicalMidiEventPage.MaximumRecordCount)
            {
                yield return new(page.ToArray());
                page.Clear();
            }
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    public IEnumerable<CanonicalMidiEventPage> QueryPagedEventPages(
        long startTick,
        long endTick,
        bool includeStateAtStart = false,
        CancellationToken cancellationToken = default)
    {
        if (startTick < StartTick || endTick > EndTick || endTick <= startTick)
            throw new ArgumentOutOfRangeException(nameof(startTick));
        return Page(Merge(
            _logicalEventSource?.Enumerate(startTick, endTick, endTick == EndTick, cancellationToken) ?? [],
            _pagedEventSource?.QueryPages(startTick, endTick, includeStateAtStart, cancellationToken)
                .SelectMany(page => page.Items) ?? [], Compare), cancellationToken);
    }

    public IEnumerable<CanonicalMidiRenderEventPage> QueryMidiRenderEventPages(
        long startTick,
        long endTick,
        bool includeStateAtStart = false,
        CancellationToken cancellationToken = default)
    {
        if (startTick < StartTick || endTick > EndTick || endTick <= startTick)
            throw new ArgumentOutOfRangeException(nameof(startTick));
        return MergePagedRenderEvents(startTick, endTick, includeStateAtStart, null, cancellationToken);
    }

    public IEnumerable<CanonicalMidiRenderEventPage> QueryMidiRenderEventPages(
        long startTick,
        long endTick,
        bool includeStateAtStart,
        IReadOnlySet<MidoraId> demandedMonitoringSourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(demandedMonitoringSourceIds);
        if (startTick < StartTick || endTick > EndTick || endTick <= startTick)
            throw new ArgumentOutOfRangeException(nameof(startTick));
        return MergePagedRenderEvents(startTick, endTick, includeStateAtStart,
            demandedMonitoringSourceIds, cancellationToken);
    }

    public IEnumerable<CanonicalSmfTrackChannelEventPage> QuerySmfTrackChannelEventPages(
        MidoraId exportTrackId,
        CancellationToken cancellationToken = default)
    {
        if (exportTrackId == default) throw new ArgumentOutOfRangeException(nameof(exportTrackId));
        // Pure SMF tracks never own Logical events. Do not scan the complete Logical
        // store once for every sibling Pure track in a mixed-project export.
        bool pureTrack = Array.Exists(_smfTracks, descriptor => descriptor.ExportTrackId == exportTrackId
            && descriptor.Kind == CanonicalSmfTrackKind.PureMidiTrack);
        IEnumerable<CanonicalMidiEvent> resident = pureTrack ? _events
            : EnumerateResidentAndLogicalEvents(cancellationToken);
        IEnumerable<CanonicalSmfTrackChannelEvent> inMemory = resident
            .Where(value => value.ExportTrackId == exportTrackId)
            .Select(ToSmfTrackEvent);
        IEnumerable<CanonicalSmfTrackChannelEvent> paged = _pagedSmfTrackSource is null
            ? []
            : _pagedSmfTrackSource.QueryTrackChannelEventPages(
                    exportTrackId,
                    StartTick,
                    EndTick,
                    cancellationToken)
                .SelectMany(value => value.Items);
        return PageSmf(Merge(inMemory, paged, CompareSmf), cancellationToken);
    }

    public IEnumerable<CanonicalOpaqueMidiEventPage> QuerySmfTrackOpaqueEventPages(
        MidoraId exportTrackId,
        CancellationToken cancellationToken = default)
    {
        if (exportTrackId == default) throw new ArgumentOutOfRangeException(nameof(exportTrackId));
        IEnumerable<CanonicalOpaqueMidiEvent> inMemory = _opaqueMidiEvents
            .Where(value => value.ExportTrackId == exportTrackId);
        IEnumerable<CanonicalOpaqueMidiEvent> paged = _pagedSmfTrackSource is null
            ? []
            : _pagedSmfTrackSource.QueryTrackOpaqueEventPages(
                    exportTrackId,
                    StartTick,
                    EndTick,
                    cancellationToken)
                .SelectMany(value => value.Items);
        return PageOpaque(Merge(inMemory, paged, CompareOpaque), cancellationToken);
    }

    internal CanonicalCompiledResult CreatePlaybackView()
    {
        if (!Context.IsFullProject || Context.StartTick != 0 || !IsConsumable || IsPartial)
        {
            throw new InvalidOperationException(
                "Only a complete consumable full-Project result can be reused as the default playback view.");
        }

        CompilationRequest request = new()
        {
            Purpose = CompilationPurpose.Playback,
            StartTick = Context.StartTick,
            TreatWarningsAsErrors = Context.TreatWarningsAsErrors,
            CollectDebugDiagnostics = Context.CollectDebugDiagnostics
        };
        return new CanonicalCompiledResult(
            TicksPerQuarterNote,
            new CompilationContextSummary(request, EndTick, Context.EndTickSource),
            _events,
            Conductor,
            _allocations,
            _diagnostics,
            IsPartial,
            IsConsumable,
            FailureStage,
            Fingerprint,
            Statistics,
            _smfTracks,
            _opaqueMidiEvents,
            _pagedEventSource,
            _channelModeSystemExclusiveEvents,
            _logicalEventSource,
            _consumerCacheIdentity);
    }

    private static long CountNoteOns(ReadOnlySpan<CanonicalMidiEvent> events)
    {
        long result = 0;
        foreach (CanonicalMidiEvent value in events)
            if (value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 != 0)
                result++;
        return result;
    }

    private static int Compare(CanonicalMidiEvent x, CanonicalMidiEvent y)
    {
        int value = x.Tick.CompareTo(y.Tick);
        if (value != 0) return value;
        value = x.Role.CompareTo(y.Role);
        if (value != 0) return value;
        value = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort);
        if (value != 0) return value;
        value = x.ZeroBasedChannel.CompareTo(y.ZeroBasedChannel);
        if (value != 0) return value;
        value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
        if (value != 0) return value;
        if (x.Role == CanonicalEventRole.DirectMidi
            && y.Role == CanonicalEventRole.DirectMidi)
        {
            value = x.SmfEventOrder.CompareTo(y.SmfEventOrder);
            if (value != 0) return value;
            value = CanonicalMidiOrdering.DirectEndpointOrder(x.Message).CompareTo(
                CanonicalMidiOrdering.DirectEndpointOrder(y.Message));
            if (value != 0) return value;
        }
        value = x.StableOrder.CompareTo(y.StableOrder);
        if (value != 0) return value;
        value = x.Source.TrackId.CompareTo(y.Source.TrackId);
        if (value != 0) return value;
        value = x.Source.SegmentId.CompareTo(y.Source.SegmentId);
        if (value != 0) return value;
        value = x.Source.LogicalNoteId.CompareTo(y.Source.LogicalNoteId);
        if (value != 0) return value;
        value = x.Source.EventInstrumentId.CompareTo(y.Source.EventInstrumentId);
        if (value != 0) return value;
        value = x.Source.SubVoiceId.CompareTo(y.Source.SubVoiceId);
        if (value != 0) return value;
        value = x.Source.SourceEventId.CompareTo(y.Source.SourceEventId);
        if (value != 0) return value;
        value = x.Source.LogicalParameterId.CompareTo(y.Source.LogicalParameterId);
        if (value != 0) return value;
        value = x.Source.LogicalParameterMappingId.CompareTo(y.Source.LogicalParameterMappingId);
        if (value != 0) return value;
        value = x.Source.MappingStepId.CompareTo(y.Source.MappingStepId);
        if (value != 0) return value;
        value = x.Source.MappingFunctionId.CompareTo(y.Source.MappingFunctionId);
        if (value != 0) return value;
        value = x.Source.ValueCurveId.CompareTo(y.Source.ValueCurveId);
        if (value != 0) return value;
        value = x.Source.EnvelopeId.CompareTo(y.Source.EnvelopeId);
        if (value != 0) return value;
        value = x.Source.MidiChannelRootId.CompareTo(y.Source.MidiChannelRootId);
        if (value != 0) return value;
        value = x.Source.PureMidiTrackId.CompareTo(y.Source.PureMidiTrackId);
        if (value != 0) return value;
        value = x.Source.MidiSegmentId.CompareTo(y.Source.MidiSegmentId);
        if (value != 0) return value;
        value = x.Source.DirectMidiObjectId.CompareTo(y.Source.DirectMidiObjectId);
        if (value != 0) return value;
        value = x.ExportTrackId.CompareTo(y.ExportTrackId);
        if (value != 0) return value;
        value = x.SemanticTargetKey.CompareTo(y.SemanticTargetKey);
        if (value != 0) return value;
        value = x.SemanticGroup.CompareTo(y.SemanticGroup);
        if (value != 0) return value;
        value = x.Message.PackedValue.CompareTo(y.Message.PackedValue);
        if (value != 0) return value;
        value = x.Source.Tick.CompareTo(y.Source.Tick);
        if (value != 0) return value;
        return x.Source.Origin.CompareTo(y.Source.Origin);
    }

    private static int CompareOpaque(CanonicalOpaqueMidiEvent x, CanonicalOpaqueMidiEvent y)
    {
        int value = x.Tick.CompareTo(y.Tick);
        if (value != 0) return value;
        value = x.StableOrder.CompareTo(y.StableOrder);
        if (value != 0) return value;
        value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
        if (value != 0) return value;
        return x.Source.DirectMidiObjectId.CompareTo(y.Source.DirectMidiObjectId);
    }

    private static CanonicalSmfTrackChannelEvent ToSmfTrackEvent(CanonicalMidiEvent value) => new(
        value.ExportTrackId,
        value.Tick,
        value.ZeroBasedPort,
        value.ZeroBasedChannel,
        value.Message,
        value.Role,
        value.SmfEventOrder,
        value.Source.DirectMidiObjectId);

    private static int CompareSmf(
        CanonicalSmfTrackChannelEvent x,
        CanonicalSmfTrackChannelEvent y)
    {
        int value = x.Tick.CompareTo(y.Tick);
        if (value != 0) return value;
        value = x.Role.CompareTo(y.Role);
        if (value != 0) return value;
        value = x.EventOrder.CompareTo(y.EventOrder);
        if (value != 0) return value;
        if (x.Role == CanonicalEventRole.DirectMidi
            && y.Role == CanonicalEventRole.DirectMidi)
        {
            value = CanonicalMidiOrdering.DirectEndpointOrder(x.Message).CompareTo(
                CanonicalMidiOrdering.DirectEndpointOrder(y.Message));
            if (value != 0) return value;
        }
        value = SmfKindOrder(x.Role).CompareTo(SmfKindOrder(y.Role));
        if (value != 0) return value;
        return x.SourceObjectId.CompareTo(y.SourceObjectId);

        static int SmfKindOrder(CanonicalEventRole role) => role switch
        {
            CanonicalEventRole.Reset => 0,
            CanonicalEventRole.RootBoundaryCleanup => 2,
            _ => 1
        };
    }

    private static IEnumerable<T> Merge<T>(
        IEnumerable<T> leftSource,
        IEnumerable<T> rightSource,
        Func<T, T, int> compare)
    {
        using IEnumerator<T> left = leftSource.GetEnumerator();
        using IEnumerator<T> right = rightSource.GetEnumerator();
        bool hasLeft = left.MoveNext();
        bool hasRight = right.MoveNext();
        while (hasLeft || hasRight)
        {
            bool takeLeft = !hasRight || hasLeft && compare(left.Current, right.Current) <= 0;
            yield return takeLeft ? left.Current : right.Current;
            if (takeLeft) hasLeft = left.MoveNext();
            else hasRight = right.MoveNext();
        }
    }

    private static IEnumerable<CanonicalMidiEventPage> Page(
        IEnumerable<CanonicalMidiEvent> source,
        CancellationToken cancellationToken)
    {
        List<CanonicalMidiEvent> page = new(CanonicalMidiEventPage.MaximumRecordCount);
        foreach (CanonicalMidiEvent value in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Add(value);
            if (page.Count != CanonicalMidiEventPage.MaximumRecordCount) continue;
            yield return new(page.ToArray());
            page.Clear();
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    private static IEnumerable<CanonicalSmfTrackChannelEventPage> PageSmf(
        IEnumerable<CanonicalSmfTrackChannelEvent> source,
        CancellationToken cancellationToken)
    {
        List<CanonicalSmfTrackChannelEvent> page = new(
            CanonicalSmfTrackChannelEventPage.MaximumRecordCount);
        foreach (CanonicalSmfTrackChannelEvent value in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Add(value);
            if (page.Count != CanonicalSmfTrackChannelEventPage.MaximumRecordCount) continue;
            yield return new(page.ToArray());
            page.Clear();
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    private static IEnumerable<CanonicalOpaqueMidiEventPage> PageOpaque(
        IEnumerable<CanonicalOpaqueMidiEvent> source,
        CancellationToken cancellationToken)
    {
        List<CanonicalOpaqueMidiEvent> page = new(CanonicalOpaqueMidiEventPage.MaximumRecordCount);
        foreach (CanonicalOpaqueMidiEvent value in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Add(value);
            if (page.Count != CanonicalOpaqueMidiEventPage.MaximumRecordCount) continue;
            yield return new(page.ToArray());
            page.Clear();
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }
}

public readonly record struct CompilationStatistics(
    int SourceTrackCount,
    int ExpandedInstanceCount,
    long EventCount,
    int PeakChannelUnitCount)
{
    public int ExpandedSegmentCount { get; init; }
    public int ParticipatingEventInstrumentCount { get; init; }
    public int ParticipatingSubVoiceCount { get; init; }
    public int UsedPortCount { get; init; }
    public long NoteOnEventCount { get; init; }
    public ResourceShortageDetails? ResourceShortage { get; init; }
    public int ReservedMidiRootUnitCount { get; init; }
    public int AllocatedMidiRootUnitCount { get; init; }
    public int LogicalPeakChannelUnitCount { get; init; }
}

public sealed partial class ResourceShortageDetails
{
    private readonly MidoraId[] _trackIds;
    private readonly MidoraId[] _segmentIds;
    private readonly MidoraId[] _logicalNoteIds;
    private readonly MidoraId[] _eventInstrumentIds;
    private readonly MidoraId[] _subVoiceIds;

    internal ResourceShortageDetails(
        TickRange range,
        int requestedChannelUnitCount,
        int availableChannelUnitCount,
        IEnumerable<MidoraId> trackIds,
        IEnumerable<MidoraId> segmentIds,
        IEnumerable<MidoraId> logicalNoteIds,
        IEnumerable<MidoraId> eventInstrumentIds,
        IEnumerable<MidoraId> subVoiceIds)
    {
        Range = range;
        RequestedChannelUnitCount = requestedChannelUnitCount;
        AvailableChannelUnitCount = availableChannelUnitCount;
        _trackIds = FreezeIds(trackIds);
        _segmentIds = FreezeIds(segmentIds);
        _logicalNoteIds = FreezeIds(logicalNoteIds);
        _eventInstrumentIds = FreezeIds(eventInstrumentIds);
        _subVoiceIds = FreezeIds(subVoiceIds);
    }

    public TickRange Range { get; }
    public int RequestedChannelUnitCount { get; }
    public int AvailableChannelUnitCount { get; }
    public ReadOnlySpan<MidoraId> TrackIds => _trackIds;
    public ReadOnlySpan<MidoraId> SegmentIds => _segmentIds;
    public ReadOnlySpan<MidoraId> LogicalNoteIds => _logicalNoteIds;
    public ReadOnlySpan<MidoraId> EventInstrumentIds => _eventInstrumentIds;
    public ReadOnlySpan<MidoraId> SubVoiceIds => _subVoiceIds;

    private static MidoraId[] FreezeIds(IEnumerable<MidoraId> ids) => ids
        .Distinct()
        .OrderBy(id => id)
        .ToArray();
}

public readonly record struct CompilerRunTelemetry(
    int RecompiledTrackCount,
    int ReusedTrackCount)
{
    public int RecompiledSegmentCount { get; init; }
    public int ReusedSegmentCount { get; init; }
    public int StateConvergenceCount { get; init; }
    public long? EarliestDirtyTick { get; init; }
}
