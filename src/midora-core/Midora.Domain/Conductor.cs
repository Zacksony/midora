namespace Midora.Domain;

public sealed record TempoChange
{
    public TempoChange(MidoraProject project, long tick, decimal beatsPerMinute)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        BeatsPerMinute = beatsPerMinute;
    }

    internal TempoChange(MidoraId preservedId, long tick, decimal beatsPerMinute)
    {
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
        BeatsPerMinute = beatsPerMinute;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public decimal BeatsPerMinute { get; init; }
}

public sealed record TimeSignatureChange
{
    public TimeSignatureChange(MidoraProject project, long tick, int numerator, int denominator)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectTimeSignatureRules.ValidateCompatibility(
            project.TicksPerQuarterNote,
            denominator,
            nameof(denominator));
        Id = project.AllocateStableId();
        Tick = tick;
        Numerator = numerator;
        Denominator = denominator;
    }

    internal TimeSignatureChange(MidoraId preservedId, long tick, int numerator, int denominator)
    {
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
        Numerator = numerator;
        Denominator = denominator;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public int Numerator { get; init; }
    public int Denominator { get; init; }
}

public sealed record KeySignatureChange
{
    public KeySignatureChange(MidoraProject project, long tick, int sharpsFlats, bool isMinor)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        SharpsFlats = sharpsFlats;
        IsMinor = isMinor;
    }

    internal KeySignatureChange(MidoraId preservedId, long tick, int sharpsFlats, bool isMinor)
    {
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
        SharpsFlats = sharpsFlats;
        IsMinor = isMinor;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public int SharpsFlats { get; init; }
    public bool IsMinor { get; init; }
}

public sealed record ProjectMarker
{
    public ProjectMarker(MidoraProject project, long tick, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    internal ProjectMarker(MidoraId preservedId, long tick, string name)
    {
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public MidoraId Id { get; init; }
    public long Tick { get; init; }
    public string Name { get; init; }
}

public sealed class ProjectEndMarker
{
    public ProjectEndMarker(MidoraProject project, long tick)
    {
        ArgumentNullException.ThrowIfNull(project);
        Id = project.AllocateStableId();
        Tick = tick;
    }

    internal ProjectEndMarker(MidoraId preservedId, long tick)
    {
        if (preservedId == default) throw new ArgumentOutOfRangeException(nameof(preservedId));
        Id = preservedId;
        Tick = tick;
    }

    public MidoraId Id { get; init; }
    public long Tick { get; set; }
}

public sealed class ConductorTrack
{
    private ConductorTrack() { }
    internal ConductorTrack(MidoraProject project, bool createInitialState)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (createInitialState)
        {
            Tempos.Add(new TempoChange(project, 0, 120m));
            TimeSignatures.Add(new TimeSignatureChange(project, 0, 4, 4));
        }
    }

    public ConductorCollection<TempoChange> Tempos { get; internal set; } = [];
    public ConductorCollection<TimeSignatureChange> TimeSignatures { get; internal set; } = [];
    public ConductorCollection<KeySignatureChange> KeySignatures { get; internal set; } = [];
    public ConductorCollection<ProjectMarker> Markers { get; internal set; } = [];
    public ProjectEndMarker? EndMarker { get; set; }
    public long? EndMarkerTick => EndMarker?.Tick;

    public ConductorTrack CloneFrozen()
    {
        ConductorTrack result = new();
        result.Tempos.AdoptSnapshot(Tempos.CaptureQuerySnapshot());
        result.TimeSignatures.AdoptSnapshot(TimeSignatures.CaptureQuerySnapshot());
        result.KeySignatures.AdoptSnapshot(KeySignatures.CaptureQuerySnapshot());
        result.Markers.AdoptSnapshot(Markers.CaptureQuerySnapshot());
        result.EndMarker = EndMarker is { } marker ? new(marker.Id, marker.Tick) : null;
        return result;
    }
}
