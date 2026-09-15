using System.Globalization;
using Midora.Compiler;
using Midora.Midi;

namespace Midora.MidiExport;

/// <summary>Export-only Info, independent of compiler diagnostics and Warning-as-Error.</summary>
public readonly record struct MidiExportPaddingSummary(
    long PaddingEventCount, long PaddedTrackCount, int AffectedFileCount)
{
    public bool HasPadding => PaddingEventCount != 0;
    public string Message => string.Create(CultureInfo.InvariantCulture,
        $"SMF timing compatibility: inserted {PaddingEventCount} empty Text Meta event(s) in {PaddedTrackCount} MIDI track(s) across {AffectedFileCount} file(s) to encode long delta times. ") +
        "Original event ticks, order and track end ticks are unchanged. " +
        "No padding was added to the Project or compiled result.";

    internal static MidiExportPaddingSummary Aggregate(IEnumerable<MidiExportEncodingResult> encodings)
    {
        long events = 0, tracks = 0;
        int files = 0;
        foreach (MidiExportEncodingResult encoding in encodings)
        {
            StandardMidiFileWriteSummary summary = encoding.WriteSummary;
            events = checked(events + summary.PaddingEventCount);
            tracks = checked(tracks + summary.PaddedTrackCount);
            if (summary.PaddingEventCount != 0) files++;
        }
        return new(events, tracks, files);
    }
}

public readonly record struct MidiExportProgress(string FileName, string Phase,
    StandardMidiFileWriteProgress? Encoding = null);

public sealed class MidiExportEncodingException : Exception
{
    internal MidiExportEncodingException(MidiExportDiagnostic diagnostic, Exception cause)
        : base(diagnostic.Message, cause) => Diagnostic = diagnostic;
    public MidiExportDiagnostic Diagnostic { get; }
}

internal static class MidiExportEncodingErrors
{
    internal static MidiExportDiagnostic FromException(Exception exception, SourceReference source = default)
    {
        string suffix = exception is StandardMidiFileEncodingException limit ? limit.Boundary switch
        {
            StandardMidiFileEncodingBoundary.TrackDataLength => "MTRK-SIZE",
            StandardMidiFileEncodingBoundary.PayloadLength => "PAYLOAD-LENGTH",
            StandardMidiFileEncodingBoundary.TrackCount => "TRACK-COUNT",
            StandardMidiFileEncodingBoundary.TicksPerQuarterNote => "TPQN",
            _ => "ENCODING"
        } : "ENCODING";
        return new("MIDORA-MIDI-EXPORT-" + suffix, MidiExportDiagnosticCategory.Encoding, exception.Message, source);
    }
}
