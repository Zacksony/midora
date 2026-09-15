using Midora.Domain;

namespace Midora.Desktop;

/// <summary>The shared cursor/follow contract for editable and read-only project timelines.</summary>
internal interface IPlaybackTimelineWorkspace
{
    long StartTick { get; set; }
    long TickSpan { get; set; }
    long? PlaybackCursorTick { get; }
    void UpdatePlaybackCursor(MidoraProject project, long projectTick);
}
