using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class MidiEditingValueDomainTests
{
    [Fact]
    public void EveryControllerAndEveryByteRoundTripsWithoutChangingOtherTargets()
    {
        for (int cc = 0; cc < 128; cc++)
        for (int raw = 0; raw < 128; raw++)
        {
            int offset = cc is 10 or >= 71 and <= 78 ? -64 : 0;
            Assert.Equal(raw + offset, MidiEditingValueDomain.ControllerDisplay(cc, raw));
            Assert.Equal(raw, MidiEditingValueDomain.ControllerRaw(cc, raw + offset));
        }
        Assert.Equal(0, MidiEditingValueDomain.Offset(MidiValueTarget.PitchBend));
        Assert.Equal(0, MidiEditingValueDomain.Offset(MidiValueTarget.Program));
        Assert.Equal(0, MidiEditingValueDomain.Offset(MidiValueTarget.Rpn(10)));
        Assert.Equal(0, MidiEditingValueDomain.ClampInitialState(MidiValueTarget.ControlChange(10), int.MinValue));
        Assert.Equal(127, MidiEditingValueDomain.ClampInitialState(MidiValueTarget.ControlChange(10), int.MaxValue));
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 4100)] [InlineData(true, 4100)]
    public void BothCommandPathsUseDisplayExpressionsAndUndoRestoresRaw(bool template, int count)
    {
        using var p = new MidoraProject(480);
        var instrument = EventInstrumentLibrary.Create(p, "Instrument");
        var voice = instrument.SubVoices[0];
        var midi = CreateMidi(p);
        if (template) voice.Events.AddRange(Enumerable.Range(0, count).Select(i => TemplateEvent.ControlChange(p, i * 2, 10, 96)));
        else midi.ChannelEvents.AddRange(Enumerable.Range(0, count).Select(i => new DirectMidiChannelEvent(p)
            { Tick = i * 2, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 10, Data2 = 96, Order = i }));
        var ids = template ? voice.Events.Select(e => e.Id).ToArray() : midi.ChannelEvents.Select(e => e.Id).ToArray();
        foreach (string formula in new[] { "=p0*0.5", "*0.5", "50%", "+8", "=1000", "=-1000" })
        {
            using var program = BatchEditExpressionProgram.Compile(new Dictionary<BatchEditField, string?> { [BatchEditField.PointValue] = formula, [BatchEditField.Tick] = null });
            var edit = (template ? ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(instrument.Id, voice.Id, ids,
                    MidiValueTarget.ControlChange(10), program)
                : ProjectDomainEditCommands.BatchEditDirectMidiEventPoints(midi.Id, ids, program)).Prepare(p);
            edit.Apply(p);
            int expected = formula switch { "+8" => 104, "=1000" => 127, "=-1000" => 0, _ => 80 };
            Assert.All(Read(), value => Assert.Equal(expected, value));
            edit.Undo(p); Assert.All(Read(), value => Assert.Equal(96, value));
            edit.Apply(p); Assert.All(Read(), value => Assert.Equal(expected, value));
            edit.Undo(p);
        }
        IEnumerable<int> Read() => template ? instrument.SubVoices[0].Events.CreateQuerySnapshot().EnumerateAll().Select(e => e.Value)
            : p.PureMidiTracks[0].Segments[0].ChannelEvents.CreateQuerySnapshot().QueryValues(0, long.MaxValue).Select(e => e.Data2);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void GeneratorFeedbackAndInitialObjectRemainInDisplayDomain(bool template, bool initial)
    {
        using var p = new MidoraProject(480);
        var instrument = EventInstrumentLibrary.Create(p, "Instrument");
        var voice = instrument.SubVoices[0];
        var midi = CreateMidi(p);
        EventGenerationOptions options = new() { MaximumCandidates = 4, InitialValue = 32, ValueExpression = "=p0*0.5",
            TickExpression = "=i*10", CreateFirstFromInitialValues = initial };
        var command = template ? ProjectDomainEditCommands.GenerateTemplateEventPoints(instrument.Id, voice.Id, MidiValueTarget.ControlChange(10), options)
            : ProjectDomainEditCommands.GenerateDirectMidiEventPoints(midi.Id, DirectMidiChannelEventKind.ControlChange, 10, options);
        var edit = command.Prepare(p); edit.Apply(p);
        int[] expected = initial ? [96, 80, 72, 68] : [80, 72, 68, 66];
        Assert.Equal(expected, template ? instrument.SubVoices[0].Events.Select(e => e.Value)
            : p.PureMidiTracks[0].Segments[0].ChannelEvents.Select(e => e.Data2));
        edit.Undo(p);
        Assert.Empty(template ? instrument.SubVoices[0].Events.Select(e => e.Id)
            : p.PureMidiTracks[0].Segments[0].ChannelEvents.Select(e => e.Id));
    }

    [Fact]
    public void MixedControllerPropertyValuesDecodeAgainstEachDestinationTarget()
    {
        using var p = new MidoraProject(480); var midi = CreateMidi(p);
        foreach (int cc in new[] { 10, 11, 74 }) midi.ChannelEvents.Add(new(p)
            { Kind = DirectMidiChannelEventKind.ControlChange, Data1 = cc, Data2 = 96, Order = cc });
        var ids = midi.ChannelEvents.Select(e => e.Id).ToArray();
        var edit = ProjectDomainEditCommands.SetDirectMidiEventValues(midi.Id, ids, controllerDisplayValue: 16).Prepare(p);
        edit.Apply(p); Assert.Equal(new[] { 80, 16, 80 }, midi.ChannelEvents.Select(e => e.Data2));
        edit.Undo(p); Assert.All(midi.ChannelEvents, e => Assert.Equal(96, e.Data2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectDomainEditCommands.SetDirectMidiEventValues(midi.Id, ids, controllerDisplayValue: -64).Prepare(p));
        Assert.All(midi.ChannelEvents, e => Assert.Equal(96, e.Data2));
    }

    internal static MidiSegment CreateMidi(MidoraProject p)
    {
        var root = new MidiChannelRoot(p) { Name = "Root" }; p.MidiChannelRoots.Add(root);
        var track = new PureMidiTrack(p) { Name = "MIDI", MidiChannelRootId = root.Id }; p.PureMidiTracks.Add(track);
        p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var midi = new MidiSegment(p) { LengthTicks = 1_000_000 }; track.Segments.Add(midi); return midi;
    }
}
