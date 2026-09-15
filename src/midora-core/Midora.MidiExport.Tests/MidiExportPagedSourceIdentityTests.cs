using System.Text;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportPagedSourceIdentityTests
{
    // More than 131,072 endpoints forces the production SMF sorter onto disk,
    // including Root-generated records that have no DirectMidiObjectId.
    private const int NoteCount = 70_000;
    private const long EndTick = NoteCount * 4L + 11;

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 0L)]
    [InlineData(false, 16L)]
    [InlineData(true, 16L)]
    public void LargeSharedRootExportsKeepGeneratedStateAndExactNoteEndpoints(bool fixedRoute, long startTick)
    {
        using TestDirectory directory = new();
        using MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Shared", RoutingMode = fixedRoute ? MidiChannelRootRoutingMode.Fixed : MidiChannelRootRoutingMode.Auto,
            FixedZeroBasedPort = fixedRoute ? (byte)1 : (byte)0,
            FixedZeroBasedChannel = fixedRoute ? (byte)9 : (byte)0
        };
        project.MidiChannelRoots.Add(root);
        MidiSegment notes = AddTrack(project, root, "Notes");
        MidiSegment controls = AddTrack(project, root, "Controls");
        using PureMidiContentPackWriter writer = new(Path.Combine(directory.Root, "source.mpk"));
        for (int index = 0; index < NoteCount; index++)
            writer.AddNote(notes.Id, new(project.AllocateStableId(), index * 4L, 2,
                index % 128, index % 127 + 1, 37, index * 2L, index * 2L + 1));
        writer.AddChannelEvent(controls.Id, new(project.AllocateStableId(), 1,
            DirectMidiChannelEventKind.ControlChange, 11, 40, 0));
        writer.AddOpaqueEvent(controls.Id, new(project.AllocateStableId(), 32,
            OpaqueMidiEventKind.Meta, StandardMidiFile.MarkerMetaType, "Marker"u8.ToArray(), 1));
        using PureMidiContentPack pack = writer.Complete();
        notes.AttachPagedContent(pack.GetSegmentSource(notes.Id));
        controls.AttachPagedContent(pack.GetSegmentSource(controls.Id));

        using MidoraCompiler compiler = new();
        var compiled = compiler.CompileFull(project, new()
        {
            Purpose = CompilationPurpose.MidiExport, StartTick = startTick
        });
        Assert.True(compiled.IsConsumable, string.Join("\n", compiled.Diagnostics));
        Assert.True(compiled.HasPagedEvents);
        Assert.Equal(NoteCount - startTick / 4, compiled.TotalNoteOnEventCount);
        foreach (bool perPort in new[] { false, true })
        {
            var encoded = perPort
                ? CanonicalMidiFileExporter.EncodePort(new()
                {
                    CompiledResult = compiled, ConductorTrackName = "Song", LogicalTracks = [],
                    ZeroBasedOriginalPort = fixedRoute ? (byte)1 : (byte)0
                })
                : CanonicalMidiFileExporter.EncodeWholeProject(new()
                {
                    CompiledResult = compiled, ConductorTrackName = "Song", LogicalTracks = []
                });
            Assert.True(encoded.Succeeded, string.Join("\n", encoded.Diagnostics));
            using MemoryStream first = new(), second = new();
            encoded.WriteTo(first);
            encoded.WriteTo(second);
            Assert.Equal(first.ToArray(), second.ToArray());
            first.Position = 0;
            StandardMidiFile.ValidateType1(first);
            var parsed = StandardMidiFile.ParseType0Or1(first.ToArray());
            Assert.Equal(new[] { "Song", "Notes", "Controls" }, parsed.Tracks.Select(track =>
                Encoding.UTF8.GetString(track.Events.Single(e => e.Kind == StandardMidiFileEventKind.Meta
                    && e.Type == StandardMidiFile.TrackNameMetaType).Data.Span)));
            Assert.All(parsed.Tracks, track => Assert.Equal(EndTick - startTick, track.EndTick));
            var noteEvents = parsed.Tracks[1].Events.Where(e => e.Kind == StandardMidiFileEventKind.ChannelVoice
                && e.Message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff).ToArray();
            int firstNote = (int)(startTick / 4);
            Assert.Equal((NoteCount - firstNote) * 2, noteEvents.Length);
            byte channel = fixedRoute ? (byte)9 : (byte)0;
            for (int index = firstNote; index < NoteCount; index++)
            {
                int offset = (index - firstNote) * 2;
                Assert.Equal(index * 4L - startTick, noteEvents[offset].Tick);
                Assert.Equal(MidiMessage.NoteOn(channel, (byte)(index % 128), (byte)(index % 127 + 1)), noteEvents[offset].Message);
                Assert.Equal(index * 4L + 2 - startTick, noteEvents[offset + 1].Tick);
                Assert.Equal(MidiMessage.NoteOff(channel, (byte)(index % 128), 37), noteEvents[offset + 1].Message);
            }
            Assert.Contains(parsed.Tracks[1].Events, e => e.Tick == 0 && IsControl(e, 121, 0));
            Assert.Contains(parsed.Tracks[1].Events, e => e.Tick == EndTick - startTick && IsControl(e, 120, 0));
            Assert.Contains(parsed.Tracks[2].Events, e => e.Tick == (startTick == 0 ? 1 : 0) && IsControl(e, 11, 40));
            Assert.Single(parsed.Tracks[2].Events, e => e.Tick == 32 - startTick
                && e.Kind == StandardMidiFileEventKind.Meta && e.Type == StandardMidiFile.MarkerMetaType
                && e.Data.Span.SequenceEqual("Marker"u8));
        }
    }

    private static MidiSegment AddTrack(MidoraProject project, MidiChannelRoot root, string name)
    {
        PureMidiTrack track = new(project) { Name = name, MidiChannelRootId = root.Id };
        MidiSegment segment = new(project) { LengthTicks = EndTick };
        track.Segments.Add(segment);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return segment;
    }

    private static bool IsControl(ParsedStandardMidiFileEvent value, byte controller, byte data) =>
        value.Kind == StandardMidiFileEventKind.ChannelVoice && value.Message.MessageType == MidiMessageType.ControlChange
        && value.Message.Byte1 == controller && value.Message.Byte2 == data;

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory() => Directory.CreateDirectory(Root);
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, ".tmp", "smf-spill-" + Guid.NewGuid().ToString("N"));
        public void Dispose() => Directory.Delete(Root, true);
    }
}
