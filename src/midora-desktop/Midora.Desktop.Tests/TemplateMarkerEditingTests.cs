using Midora.Desktop.Presentation.Controls;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class TemplateMarkerEditingTests
{
    [Theory]
    [InlineData(TemplateTimelineMarker.TemplateEnd, 1000)]
    [InlineData(TemplateTimelineMarker.LoopStart, 100)]
    [InlineData(TemplateTimelineMarker.LoopEnd, 700)]
    [InlineData(TemplateTimelineMarker.PreRoll, 400)]
    public void SetAndUndoUseExistingAtomicCommands(TemplateTimelineMarker marker, long tick)
    {
        using var p = new MidoraProject(192);
        var instrument = Instrument(p);
        var before = Values(instrument);
        var edit = TemplateMarkerEditing.Set(instrument, marker, tick).Prepare(p);
        Assert.Equal(before, Values(instrument));
        edit.Apply(p);
        Assert.Equal(tick, marker switch
        {
            TemplateTimelineMarker.TemplateEnd => instrument.TemplateLengthTicks,
            TemplateTimelineMarker.LoopStart => instrument.LoopStartTick,
            TemplateTimelineMarker.LoopEnd => instrument.LoopEndTick,
            _ => instrument.PreRollTicks
        });
        edit.Undo(p); Assert.Equal(before, Values(instrument));
        edit.Apply(p); edit.Undo(p); Assert.Equal(before, Values(instrument));
    }

    [Theory]
    [InlineData(TemplateTimelineMarker.TemplateEnd, 200)]
    [InlineData(TemplateTimelineMarker.TemplateEnd, 0)]
    [InlineData(TemplateTimelineMarker.LoopStart, -1)]
    [InlineData(TemplateTimelineMarker.LoopStart, 384)]
    [InlineData(TemplateTimelineMarker.LoopEnd, 192)]
    [InlineData(TemplateTimelineMarker.LoopEnd, 769)]
    [InlineData(TemplateTimelineMarker.PreRoll, -1)]
    [InlineData(TemplateTimelineMarker.PreRoll, 769)]
    public void InvalidExactValuesAreRejectedWithoutClampingOrMutation(TemplateTimelineMarker marker, long tick)
    {
        using var p = new MidoraProject(192); var instrument = Instrument(p); var before = Values(instrument);
        Exception error = Assert.ThrowsAny<Exception>(() => TemplateMarkerEditing.Set(instrument, marker, tick).Prepare(p));
        Assert.True(error is ArgumentException or InvalidOperationException);
        Assert.Equal(before, Values(instrument));
    }

    [Theory]
    [InlineData(TemplateTimelineMarker.LoopStart)]
    [InlineData(TemplateTimelineMarker.LoopEnd)]
    [InlineData(TemplateTimelineMarker.PreRoll)]
    public void DeleteOnlyRemovesTheChosenMarkerAndUndoRestoresIt(TemplateTimelineMarker marker)
    {
        using var p = new MidoraProject(192); var instrument = Instrument(p); var before = Values(instrument);
        var edit = TemplateMarkerEditing.Delete(instrument, marker).Prepare(p); edit.Apply(p);
        Assert.Equal(marker == TemplateTimelineMarker.LoopStart ? null : (long?)192, instrument.LoopStartTick);
        Assert.Equal(marker == TemplateTimelineMarker.LoopEnd ? null : (long?)384, instrument.LoopEndTick);
        Assert.Equal(marker == TemplateTimelineMarker.PreRoll ? 0 : 96, instrument.PreRollTicks);
        edit.Undo(p); Assert.Equal(before, Values(instrument));
        Assert.Throws<InvalidOperationException>(() => TemplateMarkerEditing.Delete(instrument, TemplateTimelineMarker.TemplateEnd));
    }

    [Fact]
    public void AddLoopExplainsIsolationRequirementAndUsesTheFullTemplateInOneEdit()
    {
        using var p = new MidoraProject(192); var instrument = Instrument(p); var before = Values(instrument);
        instrument.RequiresChannelIsolation = false;
        Assert.Contains("Per-Note Instance Isolation", Assert.Throws<InvalidOperationException>(() => TemplateMarkerEditing.AddLoop(instrument)).Message);
        Assert.Equal(before, Values(instrument));
        instrument.RequiresChannelIsolation = true;
        var edit = TemplateMarkerEditing.AddLoop(instrument).Prepare(p); edit.Apply(p);
        Assert.Equal(0, instrument.LoopStartTick); Assert.Equal(768, instrument.LoopEndTick);
        edit.Undo(p); Assert.Equal(before, Values(instrument));
    }

    [Theory]
    [InlineData(97L, 768, 97)]
    [InlineData(0L, 768, 0)]
    [InlineData(768L, 768, 768)]
    [InlineData(-1L, 768, 384)]
    [InlineData(769L, 768, 384)]
    [InlineData(null, 1, 1)]
    [InlineData(long.MaxValue, 768, 384)]
    [InlineData(null, long.MaxValue, long.MaxValue / 2)]
    public void AddPreRollFallbackStaysInRange(long? click, long length, long expected)
        => Assert.Equal(expected, TemplateMarkerEditing.ResolveAddedPreRollTick(click, length));

    [Fact]
    public void ExactTickParserDoesNotLoseInt64PrecisionAndRejectsMalformedInput()
    {
        Assert.Equal(9007199254740993, TemplateMarkerEditing.ParseTick("9007199254740993"));
        Assert.Equal(long.MaxValue, TemplateMarkerEditing.ParseTick(long.MaxValue.ToString()));
        foreach (string value in new[] { "", " ", "1.5", "1e3", "abc", "9223372036854775808" })
            Assert.Throws<FormatException>(() => TemplateMarkerEditing.ParseTick(value));
        Assert.Throws<InvalidOperationException>(() => TemplateMarkerEditing.ResolveAddedPreRollTick(1, 0));
    }

    private static EventInstrument Instrument(MidoraProject p)
    {
        var instrument = new EventInstrument(p) { Name = "Markers", TemplateLengthTicks = 768,
            RequiresChannelIsolation = true, LoopStartTick = 192, LoopEndTick = 384, PreRollTicks = 96 };
        p.EventInstruments.Add(instrument); return instrument;
    }
    private static (long, long?, long?, long) Values(EventInstrument i) => (i.TemplateLengthTicks, i.LoopStartTick, i.LoopEndTick, i.PreRollTicks);
}
