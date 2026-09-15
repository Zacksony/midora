using System.Globalization;
using System.Text;

namespace Midora.MidiExport;

public enum MidiExportRoutingStrategy
{
    Compact,
    Preserve
}

public enum MidiExportRangeSource
{
    ProjectEndMarker,
    NaturalContentEnd,
    Manual
}

public sealed record MidiExportReadmeTrack(
    string StableKey,
    int ProjectDisplayOrder,
    string DisplayName,
    bool Exported,
    string? ExclusionReason = null);

public sealed record MidiExportReadmePortMapping(
    int OriginalOneBasedPort,
    int OutputOneBasedPort,
    string? FileName = null);

public sealed record MidiExportReadmeDiagnostic(string Severity, string Code, string Message);

public sealed class MidiExportReadmeRequest
{
    public required string ProjectName { get; init; }
    public required string ProjectVersion { get; init; }
    public required string AuthorOrTeam { get; init; }
    public required string OriginalWork { get; init; }
    public required string Copyright { get; init; }
    public required long NoteOnEventCount { get; init; }
    public required MidiExportMode Mode { get; init; }
    public required MidiExportRangeSource RangeSource { get; init; }
    public required long StartTick { get; init; }
    public required long EndTick { get; init; }
    public required MidiExportRoutingStrategy Routing { get; init; }
    public required int TicksPerQuarterNote { get; init; }
    public required int TempoEventCount { get; init; }
    public required int TimeSignatureEventCount { get; init; }
    public required int KeySignatureEventCount { get; init; }
    public required IReadOnlyList<MidiExportReadmeTrack> Tracks { get; init; }
    public required IReadOnlyList<MidiExportReadmePortMapping> PortMappings { get; init; }
    public required IReadOnlyList<MidiExportReadmeDiagnostic> Diagnostics { get; init; }
    /// <summary>Full Warning/Info count when Diagnostics holds only a bounded prefix.</summary>
    public long? TotalDiagnosticCount { get; init; }
    public required IReadOnlyList<string> FileNames { get; init; }
    public required string CreatedWithSoftwareVersion { get; init; }
    public required string LastSavedWithSoftwareVersion { get; init; }
    public required string ExportSoftwareVersion { get; init; }
    public required DateTimeOffset ExportedAtUtc { get; init; }
}

public static class MidiExportReadmeBuilder
{
    public const int MaximumDiagnosticRows = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Build(MidiExportReadmeRequest request)
    {
        using MemoryStream output = new();
        WriteTo(output, request);
        return output.ToArray();
    }

    /// <summary>
    /// Writes strict UTF-8 without buffering the full document. The caller owns
    /// publication: cancellation, invalid input, or I/O failure may leave a
    /// partial stream, which the formal output transaction discards atomically.
    /// </summary>
    public static void WriteTo(
        Stream destination,
        MidiExportReadmeRequest request,
        CancellationToken cancellationToken = default,
        MidiExportPaddingSummary paddingSummary = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);
        if (!destination.CanWrite) throw new ArgumentException("The README stream is not writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request, cancellationToken);

        using StreamWriter output = new(destination, StrictUtf8, 16_384, leaveOpen: true);
        AppendLine(output, "# Midora MIDI Export");
        AppendLine(output);
        AppendLine(output, "## Project Metadata");
        AppendLine(output);
        AppendField(output, "Project name", request.ProjectName);
        AppendField(output, "Project version", request.ProjectVersion);
        AppendField(output, "Author or team", request.AuthorOrTeam);
        AppendField(output, "Original work / remix source", request.OriginalWork);
        AppendField(output, "Copyright", request.Copyright);
        AppendField(
            output,
            "Notes",
            request.NoteOnEventCount.ToString(CultureInfo.InvariantCulture));

        AppendLine(output);
        AppendLine(output, "## Export Configuration");
        AppendLine(output);
        AppendField(output, "Mode", ModeName(request.Mode));
        AppendField(output, "Range", $"[{request.StartTick}, {request.EndTick})");
        AppendField(output, "Range source", RangeSourceName(request.RangeSource));
        AppendField(output, "Routing", RoutingName(request.Routing));
        AppendField(output, "TPQ", request.TicksPerQuarterNote.ToString(CultureInfo.InvariantCulture));
        AppendField(output, "Tempo events", request.TempoEventCount.ToString(CultureInfo.InvariantCulture));
        AppendField(
            output,
            "Time Signature events",
            request.TimeSignatureEventCount.ToString(CultureInfo.InvariantCulture));
        AppendField(
            output,
            "Key Signature events",
            request.KeySignatureEventCount.ToString(CultureInfo.InvariantCulture));

        AppendLine(output);
        AppendLine(output, "## Track Selection");
        AppendLine(output);
        foreach (MidiExportReadmeTrack track in request.Tracks
            .OrderBy(track => track.ProjectDisplayOrder)
            .ThenBy(track => track.StableKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string status = track.Exported
                ? "exported"
                : $"excluded: {Inline(track.ExclusionReason)}";
            AppendLine(
                output,
                $"- {track.ProjectDisplayOrder.ToString(CultureInfo.InvariantCulture)}. " +
                $"{Escape(track.DisplayName)} — {Escape(status)}");
        }

        AppendLine(output);
        AppendLine(output, "## Port Mapping");
        AppendLine(output);
        if (request.PortMappings.Count == 0)
        {
            AppendLine(output, "- No canonical MIDI Port was used.");
        }
        else
        {
            foreach (MidiExportReadmePortMapping mapping in request.PortMappings
                .OrderBy(mapping => mapping.OriginalOneBasedPort)
                .ThenBy(mapping => mapping.OutputOneBasedPort)
                .ThenBy(mapping => mapping.FileName, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = string.IsNullOrEmpty(mapping.FileName)
                    ? string.Empty
                    : $" — {Escape(mapping.FileName)}";
                AppendLine(
                    output,
                    $"- Midora Port {mapping.OriginalOneBasedPort} → output Port " +
                    $"{mapping.OutputOneBasedPort}{file}");
            }
        }

        AppendLine(output);
        AppendLine(output, "## Channel 10 Melodic Compatibility");
        AppendLine(output);
        AppendLine(output, "Midora writes Roland GS and Yamaha XG Normal Part initialization, in that order, " +
            "for each event Track that actually uses Channel 10. It does not send a GS, XG, or GM reset and " +
            "does not replace canonical Bank or Program events. The fixed default vendor device identifiers " +
            "may be ignored by receivers configured for another identifier.");

        AppendLine(output);
        AppendLine(output, "## Compatibility Boundary");
        AppendLine(output);
        AppendLine(output, "Each event Track contains exactly one original Midora Channel Unit (Port + Channel), " +
            "and each used Unit appears in exactly one event Track in that file.");
        AppendLine(output);
        AppendLine(output, "The files contain standard MIDI 1.0 SMF Type 1 data. Third-party playback may differ " +
            "for multi-Port interpretation, MIDI Port Meta events, Channel 10 vendor initialization, SoundFont " +
            "selection, RPN/NRPN, Pitch Bend Range, and overlapping equal-pitch notes.");

        AppendLine(output);
        if (paddingSummary.HasPadding)
        {
            AppendLine(output, "## SMF Timing Compatibility (Export Info)");
            AppendLine(output);
            AppendLine(output, paddingSummary.Message);
            AppendLine(output, "On re-import, these standard empty Text Meta events may remain visible as imported metadata.");
            AppendLine(output);
        }
        AppendLine(output, "## Diagnostics");
        AppendLine(output);
        long diagnosticCount = GetTotalDiagnosticCount(request);
        if (diagnosticCount == 0)
        {
            AppendLine(output, "- No Warning or Information diagnostics.");
        }
        else
        {
            int shown = (int)Math.Min(diagnosticCount, MaximumDiagnosticRows);
            if (diagnosticCount > shown)
            {
                AppendLine(output, FormattableString.Invariant(
                    $"Showing the first {shown:N0} of {diagnosticCount:N0} diagnostics. {diagnosticCount - shown:N0} additional diagnostics are omitted. View the complete list in Midora."));
                AppendLine(output);
            }
            for (int index = 0; index < shown; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MidiExportReadmeDiagnostic diagnostic = request.Diagnostics[index];
                ValidateDiagnostic(diagnostic);
                AppendLine(output, $"- [{Escape(diagnostic.Severity)}] {Escape(diagnostic.Code)}: " +
                    Escape(diagnostic.Message));
            }
        }

        AppendLine(output);
        AppendLine(output, "## Files");
        AppendLine(output);
        foreach (string fileName in request.FileNames.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendLine(output, $"- {Escape(fileName)}");
        }

        AppendLine(output);
        AppendLine(output, "## Software");
        AppendLine(output);
        AppendField(output, "Created with", request.CreatedWithSoftwareVersion);
        AppendField(output, "Last saved with", request.LastSavedWithSoftwareVersion);
        AppendField(output, "Exported with", request.ExportSoftwareVersion);
        AppendField(
            output,
            "Exported at UTC",
            request.ExportedAtUtc.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture));

        cancellationToken.ThrowIfCancellationRequested();
        output.Flush();
    }

    internal static MidiExportReadmeRequest Freeze(MidiExportReadmeRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateDiagnosticPrefix(source);
        return new()
        {
            ProjectName = source.ProjectName,
            ProjectVersion = source.ProjectVersion,
            AuthorOrTeam = source.AuthorOrTeam,
            OriginalWork = source.OriginalWork,
            Copyright = source.Copyright,
            NoteOnEventCount = source.NoteOnEventCount,
            Mode = source.Mode,
            RangeSource = source.RangeSource,
            StartTick = source.StartTick,
            EndTick = source.EndTick,
            Routing = source.Routing,
            TicksPerQuarterNote = source.TicksPerQuarterNote,
            TempoEventCount = source.TempoEventCount,
            TimeSignatureEventCount = source.TimeSignatureEventCount,
            KeySignatureEventCount = source.KeySignatureEventCount,
            Tracks = source.Tracks.ToArray(),
            PortMappings = source.PortMappings.ToArray(),
            Diagnostics = source.Diagnostics is MidiExportReadmeDiagnosticProjection
                ? source.Diagnostics : source.Diagnostics.Take(MaximumDiagnosticRows).ToArray(),
            TotalDiagnosticCount = GetTotalDiagnosticCount(source),
            FileNames = source.FileNames.ToArray(),
            CreatedWithSoftwareVersion = source.CreatedWithSoftwareVersion,
            LastSavedWithSoftwareVersion = source.LastSavedWithSoftwareVersion,
            ExportSoftwareVersion = source.ExportSoftwareVersion,
            ExportedAtUtc = source.ExportedAtUtc
        };
    }

    private static void Validate(MidiExportReadmeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.ProjectName);
        ArgumentNullException.ThrowIfNull(request.ProjectVersion);
        ArgumentNullException.ThrowIfNull(request.AuthorOrTeam);
        ArgumentNullException.ThrowIfNull(request.OriginalWork);
        ArgumentNullException.ThrowIfNull(request.Copyright);
        ArgumentOutOfRangeException.ThrowIfNegative(request.NoteOnEventCount);
        ArgumentNullException.ThrowIfNull(request.Tracks);
        ArgumentNullException.ThrowIfNull(request.PortMappings);
        ValidateDiagnosticPrefix(request);
        ArgumentNullException.ThrowIfNull(request.FileNames);
        ArgumentNullException.ThrowIfNull(request.CreatedWithSoftwareVersion);
        ArgumentNullException.ThrowIfNull(request.LastSavedWithSoftwareVersion);
        ArgumentNullException.ThrowIfNull(request.ExportSoftwareVersion);
        if (request.StartTick < 0 || request.EndTick < request.StartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The MIDI export Readme range is invalid.");
        }
        if (request.TicksPerQuarterNote is < 1 or > 0x7fff)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TicksPerQuarterNote));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(request.TempoEventCount);
        ArgumentOutOfRangeException.ThrowIfNegative(request.TimeSignatureEventCount);
        ArgumentOutOfRangeException.ThrowIfNegative(request.KeySignatureEventCount);
        foreach (MidiExportReadmeTrack track in request.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(track);
            ArgumentException.ThrowIfNullOrEmpty(track.StableKey);
            ArgumentNullException.ThrowIfNull(track.DisplayName);
            ArgumentOutOfRangeException.ThrowIfLessThan(track.ProjectDisplayOrder, 1);
        }
        foreach (MidiExportReadmePortMapping mapping in request.PortMappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(mapping);
            if (mapping.OriginalOneBasedPort is < 1 or > 16
                || mapping.OutputOneBasedPort is < 1 or > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(request.PortMappings));
            }
        }
        foreach (string fileName in request.FileNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrEmpty(fileName);
        }
    }

    private static void ValidateDiagnosticPrefix(MidiExportReadmeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Diagnostics);
        long total = GetTotalDiagnosticCount(request);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (request.Diagnostics is MidiExportReadmeDiagnosticProjection projection && total != projection.TotalCount)
            throw new ArgumentException("The diagnostic total differs from the frozen projection.", nameof(request));
        if (total < request.Diagnostics.Count
            || request.Diagnostics.Count < Math.Min(total, MaximumDiagnosticRows))
            throw new ArgumentException("The diagnostic prefix and full count are inconsistent.", nameof(request));
    }

    private static long GetTotalDiagnosticCount(MidiExportReadmeRequest request) =>
        request.TotalDiagnosticCount
        ?? (request.Diagnostics as MidiExportReadmeDiagnosticProjection)?.TotalCount
        ?? request.Diagnostics.Count;

    private static void ValidateDiagnostic(MidiExportReadmeDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentException.ThrowIfNullOrEmpty(diagnostic.Severity);
        ArgumentException.ThrowIfNullOrEmpty(diagnostic.Code);
        ArgumentNullException.ThrowIfNull(diagnostic.Message);
        if (diagnostic.Severity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A success README.md cannot contain an Error diagnostic.", "Diagnostics");
    }

    private static string ModeName(MidiExportMode value) => value switch
    {
        MidiExportMode.WholeProject => "Whole Project",
        MidiExportMode.PerLogicalTrack => "Per Logical Track",
        MidiExportMode.PerPort => "Per Port",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string RangeSourceName(MidiExportRangeSource value) => value switch
    {
        MidiExportRangeSource.ProjectEndMarker => "Project End Marker",
        MidiExportRangeSource.NaturalContentEnd => "Natural content end",
        MidiExportRangeSource.Manual => "Manual range",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string RoutingName(MidiExportRoutingStrategy value) => value switch
    {
        MidiExportRoutingStrategy.Compact => "Compact",
        MidiExportRoutingStrategy.Preserve => "Preserve",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string Inline(string? value) => string.IsNullOrWhiteSpace(value) ? "not specified" : value;

    private static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder escaped = new(value.Length);
        foreach (char character in value)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']'
                or '<' or '>' or '(' or ')' or '#' or '+' or '!' or '|')
            {
                escaped.Append('\\');
            }
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static void AppendField(TextWriter output, string label, string value) =>
        AppendLine(output, $"- {label}: {Escape(string.IsNullOrWhiteSpace(value) ? "(not set)" : value)}");

    private static void AppendLine(TextWriter output, string? value = null)
    {
        if (value is not null)
        {
            output.Write(value);
        }
        output.Write('\n');
    }
}
