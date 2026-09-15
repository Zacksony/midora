using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport;

public enum MidiExportDiagnosticCategory
{
    CanonicalConsistency,
    Encoding
}

public sealed record MidiExportDiagnostic(
    string Code,
    MidiExportDiagnosticCategory Category,
    string Message,
    SourceReference Source = default);

public readonly record struct MidiExportChannelUnit
{
    public MidiExportChannelUnit(byte zeroBasedPort, byte zeroBasedChannel)
    {
        if (zeroBasedPort > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zeroBasedPort),
                "Midora MIDI export ports must be in the zero-based range 0..15.");
        }
        if (zeroBasedChannel > 15)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zeroBasedChannel),
                "MIDI channels must be in the zero-based range 0..15.");
        }

        ZeroBasedPort = zeroBasedPort;
        ZeroBasedChannel = zeroBasedChannel;
    }

    public byte ZeroBasedPort { get; }
    public byte ZeroBasedChannel { get; }
}

public sealed class MidiExportLogicalTrackLayout
{
    private readonly Dictionary<MidiExportChannelUnit, string> _eventTrackNamesByUnit;

    public MidiExportLogicalTrackLayout(
        MidoraId trackId,
        IReadOnlyDictionary<MidiExportChannelUnit, string> eventTrackNamesByUnit)
    {
        ArgumentNullException.ThrowIfNull(eventTrackNamesByUnit);
        if (trackId == default)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }
        foreach ((MidiExportChannelUnit unit, string name) in eventTrackNamesByUnit)
        {
            if (unit.ZeroBasedPort > 15 || unit.ZeroBasedChannel > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(eventTrackNamesByUnit),
                    "Midora MIDI export Channel Units must use Port and Channel values in the zero-based range 0..15.");
            }
            ArgumentNullException.ThrowIfNull(name);
        }
        TrackId = trackId;
        _eventTrackNamesByUnit = new(eventTrackNamesByUnit);
    }

    public MidoraId TrackId { get; }
    public IReadOnlyDictionary<MidiExportChannelUnit, string> EventTrackNamesByUnit =>
        _eventTrackNamesByUnit;
}

public sealed class WholeProjectMidiEncodingRequest
{
    public required CanonicalCompiledResult CompiledResult { get; init; }
    public required string ConductorTrackName { get; init; }
    public required IReadOnlyList<MidiExportLogicalTrackLayout> LogicalTracks { get; init; }
}

public sealed class LogicalTrackMidiEncodingRequest
{
    public required CanonicalCompiledResult CompiledResult { get; init; }
    public required string ConductorTrackName { get; init; }
    public required MidiExportLogicalTrackLayout LogicalTrack { get; init; }
}

public sealed class PortMidiEncodingRequest
{
    public required CanonicalCompiledResult CompiledResult { get; init; }
    public required string ConductorTrackName { get; init; }
    public required IReadOnlyList<MidiExportLogicalTrackLayout> LogicalTracks { get; init; }
    public required byte ZeroBasedOriginalPort { get; init; }
}

public sealed class MidiExportEncodingResult
{
    private readonly Func<Stream, CancellationToken, IProgress<StandardMidiFileWriteProgress>?, StandardMidiFileWriteSummary>? _writer;
    private readonly IReadOnlyList<SourceReference> _sources = [];
    private readonly long _startTick;
    private byte[]? _fileBytes;

    internal MidiExportEncodingResult(byte[] fileBytes, MidiExportDiagnostic[] diagnostics)
    {
        _fileBytes = fileBytes ?? throw new ArgumentNullException(nameof(fileBytes));
        Diagnostics = diagnostics;
    }

    internal MidiExportEncodingResult(
        Func<Stream, CancellationToken, IProgress<StandardMidiFileWriteProgress>?, StandardMidiFileWriteSummary> writer,
        MidiExportDiagnostic[] diagnostics, IReadOnlyList<SourceReference> sources, long startTick)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        Diagnostics = diagnostics;
        _sources = sources;
        _startTick = startTick;
    }

    /// <summary>Preparation succeeded; deferred encoding is validated by the atomic output transaction.</summary>
    public bool Succeeded => Diagnostics.Count == 0
        && (_writer is not null || _fileBytes is { Length: > 0 });
    public byte[] FileBytes
    {
        get
        {
            if (_fileBytes is not null) return _fileBytes;
            using MemoryStream output = new();
            WriteTo(output);
            _fileBytes = output.ToArray();
            return _fileBytes;
        }
    }
    public IReadOnlyList<MidiExportDiagnostic> Diagnostics { get; }
    public StandardMidiFileWriteSummary WriteSummary { get; private set; }
    public bool EncodingCompleted { get; private set; }

    internal void WriteTo(Stream output, CancellationToken cancellationToken = default,
        IProgress<StandardMidiFileWriteProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        EncodingCompleted = false;
        WriteSummary = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_writer is not null) WriteSummary = _writer(output, cancellationToken, progress);
            else output.Write(_fileBytes!);
            EncodingCompleted = true;
        }
        catch (Exception exception) when (exception is MidoraMidiException or ArgumentException or OverflowException)
        {
            SourceReference source = default;
            if (exception is StandardMidiFileEncodingException boundary)
            {
                if (boundary.TrackIndex is int index && index >= 0 && index < _sources.Count)
                    source = _sources[index];
                if (boundary.Tick is long tick) source = source with { Tick = checked(_startTick + tick) };
            }
            throw new MidiExportEncodingException(MidiExportEncodingErrors.FromException(exception, source), exception);
        }
    }
}
