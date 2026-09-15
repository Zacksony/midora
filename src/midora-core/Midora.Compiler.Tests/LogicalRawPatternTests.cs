using System.Collections;
using System.Reflection;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class LogicalRawPatternTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RepeatedInstancesSharePatternsButPreserveEverySource(int voices)
    {
        var fixture = CompilerTestProject.Create(voices, segmentLength: 480 * 256);
        fixture.Instrument.OverlapPolicy = OverlapPolicy.LetOverlap;
        fixture.Instrument.RequiresChannelIsolation = true;
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
        {
            voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 120, 60, 100));
            voice.Events.Add(TemplateEvent.ControlChange(fixture.Project, 10, 11, 80));
        }
        for (int i = 0; i < 256; i++) CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, i * 480, 240);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(fixture.Project);
        Assert.True(before.IsConsumable);
        Assert.Equal(256 * voices, before.TotalNoteOnEventCount);
        var on = Events(before).Where(v => v.Message.MessageType == MidiMessageType.NoteOn).ToArray();
        foreach (CanonicalMidiEvent value in on)
        {
            LogicalNote note = fixture.Segment.Notes.Single(n => n.Id == value.Source.LogicalNoteId);
            Assert.Equal(note.StartTick, value.Tick);
            Assert.Equal(value.Tick, value.Source.Tick);
            Assert.Equal(fixture.Track.Id, value.Source.TrackId);
            Assert.Equal(fixture.Instrument.Id, value.Source.EventInstrumentId);
            Assert.Contains(fixture.Instrument.SubVoices, v => v.Id == value.Source.SubVoiceId);
        }
        HashSet<object> arrays = new(ReferenceEqualityComparer.Instance);
        int sequences = 0;
        foreach (DictionaryEntry track in (IDictionary)Field(compiler, "_trackCache"))
        foreach (object segment in (IEnumerable)Property(track.Value!, "Segments"))
        foreach (object instance in (IEnumerable)Property(segment, "Instances"))
        foreach (object voice in (IEnumerable)Property(instance, "Voices"))
        {
            sequences++;
            arrays.Add(Field(Property(voice, "Events"), "_events"));
        }
        Assert.Equal(256 * voices, sequences);
        Assert.Equal(voices, arrays.Count);

        fixture.Segment.Notes[100].Velocity = 50;
        CanonicalCompiledResult incremental = compiler.CompileIncremental(fixture.Project,
            new ProjectChangeSet { TrackIds = { fixture.Track.Id } });
        using MidoraCompiler reference = new();
        CanonicalCompiledResult full = reference.CompileFull(fixture.Project);
        Assert.Equal(Events(full), Events(incremental));
        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        Assert.Equal(256 * voices, before.TotalNoteOnEventCount);
        Assert.Equal(on, Events(before).Where(v => v.Message.MessageType == MidiMessageType.NoteOn));
    }

    [Fact]
    public void PatternInternerIsBoundedAndDoesNotChangeUniqueResults()
    {
        var fixture = CompilerTestProject.Create(segmentLength: 480 * 4200);
        fixture.Instrument.OverlapPolicy = OverlapPolicy.LetOverlap;
        fixture.Instrument.RequiresChannelIsolation = true;
        fixture.Instrument.LongLifecycle = LongNoteLifecycle.HoldLastState;
        fixture.Voice.Events.Add(TemplateEvent.Note(fixture.Project, 0, 2_100_000, 60, 100));
        fixture.Instrument.TemplateLengthTicks = 2_100_000;
        for (int i = 0; i < 4200; i++) CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, i * 480, i + 1);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult result = compiler.CompileFull(fixture.Project);
        Assert.True(result.IsConsumable);
        Assert.Equal(4200, result.TotalNoteOnEventCount);
        Assert.Equal(4200, Events(result).Count(v => v.Message.MessageType == MidiMessageType.NoteOff));
    }

    private static IEnumerable<CanonicalMidiEvent> Events(CanonicalCompiledResult result) =>
        result.QueryEventPages(result.StartTick, result.EndTick).SelectMany(page => page.Items);

    private static object Field(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
    private static object Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
}
