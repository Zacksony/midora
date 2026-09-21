namespace Midora.Desktop;

// Shared by the original VM initialization and the session value protocol.
internal static class PianoEditorDefaults
{
    internal const long TickSpan = 3072;
    internal const double LaneHeight = 15;
    internal const int SegmentFirstLane = 48, SubVoiceFirstLane = 59;
    internal static long SegmentTickSpan(int tpqn) => Math.Max(TickSpan, (long)tpqn * 16);
}
