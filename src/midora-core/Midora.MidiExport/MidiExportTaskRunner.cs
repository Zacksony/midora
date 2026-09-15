using System.Collections.ObjectModel;
using Midora.Compiler;
using Midora.OutputPlanning;

namespace Midora.MidiExport;

public enum MidiExportTaskStatus
{
    Succeeded,
    Failed,
    Cancelled
}

public sealed class MidiExportTaskRequest
{
    public required MidiExportCompilationResult Compilation { get; init; }
    public required MidiExportFrozenOutputPlan OutputPlan { get; init; }
    public MidiExportReadmeRequest? Readme { get; init; }
    public bool OverwriteAuthorized { get; init; }
}

public sealed class MidiExportTaskResult
{
    internal MidiExportTaskResult(
        MidiExportTaskStatus status,
        IEnumerable<CompilerDiagnostic> compilerDiagnostics,
        MidiExportArtifactDiagnostic[] artifactDiagnostics,
        MidiExportOutputResult? output,
        MidiExportOutputException? outputFailure,
        MidiExportPaddingSummary paddingSummary = default)
    {
        Status = status;
        CompilerDiagnostics = CompilerDiagnosticSequence.Wrap(compilerDiagnostics);
        ArtifactDiagnostics = Array.AsReadOnly(artifactDiagnostics);
        Output = output;
        OutputFailure = outputFailure;
        PaddingSummary = paddingSummary;
    }

    public MidiExportTaskStatus Status { get; }
    public ICompilerDiagnosticSequence CompilerDiagnostics { get; }
    public ReadOnlyCollection<MidiExportArtifactDiagnostic> ArtifactDiagnostics { get; }
    public MidiExportOutputResult? Output { get; }
    public MidiExportOutputException? OutputFailure { get; }
    public MidiExportPaddingSummary PaddingSummary { get; }
}

public sealed class MidiExportTaskRunner
{
    private readonly MidiExportOutputTransaction _outputTransaction;

    public MidiExportTaskRunner()
        : this(new MidiExportOutputTransaction())
    {
    }

    internal MidiExportTaskRunner(MidiExportOutputTransaction outputTransaction)
    {
        _outputTransaction = outputTransaction ?? throw new ArgumentNullException(nameof(outputTransaction));
    }

    public async Task<MidiExportTaskResult> ExecuteAsync(
        MidiExportTaskRequest request,
        CancellationToken cancellationToken = default,
        IProgress<MidiExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Compilation);
        ArgumentNullException.ThrowIfNull(request.OutputPlan);
        if (request.Compilation.Mode != request.OutputPlan.Mode)
        {
            throw new ArgumentException(
                "The frozen MIDI export compilation and output plan modes differ.",
                nameof(request));
        }
        if (!request.Compilation.Succeeded)
        {
            return new(
                MidiExportTaskStatus.Failed,
                request.Compilation.Diagnostics,
                [],
                null,
                null);
        }

        try
        {
            progress?.Report(new("", "Preparing encoding"));
            MidiExportArtifactBuildResult artifacts = BuildArtifacts(request, cancellationToken);
            if (!artifacts.Succeeded)
            {
                return new(
                    MidiExportTaskStatus.Failed,
                    request.Compilation.Diagnostics,
                    artifacts.Diagnostics.ToArray(),
                    null,
                    null);
            }

            MidiExportOutputResult output = await _outputTransaction.PublishAsync(
                request.OutputPlan,
                artifacts.Artifacts,
                request.OverwriteAuthorized,
                cancellationToken, progress).ConfigureAwait(false);
            return new(
                MidiExportTaskStatus.Succeeded,
                request.Compilation.Diagnostics,
                [],
                output,
                null, artifacts.PaddingSummary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(
                MidiExportTaskStatus.Cancelled,
                request.Compilation.Diagnostics,
                [],
                null,
                null);
        }
        catch (MidiExportOutputException exception)
        {
            MidiExportArtifactDiagnostic? diagnostic = exception.EncodingDiagnostic
                ?? (exception.InnerException as MidiExportOutputException)?.EncodingDiagnostic;
            return new(
                MidiExportTaskStatus.Failed,
                request.Compilation.Diagnostics,
                diagnostic is null ? [] : [diagnostic],
                null,
                exception);
        }
    }

    private static MidiExportArtifactBuildResult BuildArtifacts(MidiExportTaskRequest request,
        CancellationToken cancellationToken)
    {
        MidiExportCompilationResult compilation = request.Compilation;
        CanonicalCompiledResult compiled = compilation.CompiledResult;
        string conductorTrackName = compilation.ConductorTrackName;
        return compilation.Mode switch
        {
            MidiExportMode.WholeProject => MidiExportArtifactBuilder.BuildWholeProject(
                request.OutputPlan,
                new WholeProjectMidiEncodingRequest
                {
                    CompiledResult = compiled,
                    ConductorTrackName = conductorTrackName,
                    LogicalTracks = compilation.Layouts
                },
                request.Readme, cancellationToken),
            MidiExportMode.PerLogicalTrack => MidiExportArtifactBuilder.BuildLogicalTracks(
                request.OutputPlan,
                compilation.Layouts.Select(layout => new LogicalTrackMidiArtifactRequest(
                    compilation.Tracks.Single(track => track.TrackId == layout.TrackId).StableSourceKey,
                    new LogicalTrackMidiEncodingRequest
                    {
                        CompiledResult = compiled,
                        ConductorTrackName = conductorTrackName,
                        LogicalTrack = layout
                    })),
                request.Readme, cancellationToken),
            MidiExportMode.PerPort => MidiExportArtifactBuilder.BuildPorts(
                request.OutputPlan,
                compilation.UsedZeroBasedPorts.Select(port => new PortMidiArtifactRequest(
                    new PortMidiEncodingRequest
                    {
                        CompiledResult = compiled,
                        ConductorTrackName = conductorTrackName,
                        LogicalTracks = compilation.Layouts,
                        ZeroBasedOriginalPort = port
                    })),
                request.Readme, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(compilation.Mode))
        };
    }
}
