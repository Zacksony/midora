using System.Collections.ObjectModel;
using Midora.Compiler;
using Midora.Domain;
using Midora.OutputPlanning;

namespace Midora.AudioRender;

public sealed class AudioRenderCompilationRequest
{
    public required MidoraProject Project { get; init; }
    public required AudioRenderMode Mode { get; init; }
    public long StartTick { get; init; }
    public long? EndTick { get; init; }
    public IReadOnlySet<MidoraId>? SelectedTrackIds { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
}

public sealed record AudioRenderTrackSnapshot(
    MidoraId TrackId,
    string StableSourceKey,
    int ProjectDisplayOrder,
    string DisplayName,
    bool Participates,
    string? ExclusionReason);

public sealed class AudioRenderCompilationItem
{
    internal AudioRenderCompilationItem(
        string sourceKey,
        AudioRenderTrackSnapshot? track,
        CanonicalCompiledResult compiledResult)
    {
        SourceKey = sourceKey;
        Track = track;
        CompiledResult = compiledResult;
    }

    public string SourceKey { get; }
    public AudioRenderTrackSnapshot? Track { get; }
    public CanonicalCompiledResult CompiledResult { get; }
    public bool Succeeded => CompiledResult.IsConsumable && !CompiledResult.IsPartial;
    public ICompilerDiagnosticSequence Diagnostics => CompiledResult.Diagnostics;
}

public sealed class AudioRenderCompilationResult
{
    internal AudioRenderCompilationResult(
        AudioRenderMode mode,
        long startTick,
        long? endTick,
        AudioRenderTrackSnapshot[] tracks,
        AudioRenderCompilationItem[] items,
        AudioRenderDiagnostic[] diagnostics)
    {
        Mode = mode;
        StartTick = startTick;
        EndTick = endTick;
        Tracks = Array.AsReadOnly(tracks);
        Items = Array.AsReadOnly(items);
        Diagnostics = Array.AsReadOnly(diagnostics);
    }

    public AudioRenderMode Mode { get; }
    public long StartTick { get; }
    public long? EndTick { get; }
    public ReadOnlyCollection<AudioRenderTrackSnapshot> Tracks { get; }
    public ReadOnlyCollection<AudioRenderCompilationItem> Items { get; }
    public ReadOnlyCollection<AudioRenderDiagnostic> Diagnostics { get; }
    public bool HasGlobalError => Diagnostics.Any(value => value.Severity == AudioRenderDiagnosticSeverity.Error);
    public bool HasRenderableOutput => !HasGlobalError && Items.Any(value => value.Succeeded);
}

public sealed class AudioRenderCompilationCoordinator
{
    public const string WholeMixSourceKey = "whole-project-audio";

    private readonly MidoraCompiler _compiler;

    public AudioRenderCompilationCoordinator(MidoraCompiler compiler)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    }

    public AudioRenderCompilationResult Compile(AudioRenderCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);
        if (request.StartTick < 0
            || request.EndTick.HasValue && request.EndTick.Value <= request.StartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The audio render range is invalid.");
        }

        HashSet<MidoraId>? selected = request.SelectedTrackIds?.ToHashSet();
        HashSet<MidoraId> instrumentIds = request.Project.EventInstruments
            .Select(value => value.Id)
            .ToHashSet();
        HashSet<MidoraId> damagedInstrumentIds = request.Project.DamagedEventInstruments
            .Select(value => value.Id)
            .ToHashSet();
        List<AudioRenderTrackSnapshot> tracks = [];
        List<AudioRenderDiagnostic> diagnostics = [];
        LogicalTrack[] logicalTracks = request.Project.LogicalTracksInArrangementOrder().ToArray();
        for (int index = 0; index < logicalTracks.Length; index++)
        {
            LogicalTrack track = logicalTracks[index];
            bool selectedForTask = selected is null || selected.Contains(track.Id);
            MidoraId? instrumentId = request.Project.ResolveEventInstrumentDefinitionId(track);
            bool bound = instrumentId.HasValue && instrumentIds.Contains(instrumentId.Value);
            bool damagedBinding = instrumentId.HasValue
                && damagedInstrumentIds.Contains(instrumentId.Value);
            bool participates = selectedForTask && (bound || damagedBinding);
            int displayOrder = index + 1;
            string displayName = InitialReleaseOutputNaming.GetLogicalTrackDisplayName(
                track.Name,
                displayOrder);
            string sourceKey = track.Id.ToString();
            string? exclusion = participates
                ? null
                : !selectedForTask
                    ? "Not selected"
                    : "No valid Event Instrument binding";
            tracks.Add(new(
                track.Id,
                sourceKey,
                displayOrder,
                displayName,
                participates,
                exclusion));
            if (selectedForTask && !bound)
            {
                if (damagedBinding)
                {
                    continue;
                }
                diagnostics.Add(new(
                    "MIDORA-AUDIO-RENDER-TRACK-UNBOUND",
                    AudioRenderDiagnosticSeverity.Info,
                    "The selected Logical Track has no valid Event Instrument binding and is ignored.",
                    sourceKey,
                    track.Id));
            }
        }

        AudioRenderTrackSnapshot[] participatingLogicalTracks = tracks
            .Where(value => value.Participates)
            .ToArray();
        HashSet<MidoraId> rootIds = request.Project.MidiChannelRoots
            .Select(value => value.Id)
            .ToHashSet();
        HashSet<MidoraId> damagedRootIds = request.Project.DamagedMidiChannelRoots
            .Select(value => value.Id)
            .ToHashSet();
        MidoraId[] participatingPureMidiTrackIds = request.Mode == AudioRenderMode.WholeMix
            ? request.Project.PureMidiTracks
                .Where(track => selected is null || selected.Contains(track.Id))
                .Where(track => rootIds.Contains(track.MidiChannelRootId)
                    || damagedRootIds.Contains(track.MidiChannelRootId))
                .Select(track => track.Id)
                .ToArray()
            : [];
        bool hasTarget = request.Mode switch
        {
            AudioRenderMode.WholeMix => participatingLogicalTracks.Length != 0
                || participatingPureMidiTrackIds.Length != 0,
            AudioRenderMode.PerLogicalTrack => participatingLogicalTracks.Length != 0,
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode))
        };
        if (!hasTarget)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-NO-TARGETS",
                AudioRenderDiagnosticSeverity.Error,
                request.Mode == AudioRenderMode.WholeMix
                    ? "At least one selected Logical Track or Pure MIDI Track with a valid parent is required."
                    : "At least one selected Logical Track with a valid Event Instrument binding is required."));
            return new(
                request.Mode,
                request.StartTick,
                request.EndTick,
                tracks.ToArray(),
                [],
                diagnostics.ToArray());
        }

        List<AudioRenderCompilationItem> items = request.Mode switch
        {
            AudioRenderMode.WholeMix => CompileWholeMix(
                request,
                participatingLogicalTracks
                    .Select(value => value.TrackId)
                    .Concat(participatingPureMidiTrackIds)),
            AudioRenderMode.PerLogicalTrack => CompileLogicalTracks(request, participatingLogicalTracks),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode))
        };
        long[] successfulEndTicks = items
            .Where(value => value.Succeeded)
            .Select(value => value.CompiledResult.EndTick)
            .Distinct()
            .ToArray();
        long? frozenEndTick = successfulEndTicks.Length == 1
            ? successfulEndTicks[0]
            : null;
        if (successfulEndTicks.Length > 1)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-RANGE-MISMATCH",
                AudioRenderDiagnosticSeverity.Error,
                "Independent audio render compilations did not freeze one common end tick."));
        }
        if (!frozenEndTick.HasValue || frozenEndTick.Value <= request.StartTick)
        {
            diagnostics.Add(new(
                "MIDORA-AUDIO-RENDER-EMPTY-RANGE",
                AudioRenderDiagnosticSeverity.Error,
                "The compiled audio render range has no positive duration."));
        }

        return new(
            request.Mode,
            request.StartTick,
            frozenEndTick,
            tracks.ToArray(),
            items.ToArray(),
            diagnostics.ToArray());
    }

    private List<AudioRenderCompilationItem> CompileWholeMix(
        AudioRenderCompilationRequest request,
        IEnumerable<MidoraId> trackIds)
    {
        CanonicalCompiledResult compiled = Compile(
            request,
            CompilationPurpose.AudioRender,
            trackIds,
            request.EndTick);
        return [new(WholeMixSourceKey, null, compiled)];
    }

    private List<AudioRenderCompilationItem> CompileLogicalTracks(
        AudioRenderCompilationRequest request,
        AudioRenderTrackSnapshot[] tracks)
    {
        List<AudioRenderCompilationItem> firstPass = [];
        foreach (AudioRenderTrackSnapshot track in tracks)
        {
            CanonicalCompiledResult compiled = Compile(
                request,
                CompilationPurpose.LogicalTrackAudioRender,
                [track.TrackId],
                request.EndTick);
            firstPass.Add(new(track.StableSourceKey, track, compiled));
        }

        if (request.EndTick.HasValue)
        {
            return firstPass;
        }

        long? commonEndTick = firstPass
            .Where(value => value.Succeeded)
            .Select(value => (long?)value.CompiledResult.EndTick)
            .Max();
        if (!commonEndTick.HasValue)
        {
            return firstPass;
        }

        List<AudioRenderCompilationItem> final = [];
        foreach (AudioRenderCompilationItem item in firstPass)
        {
            if (!item.Succeeded)
            {
                final.Add(item);
                continue;
            }

            CanonicalCompiledResult compiled = Compile(
                request,
                CompilationPurpose.LogicalTrackAudioRender,
                [item.Track!.TrackId],
                commonEndTick.Value);
            final.Add(new(item.SourceKey, item.Track, compiled));
        }
        return final;
    }

    private CanonicalCompiledResult Compile(
        AudioRenderCompilationRequest request,
        CompilationPurpose purpose,
        IEnumerable<MidoraId> includedTrackIds,
        long? endTick) =>
        _compiler.CompileFull(
            request.Project,
            new CompilationRequest
            {
                Purpose = purpose,
                StartTick = request.StartTick,
                EndTick = endTick,
                IncludedTrackIds = includedTrackIds.ToHashSet(),
                TreatWarningsAsErrors = request.TreatWarningsAsErrors
            });
}
