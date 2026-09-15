using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Midora.Midi;

public enum StandardMidiFileEncodingBoundary
{
    TicksPerQuarterNote,
    TrackCount,
    PayloadLength,
    TrackDataLength
}

/// <summary>An SMF encoding limit, not an I/O or compilation failure.</summary>
public sealed class StandardMidiFileEncodingException : MidoraMidiException
{
    internal StandardMidiFileEncodingException(StandardMidiFileEncodingBoundary boundary,
        long limit, long actual, string message, long? tick = null) : base(message)
    {
        Boundary = boundary;
        Limit = limit;
        Actual = actual;
        Tick = tick;
    }

    public StandardMidiFileEncodingBoundary Boundary { get; }
    public long Limit { get; }
    /// <summary>For TrackDataLength, the proven minimum required data bytes, not an estimate.</summary>
    public long Actual { get; }
    public long? Tick { get; }
    public int? TrackIndex { get; internal set; }
    public string? TrackName { get; internal set; }
    public override string Message =>
        (TrackIndex is int index ? $"MTrk {index + 1}" +
            (string.IsNullOrEmpty(TrackName) ? "" : $" ({TrackName})") + ": " : "") +
        base.Message + (Tick is long tick ? $" Event/EOT tick: {tick}." : "");
}

public readonly record struct StandardMidiFileWriteSummary(long PaddingEventCount, int PaddedTrackCount);

/// <summary>Actual encoding work; padding counts describe the current gap, not a guessed file total.</summary>
public readonly record struct StandardMidiFileWriteProgress(
    int TrackIndex, int TrackCount, string? TrackName, long EventCount, long DataByteCount,
    long PaddingEventsWritten, long PaddingEventsRequired, bool TrackComplete);

public static partial class StandardMidiFile
{
    public const long MaximumTrackDataLength = uint.MaxValue;
    internal const int PaddingEventsPerBlock = 8192;
    private const int PayloadBlockBytes = 64 * 1024;

    // Allocated only on the exceptional long-gap path. Never an array of event objects.
    private static class PaddingBlock
    {
        internal static readonly byte[] Bytes = Create();
        private static byte[] Create()
        {
            byte[] bytes = new byte[7 * PaddingEventsPerBlock];
            ReadOnlySpan<byte> record = [0xff, 0xff, 0xff, 0x7f, 0xff, TextMetaType, 0];
            for (int offset = 0; offset < bytes.Length; offset += 7)
                record.CopyTo(bytes.AsSpan(offset));
            return bytes;
        }
    }

    internal readonly record struct DeltaEncoding(long PaddingEvents, int Remainder, long ByteCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DeltaEncoding PlanDelta(long delta)
    {
        if (delta < 0) throw new MidoraMidiException("SMF delta time cannot be negative.");
        if (delta <= MaximumVariableLengthValue)
            return new(0, (int)delta, VariableLengthByteCount((int)delta));
        long padding = (delta - 1) / MaximumVariableLengthValue;
        int remainder = (int)(delta - padding * MaximumVariableLengthValue);
        return new(padding, remainder, checked(padding * 7 + VariableLengthByteCount(remainder)));
    }

    /// <summary>Reserve the entire next record before writing any of its padding. No future-gap guess.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static DeltaEncoding ReserveTrackRecord(ref long writtenBytes, long delta,
        long bodyBytes, bool isEndOfTrack, long tick)
    {
        if (writtenBytes < 0 || writtenBytes > MaximumTrackDataLength || bodyBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(writtenBytes));
        DeltaEncoding plan = PlanDelta(delta);
        long next = checked(writtenBytes + plan.ByteCount + bodyBytes);
        long minimum = checked(next + (isEndOfTrack ? 0 : 4));
        if (minimum > MaximumTrackDataLength) ThrowTrackSizeLimit(minimum, tick);
        writtenBytes = next;
        return plan;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowTrackSizeLimit(long minimum, long tick) =>
        throw new StandardMidiFileEncodingException(StandardMidiFileEncodingBoundary.TrackDataLength,
            MaximumTrackDataLength, minimum,
            $"SMF track data requires at least {minimum} bytes; the limit is {MaximumTrackDataLength} " +
            "bytes (excluding the 8-byte MTrk header). Tracks are not split.", tick);

    public static void ValidateEncodingHeader(int ticksPerQuarterNote, int trackCount)
    {
        if (ticksPerQuarterNote is < 1 or > 0x7fff)
            throw new StandardMidiFileEncodingException(StandardMidiFileEncodingBoundary.TicksPerQuarterNote,
                0x7fff, ticksPerQuarterNote,
                $"SMF ticks-per-quarter-note must be in the range 1..32767, but was {ticksPerQuarterNote}.");
        if (trackCount is < 1 or > ushort.MaxValue)
            throw new StandardMidiFileEncodingException(StandardMidiFileEncodingBoundary.TrackCount,
                ushort.MaxValue, trackCount,
                $"SMF Type 1 track count must be in the range 1..{ushort.MaxValue}, but was {trackCount}.");
    }

    internal static void ValidatePayloadLength(long length, long tick)
    {
        if (length is < 0 or > MaximumVariableLengthValue)
            throw new StandardMidiFileEncodingException(StandardMidiFileEncodingBoundary.PayloadLength,
                MaximumVariableLengthValue, length,
                $"SMF Meta/SysEx payload length must be in 0..{MaximumVariableLengthValue}, but was {length}. " +
                "A payload cannot be split by delta-time padding.", tick);
    }

    private static int VariableLengthByteCount(int value) =>
        value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x200000 ? 3 : 4;

    private sealed class WriteProgress(IProgress<StandardMidiFileWriteProgress>? target, int trackCount)
    {
        private long _lastReport;
        public int TrackIndex;
        public string? TrackName;
        public long Events;

        public void Report(long bytes, long paddingWritten = 0, long paddingRequired = 0,
            bool complete = false, bool force = false)
        {
            if (target is null) return;
            long now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(_lastReport, now).TotalMilliseconds < 100) return;
            _lastReport = now;
            target.Report(new(TrackIndex, trackCount, TrackName, Events, bytes,
                paddingWritten, paddingRequired, complete));
        }
    }

    private static void WriteDelta(Stream output, DeltaEncoding plan, CancellationToken token,
        WriteProgress progress, long precedingBytes)
    {
        if (plan.PaddingEvents != 0)
        {
            long written = 0;
            while (written < plan.PaddingEvents)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(PaddingEventsPerBlock, plan.PaddingEvents - written);
                output.Write(PaddingBlock.Bytes.AsSpan(0, count * 7));
                written += count;
                progress.Report(precedingBytes + written * 7, written, plan.PaddingEvents);
            }
        }
        WriteVariableLength(output, plan.Remainder);
    }

    private static void WritePayload(Stream output, ReadOnlySpan<byte> payload,
        CancellationToken token, WriteProgress progress, long precedingBytes)
    {
        int offset = 0;
        while (offset < payload.Length)
        {
            token.ThrowIfCancellationRequested();
            int count = Math.Min(PayloadBlockBytes, payload.Length - offset);
            output.Write(payload.Slice(offset, count));
            offset += count;
            if (payload.Length > PayloadBlockBytes) progress.Report(precedingBytes + offset);
        }
    }
}
