using System.Diagnostics;
using Midora.Domain;
using Midora.Compiler;
using Midora.Persistence;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class TemplateLengthSummaryTests(ITestOutputHelper output)
{
    [Fact]
    public void MinimumCoversEveryVoiceCurveLoopHalfAndPreRollAndTracksMutation()
    {
        var p = new MidoraProject(480);
        var instrument = new EventInstrument(p) { Name = "Template", TemplateLengthTicks = 2000 };
        var a = new SubVoice(p); var b = new SubVoice(p);
        instrument.SubVoices.Add(a); instrument.SubVoices.Add(b);
        Assert.Equal(1, Minimum());
        var n = new TemplateEvent(p) { Kind = TemplateEventKind.Note, Tick = 10, LengthTicks = 400, Number = 60, Value = 100 };
        a.Events.Add(n); Assert.Equal(410, Minimum());
        var cc = new TemplateEvent(p) { Kind = TemplateEventKind.ControlChange, Tick = 499, Number = 11, Value = 100 };
        b.Events.Add(cc); Assert.Equal(500, Minimum());
        var curve = new ValueCurve(p) { Target = MidiValueTarget.ControlChange(1) };
        var point = new CurvePoint(p, 600, 10);
        curve.Points.Add(point); b.Curves.Add(curve); Assert.Equal(601, Minimum());
        instrument.LoopStartTick = 700; Assert.Equal(701, Minimum());
        instrument.LoopStartTick = null; instrument.LoopEndTick = 800; Assert.Equal(800, Minimum());
        instrument.PreRollTicks = 900; Assert.Equal(900, Minimum());
        n.LengthTicks = 1200; Assert.Equal(1210, Minimum());
        n.LengthTicks = 400; instrument.PreRollTicks = 0; instrument.LoopEndTick = null;
        curve.Points[0] = new CurvePoint(p, 200, 10); Assert.Equal(500, Minimum());
        b.Events.Remove(cc); Assert.Equal(410, Minimum());
        b.Events.Add(cc); Assert.Equal(500, Minimum());
        long Minimum() => ProjectDomainEditCommands.GetMinimumTemplateLength(instrument);
    }

    [Fact]
    public void SaturatedMetadataDoesNotAcceptUnrepresentableEventEnds()
    {
        var p = new MidoraProject(480);
        var instrument = new EventInstrument(p) { Name = "Extreme", TemplateLengthTicks = long.MaxValue };
        var voice = new SubVoice(p); instrument.SubVoices.Add(voice);
        var note = new TemplateEvent(p) { Kind = TemplateEventKind.Note, Tick = long.MaxValue - 1, LengthTicks = 1, Number = 60, Value = 100 };
        voice.Events.Add(note);
        Assert.Equal(long.MaxValue, ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
        note.LengthTicks = 2;
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
        voice.Events.Clear();
        voice.Events.Add(new(p) { Kind = TemplateEventKind.PitchBend, Tick = long.MaxValue, Value = 0 });
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
        voice.Events.Clear();
        var curve = new ValueCurve(p) { Target = MidiValueTarget.ControlChange(1) };
        curve.Points.Add(new(p, long.MaxValue, 1)); voice.Curves.Add(curve);
        Assert.Throws<InvalidOperationException>(() => ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
    }

    [Fact]
    public void HotSummaryDoesNotEnumerateLargeNoteContent()
    {
        var p = new MidoraProject(480);
        var instrument = new EventInstrument(p) { Name = "Large", TemplateLengthTicks = 200000 };
        var voice = new SubVoice(p); instrument.SubVoices.Add(voice);
        voice.Events.AddRange(Enumerable.Range(0, 100000).Select(i => new TemplateEvent(p)
        { Kind = TemplateEventKind.Note, Tick = i, LengthTicks = 100, Number = 60, Value = 100 }));
        var watch = Stopwatch.StartNew();
        Assert.Equal(100099, ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
        output.WriteLine($"100k initial summary: {watch.Elapsed.TotalMilliseconds:F3} ms");
        watch.Restart(); long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++) Assert.Equal(100099, ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"100k content, 10k hot summary queries: {watch.Elapsed.TotalMilliseconds:F3} ms, {bytes} bytes");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.InRange(bytes, 0, 8 * 1024 * 1024);
    }

    [Fact]
    public async Task AcceptanceFixtureIsValidAndCanBeExportedWithoutOverwriting()
    {
        using var p = new MidoraProject(192);
        p.Metadata.ProjectName = "A4b - Template and Timeline";
        var instrument = EventInstrumentLibrary.Create(p, "Template 817 - minimum 601");
        instrument.TemplateLengthTicks = 817;
        instrument.PreRollTicks = 120;
        instrument.RequiresChannelIsolation = true;
        instrument.LoopStartTick = 192; instrument.LoopEndTick = 384;
        var voice = instrument.SubVoices[0]; voice.Name = "Note ends at 384"; voice.Events.Clear();
        voice.Events.Add(TemplateEvent.Note(p, 0, 384, 60, 100));
        var second = new SubVoice(p) { Name = "CC11 at 600 - minimum 601" };
        second.Events.Add(TemplateEvent.ControlChange(p, 600, 11, 80)); instrument.SubVoices.Add(second);
        var track = new LogicalTrack(p) { Name = "Logical - offset 1152" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(p, track, instrument.Id);
        var segment = new Segment(p) { ProjectStartTick = 1152, LengthTicks = 12000 };
        segment.Notes.Add(new(p) { StartTick = 384, LengthTicks = 96, Note = 60, Velocity = 100 });
        track.Segments.Add(segment);
        var root = new MidiChannelRoot(p) { Name = "MIDI" };
        var midi = new PureMidiTrack(p) { Name = "MIDI - keys 0 and 127", MidiChannelRootId = root.Id };
        var midiSegment = new MidiSegment(p) { ProjectStartTick = 3072, LengthTicks = 12000 };
        midiSegment.Notes.Add(new(p) { StartTick = 0, LengthTicks = 192, Key = 0, NoteOnVelocity = 100 });
        midiSegment.Notes.Add(new(p) { StartTick = 1536, LengthTicks = 192, Key = 127, NoteOnVelocity = 100 });
        midi.Segments.Add(midiSegment); p.MidiChannelRoots.Add(root); p.PureMidiTracks.Add(midi);
        p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midi.Id));
        p.Conductor.TimeSignatures.Add(new(p, 5000, 3, 4));
        Assert.Equal(601, ProjectDomainEditCommands.GetMinimumTemplateLength(instrument));
        using var compiler = new MidoraCompiler();
        var expected = compiler.CompileFull(p);
        Assert.True(expected.IsConsumable, string.Join("\n", expected.Diagnostics));
        string? path = Environment.GetEnvironmentVariable("MIDORA_A4B_UAT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Assert.False(File.Exists(path), "Do not replace an existing acceptance project.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var package = new MidoraProjectPackageV1("1.0.0-dev");
        await package.SaveCopyAsync(p, path);
        using var opened = await package.OpenAsync(path);
        Assert.Equal(4, opened.SourceFileFormatVersion);
        var restored = compiler.CompileFull(opened.Project);
        Assert.True(restored.IsConsumable, string.Join("\n", restored.Diagnostics));
        // Reopening uses the Pure MIDI paged backing. Events alone is only the
        // resident portion and its storage fingerprint is not a round-trip oracle.
        Assert.Equal(expected.QueryEventPages(expected.StartTick, expected.EndTick).SelectMany(page => page.Items),
            restored.QueryEventPages(restored.StartTick, restored.EndTick).SelectMany(page => page.Items));
        Assert.Equal(expected.SmfTracks.ToArray(), restored.SmfTracks.ToArray());
        Assert.Equal(restored.Fingerprint, compiler.CompileFull(opened.Project).Fingerprint);
        output.WriteLine($"Validated acceptance project: {path}");
    }
}
