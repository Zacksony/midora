using System.Collections.ObjectModel;
using System.Globalization;

namespace Midora.MidiExport;

public sealed record LogicalTrackMidiArtifactRequest(
    string StableSourceKey,
    LogicalTrackMidiEncodingRequest EncodingRequest);

public sealed record PortMidiArtifactRequest(PortMidiEncodingRequest EncodingRequest);

public sealed record MidiExportArtifactDiagnostic(
    string SourceKey,
    MidiExportDiagnostic Diagnostic);

public sealed class MidiExportArtifactBuildResult
{
    private readonly IReadOnlyList<MidiExportEncodingResult> _encodings;
    internal MidiExportArtifactBuildResult(
        MidiExportPreparedArtifact[] artifacts,
        MidiExportArtifactDiagnostic[] diagnostics,
        IReadOnlyList<MidiExportEncodingResult>? encodings = null)
    {
        Artifacts = Array.AsReadOnly(artifacts);
        Diagnostics = Array.AsReadOnly(diagnostics);
        _encodings = encodings ?? [];
    }

    public bool Succeeded => Diagnostics.Count == 0 && Artifacts.Count != 0;
    public ReadOnlyCollection<MidiExportPreparedArtifact> Artifacts { get; }
    public ReadOnlyCollection<MidiExportArtifactDiagnostic> Diagnostics { get; }
    public MidiExportPaddingSummary PaddingSummary => MidiExportPaddingSummary.Aggregate(_encodings);
}

public static class MidiExportArtifactBuilder
{
    private const string WholeProjectSourceKey = "whole-project-midi";
    private const string LogicalTrackSourceKeyPrefix = "logical-track:";
    private const string PortSourceKeyPrefix = "port:";
    private const string ReadmeSourceKey = "readme";

    public static MidiExportArtifactBuildResult BuildWholeProject(
        MidiExportFrozenOutputPlan plan,
        WholeProjectMidiEncodingRequest encodingRequest,
        MidiExportReadmeRequest? readmeRequest = null,
        CancellationToken cancellationToken = default)
    {
        RequireMode(plan, MidiExportMode.WholeProject);
        MidiExportEncodingResult encoded = CanonicalMidiFileExporter.EncodeWholeProject(encodingRequest, cancellationToken);
        return Complete(
            plan,
            [(WholeProjectSourceKey, encoded)],
            readmeRequest);
    }

    public static MidiExportArtifactBuildResult BuildLogicalTracks(
        MidiExportFrozenOutputPlan plan,
        IEnumerable<LogicalTrackMidiArtifactRequest> requests,
        MidiExportReadmeRequest? readmeRequest = null,
        CancellationToken cancellationToken = default)
    {
        RequireMode(plan, MidiExportMode.PerLogicalTrack);
        ArgumentNullException.ThrowIfNull(requests);
        List<(string SourceKey, MidiExportEncodingResult Result)> encoded = [];
        HashSet<string> sourceKeys = new(StringComparer.Ordinal);
        foreach (LogicalTrackMidiArtifactRequest request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrEmpty(request.StableSourceKey);
            ArgumentNullException.ThrowIfNull(request.EncodingRequest);
            string sourceKey = LogicalTrackSourceKeyPrefix + request.StableSourceKey;
            if (!sourceKeys.Add(sourceKey))
            {
                throw new ArgumentException(
                    $"Duplicate Logical Track MIDI artifact source key '{sourceKey}'.",
                    nameof(requests));
            }
            encoded.Add((
                sourceKey,
                CanonicalMidiFileExporter.EncodeLogicalTrack(request.EncodingRequest, cancellationToken)));
        }
        return Complete(plan, encoded, readmeRequest);
    }

    public static MidiExportArtifactBuildResult BuildPorts(
        MidiExportFrozenOutputPlan plan,
        IEnumerable<PortMidiArtifactRequest> requests,
        MidiExportReadmeRequest? readmeRequest = null,
        CancellationToken cancellationToken = default)
    {
        RequireMode(plan, MidiExportMode.PerPort);
        ArgumentNullException.ThrowIfNull(requests);
        List<(string SourceKey, MidiExportEncodingResult Result)> encoded = [];
        HashSet<byte> ports = [];
        foreach (PortMidiArtifactRequest request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.EncodingRequest);
            byte port = request.EncodingRequest.ZeroBasedOriginalPort;
            if (!ports.Add(port))
            {
                throw new ArgumentException(
                    $"Duplicate Per-Port MIDI artifact request for Port {port + 1}.",
                    nameof(requests));
            }
            encoded.Add((
                PortSourceKeyPrefix + (port + 1).ToString("D2", CultureInfo.InvariantCulture),
                CanonicalMidiFileExporter.EncodePort(request.EncodingRequest, cancellationToken)));
        }
        return Complete(plan, encoded, readmeRequest);
    }

    private static MidiExportArtifactBuildResult Complete(
        MidiExportFrozenOutputPlan plan,
        IEnumerable<(string SourceKey, MidiExportEncodingResult Result)> encodedFiles,
        MidiExportReadmeRequest? readmeRequest)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.Succeeded)
        {
            throw new ArgumentException(
                "MIDI export artifacts cannot be built for a failed output plan.",
                nameof(plan));
        }

        List<MidiExportPreparedArtifact> artifacts = [];
        List<MidiExportArtifactDiagnostic> diagnostics = [];
        List<MidiExportEncodingResult> encodings = [];
        foreach ((string sourceKey, MidiExportEncodingResult result) in encodedFiles)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (!result.Succeeded)
            {
                diagnostics.AddRange(result.Diagnostics.Select(diagnostic => new MidiExportArtifactDiagnostic(
                    sourceKey,
                    diagnostic)));
                continue;
            }
            artifacts.Add(new(sourceKey, result.WriteTo));
            encodings.Add(result);
        }
        if (diagnostics.Count != 0)
        {
            return new([], diagnostics.ToArray());
        }

        bool readmePlanned = plan.Targets.Any(target => target.SourceKey == ReadmeSourceKey);
        if (readmePlanned != (readmeRequest is not null))
        {
            throw new ArgumentException(
                readmePlanned
                    ? "The frozen output plan requires a README.md snapshot."
                    : "A README.md snapshot was supplied but the frozen output plan excludes it.",
                nameof(readmeRequest));
        }
        if (readmeRequest is not null)
        {
            if (readmeRequest.Mode != plan.Mode)
            {
                throw new ArgumentException(
                    "The README.md mode does not match the frozen output plan.",
                    nameof(readmeRequest));
            }
            string[] readmeFiles = readmeRequest.FileNames.Order(StringComparer.Ordinal).ToArray();
            string[] plannedFiles = plan.Targets.Select(target => target.FileName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!readmeFiles.SequenceEqual(plannedFiles, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "The README.md file list does not exactly match the frozen output plan.",
                    nameof(readmeRequest));
            }
            MidiExportReadmeRequest frozenReadme = MidiExportReadmeBuilder.Freeze(readmeRequest);
            artifacts.Add(new(ReadmeSourceKey,
                (output, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (encodings.Any(encoding => !encoding.EncodingCompleted))
                        throw new InvalidOperationException("MIDI artifacts must be encoded before their README summary.");
                    MidiExportReadmeBuilder.WriteTo(output, frozenReadme, token,
                        MidiExportPaddingSummary.Aggregate(encodings));
                }));
        }

        string[] plannedKeys = plan.Targets.Select(target => target.SourceKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] artifactKeys = artifacts.Select(artifact => artifact.SourceKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!plannedKeys.SequenceEqual(artifactKeys, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Encoded MIDI artifact keys do not exactly match the frozen output targets.",
                nameof(encodedFiles));
        }
        return new(artifacts.ToArray(), [], encodings);
    }

    private static void RequireMode(MidiExportFrozenOutputPlan plan, MidiExportMode expectedMode)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mode != expectedMode)
        {
            throw new ArgumentException(
                $"The frozen output plan mode is {plan.Mode}, not {expectedMode}.",
                nameof(plan));
        }
    }
}
