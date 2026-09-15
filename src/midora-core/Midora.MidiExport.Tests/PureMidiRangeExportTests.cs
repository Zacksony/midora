using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;

namespace Midora.MidiExport.Tests;

public sealed class PureMidiRangeExportTests
{
    [Fact]
    public void CroppedSharedRootExportsIdenticalBytesAndRespectsEachTrackEndInBothLayouts()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, ".tmp", "range-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using MidoraProject resident = Create(), paged = Create();
            using PureMidiContentPack pack = Page(paged, Path.Combine(folder, "source.mpk"));
            using MidoraCompiler a = new(), b = new();
            foreach (long start in new[] { 0L, 10L, 20L, 23L })
            foreach (long end in new[] { 50L, 100L, 140L })
            {
                CompilationRequest request = new() { Purpose = CompilationPurpose.MidiExport, StartTick = start, EndTick = end };
                CanonicalCompiledResult x = a.CompileFull(resident, request), y = b.CompileFull(paged, request);
                Assert.True(x.IsConsumable); Assert.True(y.IsConsumable);
                var xe = x.QueryEventPages(start, end, true).SelectMany(p => p.Items).ToArray();
                var ye = y.QueryEventPages(start, end, true).SelectMany(p => p.Items).ToArray();
                Assert.Equal(xe, ye);
                Assert.Equal(ye.LongLength, y.TotalEventCount);
                Assert.Equal(ye.LongCount(e => e.Message.MessageType == MidiMessageType.NoteOn && e.Message.Byte2 != 0), y.TotalNoteOnEventCount);
                if (end == 50)
                {
                    // The short Track's Off consumed the long Track's older On.
                    // The remaining FIFO source is the short Track, whose EOT is
                    // already past. Root-final generated output belongs to the
                    // lowest-order participating boundary Track (SRS 23.7.5).
                    CanonicalMidiEvent off = Assert.Single(ye, e => e.Tick == end && e.Message.MessageType == MidiMessageType.NoteOff);
                    Assert.Equal(paged.PureMidiTracks[1].Segments[0].Notes.EnumerateValues().Single().Id, off.Source.DirectMidiObjectId);
                    Assert.Equal(paged.PureMidiTracks[1].Id, off.Source.TrackId);
                    Assert.Equal(paged.PureMidiTracks[0].Id, off.ExportTrackId);
                    Assert.Equal(off.ExportTrackId, off.Source.ExportTrackId);
                }
                MidiExportEncodingResult first = Encode(x), second = Encode(y);
                Assert.True(first.Succeeded, string.Join("\n", first.Diagnostics));
                Assert.True(second.Succeeded, string.Join("\n", second.Diagnostics));
                Assert.Equal(first.FileBytes, second.FileBytes);
                ParsedStandardMidiFile parsed = StandardMidiFile.ParseType0Or1(first.FileBytes);
                Assert.Equal(new[] { end - start, Math.Min(100, end) - start, Math.Max(0, 20 - start) },
                    parsed.Tracks.Select(t => t.EndTick));
                Assert.All(parsed.Tracks, t => Assert.All(t.Events, e => Assert.True(e.Tick <= t.EndTick)));
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    private static MidiExportEncodingResult Encode(CanonicalCompiledResult result) =>
        CanonicalMidiFileExporter.EncodeWholeProject(new() { CompiledResult = result, ConductorTrackName = "Range", LogicalTracks = [] });

    private static MidoraProject Create()
    {
        MidoraProject p = new(480);
        MidiChannelRoot root = new(p) { Name = "Root", RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 1, FixedZeroBasedChannel = 9 };
        p.MidiChannelRoots.Add(root);
        for (int i = 0; i < 2; i++)
        {
            PureMidiTrack t = new(p) { Name = i == 0 ? "Long" : "Short", MidiChannelRootId = root.Id };
            MidiSegment s = new(p) { ProjectStartTick = i == 0 ? 0 : 10, LengthTicks = i == 0 ? 100 : 10 };
            s.Notes.Add(new(p) { StartTick = 0, LengthTicks = s.LengthTicks, Key = 60, NoteOnVelocity = 100,
                NoteOffVelocity = i == 0 ? 47 : 45, NoteOnOrder = long.MaxValue, NoteOffOrder = long.MaxValue });
            if (i == 1)
            {
                s.ChannelEvents.Add(new(p) { Tick = 7, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 11, Data2 = 53, Order = long.MaxValue });
                s.OpaqueEvents.Add(new(p) { Tick = 8, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = "text"u8.ToArray(), Order = long.MaxValue });
            }
            t.Segments.Add(s); p.PureMidiTracks.Add(t); p.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, t.Id));
        }
        return p;
    }

    private static PureMidiContentPack Page(MidoraProject p, string path)
    {
        using PureMidiContentPackWriter writer = new(path);
        foreach (MidiSegment s in p.PureMidiTracks.SelectMany(t => t.Segments))
        {
            foreach (DirectMidiNoteValue n in s.Notes.EnumerateValues()) writer.AddNote(s.Id, n);
            foreach (DirectMidiChannelEventValue e in s.ChannelEvents.EnumerateValues()) writer.AddChannelEvent(s.Id, e);
            foreach (OpaqueMidiEventValue e in s.OpaqueEvents.EnumerateValues()) writer.AddOpaqueEvent(s.Id, e);
        }
        PureMidiContentPack pack = writer.Complete();
        foreach (MidiSegment s in p.PureMidiTracks.SelectMany(t => t.Segments))
        { s.Notes.Clear(); s.ChannelEvents.Clear(); s.OpaqueEvents.Clear(); s.AttachPagedContent(pack.GetSegmentSource(s.Id)); }
        return pack;
    }
}
