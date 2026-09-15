namespace Midora.Domain;

public sealed class LogicalNote
{
    public LogicalNote(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal LogicalNote(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    internal LogicalNote(LogicalNoteSnapshotValue value)
    {
        Id = value.Id;
        _startTick = value.StartTick;
        _lengthTicks = value.LengthTicks;
        _note = value.Note;
        _velocity = value.Velocity;
    }

    private Action<LogicalNote>? _changeSink;
    private long _startTick;
    private long _lengthTicks;
    private int _note = 60;
    private int _velocity = 100;

    public MidoraId Id { get; init; }
    public long StartTick
    {
        get => _startTick;
        set => Set(ref _startTick, value);
    }
    public long LengthTicks
    {
        get => _lengthTicks;
        set => Set(ref _lengthTicks, value);
    }
    public int Note
    {
        get => _note;
        set => Set(ref _note, value);
    }
    public int Velocity
    {
        get => _velocity;
        set => Set(ref _velocity, value);
    }

    internal void SetChangeSink(Action<LogicalNote>? sink) => _changeSink = sink;

    internal void SetValues(
        long startTick,
        long lengthTicks,
        int note,
        int velocity)
    {
        if (_startTick == startTick
            && _lengthTicks == lengthTicks
            && _note == note
            && _velocity == velocity)
        {
            return;
        }

        _startTick = startTick;
        _lengthTicks = lengthTicks;
        _note = note;
        _velocity = velocity;
        _changeSink?.Invoke(this);
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _changeSink?.Invoke(this);
    }
}

public sealed class LogicalParameterLane
{
    public LogicalParameterLane(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal LogicalParameterLane(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public MidoraId ParameterId { get; set; }
    public CurvePointCollection Points { get; } = new();
}

public sealed class Segment
{
    public Segment(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal Segment(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public long ProjectStartTick { get; set; }
    public long LengthTicks { get; set; }
    public long ContentOffsetTick { get; set; }
    public LogicalNoteCollection Notes { get; } = new();
    public List<LogicalParameterLane> ParameterLanes { get; } = [];

    public TickRange ProjectRange => new(ProjectStartTick, checked(ProjectStartTick + LengthTicks));
    public long ContentEndTick => checked(ContentOffsetTick + LengthTicks);
}

public sealed class LogicalTrack
{
    public LogicalTrack(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
    }

    internal LogicalTrack(MidoraProject project, MidoraId preservedId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
    }

    public MidoraId Id { get; init; }
    public required string Name { get; set; }
    /// <summary>
    /// Stable identity of the shared Event Instrument execution state used by
    /// this Track. Null is permitted only for an empty, unbound Track shell.
    /// </summary>
    public MidoraId? EventInstrumentUsageId { get; set; }
    public string? LastBoundEventInstrumentName { get; set; }
    public MidoraColor? ColorOverride { get; set; }
    public List<Segment> Segments { get; internal set; } = [];
}

public enum StopCursorBehavior
{
    ReturnToPlaybackStart,
    StayAtStoppedTick
}

public sealed class GlobalEventScopeDefaults
{
    public static bool IsChannelWide(TemplateEventKind kind) => kind != TemplateEventKind.Note;
}

public enum AudioRenderMode
{
    WholeMix,
    PerLogicalTrack
}

public static class AudioRenderSettingsPolicy
{
    public const int MinimumSampleRate = 8_000;
    public const int MaximumSampleRate = 192_000;
    public const int DefaultSampleRate = 48_000;
    public const int MinimumSampleVoicesPerUnitStream = 1;
    public const int MaximumSampleVoicesPerUnitStreamLimit = 16_777_216;
    public const int DefaultSampleVoicesPerUnitStream = 500;

}

public sealed class MidoraProject : IDisposable
{
    public const int MinimumTicksPerQuarterNote = 1;
    public const int MaximumTicksPerQuarterNote = 32_767;

    private long _nextStableId;
    private readonly List<IDisposable> _runtimeResources = [];
    private int _disposeStarted;
    // A published detached mirror becomes only an ownership bridge for lazy
    // value-facade factories. It must never retain its former catalog graph or
    // allocate identities independently from the live Project.
    private MidoraProject? _publishedRuntimeOwner;

    public MidoraProject(int ticksPerQuarterNote)
        : this(ticksPerQuarterNote, TimeProvider.System.GetUtcNow())
    {
    }

    public MidoraProject(int ticksPerQuarterNote, DateTimeOffset createdAtUtc)
        : this(
            ticksPerQuarterNote,
            1,
            createInitialConductorState: true,
            createdAtUtc)
    {
    }

    internal MidoraProject(int ticksPerQuarterNote, long nextStableId)
        : this(
            ticksPerQuarterNote,
            nextStableId,
            createInitialConductorState: false,
            TimeProvider.System.GetUtcNow())
    {
    }

    internal MidoraProject(
        int ticksPerQuarterNote,
        long nextStableId,
        DateTimeOffset createdAtUtc)
        : this(
            ticksPerQuarterNote,
            nextStableId,
            createInitialConductorState: false,
            createdAtUtc)
    {
    }

    private MidoraProject(
        int ticksPerQuarterNote,
        long nextStableId,
        bool createInitialConductorState,
        DateTimeOffset createdAtUtc)
    {
        if (ticksPerQuarterNote is < MinimumTicksPerQuarterNote or > MaximumTicksPerQuarterNote)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        }
        if (nextStableId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextStableId));
        }
        TicksPerQuarterNote = ticksPerQuarterNote;
        _nextStableId = nextStableId;
        Metadata = new ProjectMetadata(createdAtUtc);
        Conductor = new ConductorTrack(this, createInitialConductorState);
    }

    public int TicksPerQuarterNote { get; }
    public long NextStableId => _publishedRuntimeOwner?.NextStableId ?? _nextStableId;
    public ProjectMetadata Metadata { get; }
    public ConductorTrack Conductor { get; internal set; }
    public MidiInitialState GlobalInitialState { get; internal set; } = new();
    public MidiInitialState GlobalResetDefaults { get; internal set; } = new();
    public GlobalEventScopeDefaults GlobalEventScopeDefaults { get; } = new();
    public List<EventInstrument> EventInstruments { get; internal set; } = [];
    public List<DamagedProjectObject> DamagedEventInstruments { get; internal set; } = [];
    public List<EventInstrumentUsage> EventInstrumentUsages { get; internal set; } = [];
    public List<DamagedProjectObject> DamagedEventInstrumentUsages { get; internal set; } = [];
    public List<LogicalTrack> Tracks { get; internal set; } = [];
    public List<DamagedProjectObject> DamagedLogicalTracks { get; internal set; } = [];
    public List<ArrangementTrackReference> ArrangementTracks { get; internal set; } = [];
    public List<MidiChannelRoot> MidiChannelRoots { get; internal set; } = [];
    public List<DamagedProjectObject> DamagedMidiChannelRoots { get; internal set; } = [];
    public List<PureMidiTrack> PureMidiTracks { get; internal set; } = [];
    public List<DamagedProjectObject> DamagedPureMidiTracks { get; internal set; } = [];
    public MidoraId AllocateStableId()
    {
        if (_publishedRuntimeOwner is { } owner) return owner.AllocateStableId();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (_nextStableId == long.MaxValue)
        {
            throw new InvalidOperationException("The Project stable ID counter is exhausted.");
        }
        MidoraId result = MidoraId.FromSequence(_nextStableId);
        _nextStableId++;
        return result;
    }

    public void RegisterRuntimeResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_publishedRuntimeOwner is { } owner) { owner.RegisterRuntimeResource(resource); return; }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        _runtimeResources.Add(resource);
    }

    internal void TransferRuntimeResourcesTo(MidoraProject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref target._disposeStarted) != 0, target);
        if (ReferenceEquals(this, target)) return;
        // Transfer leases, not the detached Project itself: retaining the mirror
        // would pin unrelated catalog and history roots until Project close.
        target._runtimeResources.AddRange(_runtimeResources);
        _runtimeResources.Clear();
    }

    internal void AdoptDetachedRuntimeOwner(MidoraProject target)
    {
        if (_publishedRuntimeOwner is not null)
            throw new InvalidOperationException("A detached Project already has a published owner.");
        while (target._publishedRuntimeOwner is { } parent) target = parent;
        if (ReferenceEquals(this, target)) throw new InvalidOperationException("A detached Project cannot adopt itself.");
        TransferRuntimeResourcesTo(target);
        _publishedRuntimeOwner = target;
    }

    internal void ReleaseDetachedCatalogReferences()
    {
        // Replace directory references; never Clear the old lists, which may
        // already be owned by the prepared publication slots.
        Tracks = []; PureMidiTracks = []; EventInstruments = [];
        EventInstrumentUsages = []; MidiChannelRoots = []; ArrangementTracks = [];
        Conductor = new ConductorTrack(this, createInitialState: false);
        GlobalInitialState = new(); GlobalResetDefaults = new();
        DamagedEventInstruments = []; DamagedEventInstrumentUsages = [];
        DamagedLogicalTracks = []; DamagedMidiChannelRoots = []; DamagedPureMidiTracks = [];
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        List<Exception>? failures = null;
        for (int index = _runtimeResources.Count - 1; index >= 0; index--)
        {
            try
            {
                _runtimeResources[index].Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        _runtimeResources.Clear();
        if (failures is not null) throw new AggregateException(failures);
    }

    internal void RestoreNextStableId(long nextStableId)
    {
        if (_publishedRuntimeOwner is not null)
            throw new InvalidOperationException("A published detached allocator cannot be rewound.");
        if (nextStableId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextStableId));
        }
        _nextStableId = nextStableId;
    }

    /// <summary>
    /// Advances the allocator high-water mark without ever returning IDs to
    /// the pool. Detached edit preparation uses preserved IDs and publishes
    /// the corresponding high-water mark only with its first successful root
    /// replacement.
    /// </summary>
    internal void AdvanceNextStableId(long minimumNextStableId)
    {
        if (_publishedRuntimeOwner is { } owner) { owner.AdvanceNextStableId(minimumNextStableId); return; }
        if (minimumNextStableId <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumNextStableId));
        if (minimumNextStableId > _nextStableId)
            _nextStableId = minimumNextStableId;
    }

    public void SetEndMarker(long? tick)
    {
        if (tick is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick));
        }
        if (!tick.HasValue)
        {
            Conductor.EndMarker = null;
        }
        else if (Conductor.EndMarker is null)
        {
            Conductor.EndMarker = new ProjectEndMarker(this, tick.Value);
        }
        else
        {
            Conductor.EndMarker.Tick = tick.Value;
        }
    }
}

public sealed record DamagedProjectObject(
    MidoraId Id,
    string NameSnapshot,
    string PackagePath,
    string Error,
    int OriginalIndex,
    MidoraId? ParentId = null,
    IReadOnlyList<MidoraId>? ChildIds = null);

public sealed class ProjectChangeSet
{
    public static ProjectChangeSet Everything { get; } = new() { AffectsEverything = true };

    public bool AffectsEverything { get; init; }
    public bool AffectsConductor { get; init; }
    public bool AffectsAudioPcmCacheGeneration { get; init; }
    public HashSet<MidoraId> TrackIds { get; } = [];
    public HashSet<MidoraId> EventInstrumentIds { get; } = [];
    public HashSet<MidoraId> EventInstrumentUsageIds { get; } = [];
    public HashSet<MidoraId> MidiChannelRootIds { get; } = [];
    public HashSet<MidoraId> PureMidiTrackIds { get; } = [];
    // Presentation-only source changes are propagated through document history
    // without marking canonical compilation or PCM-cache generation affected.
    public HashSet<MidoraId> PresentationTrackIds { get; } = [];
    public HashSet<MidoraId> PresentationEventInstrumentIds { get; } = [];
    // Optional exact source/range/page traces for timeline root swaps.  An
    // empty collection preserves the existing coarse invalidation contract.
    public List<ProjectTimelineOwnerChangeSet> TimelineOwnerChanges { get; } = [];
}
