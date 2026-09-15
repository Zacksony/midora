using Midora.Domain;

namespace Midora.Compiler;

public readonly record struct InstrumentPresetPreviewRequest(int BankMsb, int BankLsb, int Program,
    int Key = 60, int Velocity = 100, int DurationMilliseconds = 500, MidiChannelMode Mode = MidiChannelMode.Melodic)
{
    public void Validate()
    {
        if (BankMsb is < 0 or > 127 || BankLsb is < 0 or > 127 || Program is < 0 or > 127
            || Key is < 0 or > 127 || Velocity is < 1 or > 127 || DurationMilliseconds < 1 || !Enum.IsDefined(Mode))
            throw new ArgumentOutOfRangeException(nameof(InstrumentPresetPreviewRequest));
    }
}

/// <summary>A clean, isolated source Project, always consumed through canonical compilation.</summary>
public static class InstrumentPresetPreviewCompiler
{
    public const int TicksPerQuarterNote = 1000;
    public const decimal Tempo = 120m;
    private const int ReleaseWindowTicks = 4000;

    public static CanonicalCompiledResult Compile(InstrumentPresetPreviewRequest request,
        long? heldWindowEndTick = null, long? effectiveGateEndTick = null)
    {
        request.Validate();
        long gate = effectiveGateEndTick ?? heldWindowEndTick ?? checked(request.DurationMilliseconds * 2L);
        if (gate <= 0) throw new ArgumentOutOfRangeException(nameof(heldWindowEndTick));
        long end = heldWindowEndTick ?? checked(gate + ReleaseWindowTicks);
        using var project = new MidoraProject(TicksPerQuarterNote);
        project.Conductor.Tempos[0] = project.Conductor.Tempos[0] with { BeatsPerMinute = Tempo };
        var root = new MidiChannelRoot(project) { Name = "Preset preview", ChannelMode = request.Mode };
        var track = new PureMidiTrack(project) { Name = "Preset preview", MidiChannelRootId = root.Id };
        var segment = new MidiSegment(project) { LengthTicks = end };
        project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        track.Segments.Add(segment);
        segment.ChannelEvents.AddRange([
            new DirectMidiChannelEvent(project) { Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 0, Data2 = request.BankMsb, Order = 0 },
            new DirectMidiChannelEvent(project) { Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 32, Data2 = request.BankLsb, Order = 1 },
            new DirectMidiChannelEvent(project) { Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = request.Program, Order = 2 }
        ]);
        segment.Notes.Add(new DirectMidiNote(project) { Key = request.Key, LengthTicks = gate,
            NoteOnVelocity = request.Velocity, NoteOnOrder = 3, NoteOffOrder = 4 });
        using var compiler = new MidoraCompiler();
        return compiler.CompileFull(project);
    }
}
