using System.Collections.ObjectModel;
using Midora.Compiler;
using Midora.Domain;
using Midora.OutputPlanning;

namespace Midora.MidiExport;

public sealed class MidiExportCompilationRequest
{
    public required MidoraProject Project { get; init; }
    public required MidiExportMode Mode { get; init; }
    public required MidiExportRoutingStrategy Routing { get; init; }
    public long StartTick { get; init; }
    public long? EndTick { get; init; }
    public IReadOnlySet<MidoraId>? SelectedTrackIds { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
}

public sealed record MidiExportTrackSnapshot(
    MidoraId TrackId,
    string StableSourceKey,
    int ProjectDisplayOrder,
    string DisplayName,
    bool Participates,
    string? ExclusionReason);

public sealed class MidiExportCompilationResult
{
    internal MidiExportCompilationResult(
        MidiExportMode mode,
        MidiExportRoutingStrategy routing,
        string conductorTrackName,
        CanonicalCompiledResult compiledResult,
        MidiExportLogicalTrackLayout[] layouts,
        MidiExportTrackSnapshot[] tracks,
        byte[] usedZeroBasedPorts)
    {
        Mode = mode;
        Routing = routing;
        ConductorTrackName = conductorTrackName;
        CompiledResult = compiledResult;
        Layouts = Array.AsReadOnly(layouts);
        Tracks = Array.AsReadOnly(tracks);
        UsedZeroBasedPorts = Array.AsReadOnly(usedZeroBasedPorts);
    }

    public bool Succeeded => CompiledResult.IsConsumable && !CompiledResult.IsPartial;
    public MidiExportMode Mode { get; }
    public MidiExportRoutingStrategy Routing { get; }
    public string ConductorTrackName { get; }
    public CanonicalCompiledResult CompiledResult { get; }
    public ReadOnlyCollection<MidiExportLogicalTrackLayout> Layouts { get; }
    public ReadOnlyCollection<MidiExportTrackSnapshot> Tracks { get; }
    public ReadOnlyCollection<byte> UsedZeroBasedPorts { get; }
    public ICompilerDiagnosticSequence Diagnostics => CompiledResult.Diagnostics;
}

public sealed class MidiExportCompilationCoordinator
{
    private readonly MidoraCompiler _compiler;

    public MidiExportCompilationCoordinator(MidoraCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public MidiExportCompilationResult Compile(MidiExportCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        if (request.StartTick < 0 || request.EndTick < request.StartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The MIDI export range is invalid.");
        }

        HashSet<MidoraId>? selected = request.SelectedTrackIds?.ToHashSet();
        LogicalTrack[] logicalTracks = request.Project.LogicalTracksInArrangementOrder().ToArray();
        if (request.Mode == MidiExportMode.PerLogicalTrack
            && selected is not null
            && request.Project.PureMidiTracks.Any(track => selected.Contains(track.Id)))
        {
            throw new ArgumentException(
                "Per Logical Track export cannot include Pure MIDI Track IDs.",
                nameof(request));
        }
        HashSet<MidoraId>? selectedForCompilation = request.Mode == MidiExportMode.PerLogicalTrack
            ? selected ?? logicalTracks.Select(track => track.Id).ToHashSet()
            : selected;
        CanonicalCompiledResult compiled = _compiler.CompileFull(
            request.Project,
            new CompilationRequest
            {
                Purpose = CompilationPurpose.MidiExport,
                StartTick = request.StartTick,
                EndTick = request.EndTick,
                IncludedTrackIds = selectedForCompilation,
                TreatWarningsAsErrors = request.TreatWarningsAsErrors
            });

        HashSet<MidoraId> instrumentIds = request.Project.EventInstruments
            .Select(instrument => instrument.Id)
            .ToHashSet();
        List<MidiExportTrackSnapshot> trackSnapshots = [];
        List<MidiExportLogicalTrackLayout> layouts = [];
        Dictionary<MidoraId, HashSet<MidiExportChannelUnit>> unitsByTrack = [];
        HashSet<byte> portSet = [];
        foreach (ChannelUnitAllocation allocation in compiled.Allocations)
        {
            if (!unitsByTrack.TryGetValue(allocation.TrackId, out var units))
                unitsByTrack[allocation.TrackId] = units = [];
            units.Add(new(allocation.ZeroBasedPort, allocation.ZeroBasedChannel));
        }
        foreach (CanonicalMidiEvent value in compiled.EnumerateResidentAndLogicalEvents())
        {
            if (!unitsByTrack.TryGetValue(value.Source.TrackId, out var units))
                unitsByTrack[value.Source.TrackId] = units = [];
            units.Add(new(value.ZeroBasedPort, value.ZeroBasedChannel));
            portSet.Add(value.ZeroBasedPort);
        }
        for (int index = 0; index < logicalTracks.Length; index++)
        {
            LogicalTrack track = logicalTracks[index];
            bool selectedForTask = selected is null || selected.Contains(track.Id);
            MidoraId? instrumentId = request.Project.ResolveEventInstrumentDefinitionId(track);
            bool bound = instrumentId.HasValue && instrumentIds.Contains(instrumentId.Value);
            bool participates = selectedForTask && bound;
            int projectDisplayOrder = index + 1;
            string displayName = InitialReleaseOutputNaming.GetLogicalTrackDisplayName(
                track.Name,
                projectDisplayOrder);
            string? exclusion = participates
                ? null
                : !selectedForTask
                    ? "Not selected"
                    : "No valid Event Instrument binding";
            trackSnapshots.Add(new(
                track.Id,
                track.Id.ToString(),
                projectDisplayOrder,
                displayName,
                participates,
                exclusion));
            if (!participates)
            {
                continue;
            }

            HashSet<MidiExportChannelUnit> unitSet = unitsByTrack.GetValueOrDefault(track.Id) ?? [];
            MidiExportChannelUnit[] units = unitSet
                .OrderBy(unit => unit.ZeroBasedPort)
                .ThenBy(unit => unit.ZeroBasedChannel)
                .ToArray();
            Dictionary<MidiExportChannelUnit, string> names = units.ToDictionary(
                unit => unit,
                unit => InitialReleaseOutputNaming.GetEventTrackName(
                    unit.ZeroBasedPort + 1,
                    unit.ZeroBasedChannel + 1));
            layouts.Add(new(track.Id, names));
        }

        if (request.Mode != MidiExportMode.PerLogicalTrack)
        {
            PureMidiTrack[] pureTracks = request.Project.PureMidiTracksInArrangementOrder().ToArray();
            for (int index = 0; index < pureTracks.Length; index++)
            {
                PureMidiTrack track = pureTracks[index];
                bool participates = selected is null || selected.Contains(track.Id);
                trackSnapshots.Add(new(
                    track.Id,
                    track.Id.ToString(),
                    logicalTracks.Length + index + 1,
                    string.IsNullOrWhiteSpace(track.Name) ? $"MIDI Track {index + 1}" : track.Name,
                    participates,
                    participates ? null : "Not selected"));
            }
        }

        foreach (CanonicalSmfTrackDescriptor value in compiled.SmfTracks)
            if (value.Kind == CanonicalSmfTrackKind.PureMidiTrack) portSet.Add(value.ZeroBasedPort);
        byte[] usedPorts = portSet.Order().ToArray();
        return new(
            request.Mode,
            request.Routing,
            InitialReleaseOutputNaming.GetConductorTrackName(
                request.Project.Metadata.ProjectName),
            compiled,
            layouts.ToArray(),
            trackSnapshots.ToArray(),
            usedPorts);
    }
}
