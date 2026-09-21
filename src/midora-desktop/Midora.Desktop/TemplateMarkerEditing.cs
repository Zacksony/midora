using System.Globalization;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>UI packaging only: all validation/Undo stays in the formal commands.</summary>
internal static class TemplateMarkerEditing
{
    internal static IProjectEditCommand Set(EventInstrument instrument, TemplateTimelineMarker marker, long tick) => marker switch
    {
        TemplateTimelineMarker.TemplateEnd => ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(instrument.Id, tick),
        TemplateTimelineMarker.PreRoll => ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(instrument.Id, tick),
        TemplateTimelineMarker.LoopStart => ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, tick, instrument.LoopEndTick),
        TemplateTimelineMarker.LoopEnd => ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, instrument.LoopStartTick, tick),
        _ => throw new ArgumentOutOfRangeException(nameof(marker))
    };

    internal static IProjectEditCommand Delete(EventInstrument instrument, TemplateTimelineMarker marker) => marker switch
    {
        TemplateTimelineMarker.PreRoll => ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(instrument.Id, 0),
        TemplateTimelineMarker.LoopStart => ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, null, instrument.LoopEndTick),
        TemplateTimelineMarker.LoopEnd => ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, instrument.LoopStartTick, null),
        _ => throw new InvalidOperationException("Template Length cannot be deleted.")
    };

    internal static IProjectEditCommand AddLoop(EventInstrument instrument)
    {
        if (!instrument.RequiresChannelIsolation)
            throw new InvalidOperationException("Enable Per-Note Instance Isolation in the Event Instrument's Configurations before adding a Loop.");
        return ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrument.Id, 0, instrument.TemplateLengthTicks);
    }

    internal static long ParseTick(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long tick)
        ? tick : throw new FormatException("Enter a whole Tick value within the Int64 range.");

    internal static long ResolveAddedPreRollTick(long? clickedTick, long templateLength)
    {
        if (templateLength <= 0) throw new InvalidOperationException("Template Length must be greater than zero before adding Pre-Roll.");
        return clickedTick is >= 0 && clickedTick <= templateLength
            ? clickedTick.Value : Math.Max(1, templateLength / 2);
    }
}
