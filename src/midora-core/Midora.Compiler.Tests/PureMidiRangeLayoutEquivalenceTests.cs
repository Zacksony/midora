using Midora.Domain;
using Midora.Midi;
using Xunit.Abstractions;

namespace Midora.Compiler.Tests;

/// <summary>Independent range-end oracle, not gated behind the full-project comparison.</summary>
public sealed class PureMidiRangeLayoutEquivalenceTests(ITestOutputHelper output)
{
    [Fact]
    public void EarlyConsumerEndClosesSharedRootNotesAndResetsStateInBothSourceLayouts()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp",
            "pure-midi-range-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using MidoraProject materializedProject = CreateProject();
            using MidoraProject pagedProject = CreateProject();
            using PureMidiContentPackWriter writer = new(Path.Combine(directory, "source.mpk"));
            foreach (PureMidiTrack track in pagedProject.PureMidiTracks)
            {
                MidiSegment segment = track.Segments[0];
                foreach (DirectMidiNote note in segment.Notes)
                    writer.AddNote(segment.Id, new(note.Id, note.StartTick, note.LengthTicks,
                        note.Key, note.NoteOnVelocity, note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder));
                foreach (DirectMidiChannelEvent value in segment.ChannelEvents)
                    writer.AddChannelEvent(segment.Id, new(value.Id, value.Tick, value.Kind,
                        value.Data1, value.Data2, value.Order));
                foreach (OpaqueMidiEvent value in segment.OpaqueEvents)
                    writer.AddOpaqueEvent(segment.Id, new(value.Id, value.Tick, value.Kind,
                        value.MetaType, value.Payload, value.Order));
            }
            using PureMidiContentPack pack = writer.Complete();
            foreach (PureMidiTrack track in pagedProject.PureMidiTracks)
            {
                MidiSegment segment = track.Segments[0];
                segment.Notes.Clear();
                segment.ChannelEvents.Clear();
                segment.OpaqueEvents.Clear();
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            }
            CompilationRequest request = new()
            { Purpose = CompilationPurpose.Playback, StartTick = 300, EndTick = 600 };
            using MidoraCompiler materializedCompiler = new();
            using MidoraCompiler pagedCompiler = new();
            CanonicalCompiledResult materialized = materializedCompiler.CompileFull(materializedProject, request);
            CanonicalCompiledResult paged = pagedCompiler.CompileFull(pagedProject, request);
            CanonicalMidiEvent[] materializedEvents = Events(materialized);
            CanonicalMidiEvent[] pagedEvents = Events(paged);
            Dump("materialized", materialized, materializedEvents);
            Dump("paged", paged, pagedEvents);

            Assert.False(materialized.HasPagedEvents);
            Assert.True(paged.HasPagedEvents);
            AssertRange(materialized, materializedEvents, materializedProject);
            AssertRange(paged, pagedEvents, pagedProject);
            Assert.Equal(materialized.TotalEventCount, paged.TotalEventCount);
            Assert.Equal(materialized.TotalNoteOnEventCount, paged.TotalNoteOnEventCount);
            Assert.Equal(materializedEvents, pagedEvents);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        project.GlobalResetDefaults.Controllers[11] = 23;
        MidiChannelRoot root = new(project)
        {
            Name = "Shared", RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 1, FixedZeroBasedChannel = 9, ChannelMode = MidiChannelMode.Melodic
        };
        project.MidiChannelRoots.Add(root);
        for (int index = 0; index < 2; index++)
        {
            PureMidiTrack track = new(project) { Name = "Child " + index, MidiChannelRootId = root.Id };
            MidiSegment segment = new(project) { LengthTicks = 1920 };
            segment.Notes.Add(new(project)
            {
                StartTick = index == 0 ? 0 : 360, LengthTicks = index == 0 ? 960 : 480,
                Key = index == 0 ? 36 : 38, NoteOnVelocity = index == 0 ? 100 : 90,
                NoteOffVelocity = index == 0 ? 37 : 45, NoteOnOrder = 0, NoteOffOrder = 1
            });
            segment.ChannelEvents.Add(new(project)
            {
                Tick = index == 0 ? 240 : 400, Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = index == 0 ? 11 : 91, Data2 = index == 0 ? 55 : 99, Order = 2
            });
            if (index == 1) segment.OpaqueEvents.Add(new(project)
            { Tick = 420, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = "range"u8.ToArray(), Order = 3 });
            track.Segments.Add(segment);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        }
        return project;
    }

    private static void AssertRange(CanonicalCompiledResult result, CanonicalMidiEvent[] events, MidoraProject project)
    {
        Assert.True(result.IsConsumable, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(new[]
        {
            (360L, MidiMessageType.NoteOn, (byte)38, (byte)90),
            (600L, MidiMessageType.NoteOff, (byte)36, (byte)0),
            (600L, MidiMessageType.NoteOff, (byte)38, (byte)0)
        }, events.Where(value => value.Message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff)
            .Select(value => (value.Tick, value.Message.MessageType, value.Message.Byte1, value.Message.Byte2)));
        Assert.Equal(1, result.TotalNoteOnEventCount);
        Assert.Equal(events.LongLength, result.TotalEventCount);
        Assert.Contains(events, value => value.Tick == 300 && value.Role == CanonicalEventRole.RangeRestore
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 55);
        Assert.Single(events, value => value.Tick == 600 && value.Role == CanonicalEventRole.RootBoundaryCleanup
            && value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == 120);
        Assert.Contains(events, value => value.Tick == 600 && value.Role == CanonicalEventRole.RootBoundaryCleanup
            && value.Message.MessageType == MidiMessageType.ControlChange
            && value.Message.Byte1 == 11 && value.Message.Byte2 == 23);
        foreach (CanonicalMidiEvent noteOff in events.Where(value => value.Tick == 600
            && value.Message.MessageType == MidiMessageType.NoteOff))
        {
            PureMidiTrack owner = project.PureMidiTracks[noteOff.Message.Byte1 == 36 ? 0 : 1];
            Assert.Equal(owner.Id, noteOff.Source.TrackId);
            Assert.Equal(owner.Segments[0].Notes[0].Id, noteOff.Source.DirectMidiObjectId);
            Assert.Equal(SourceOrigin.CompilerBoundaryCleanup, noteOff.Source.Origin);
        }
    }

    private static CanonicalMidiEvent[] Events(CanonicalCompiledResult result) =>
        result.QueryEventPages(300, 600, includeStateAtStart: true).SelectMany(page => page.Items).ToArray();

    private void Dump(string layout, CanonicalCompiledResult result, CanonicalMidiEvent[] events)
    {
        output.WriteLine($"{layout}: declaredEvents={result.TotalEventCount}; declaredNoteOns={result.TotalNoteOnEventCount}; queriedEvents={events.Length}");
        foreach (CanonicalMidiEvent value in events)
            output.WriteLine($"tick={value.Tick}; port={value.ZeroBasedPort}; channel={value.ZeroBasedChannel}; "
                + $"message={value.Message.MessageType}/{value.Message.Byte1}/{value.Message.Byte2}; role={value.Role}; "
                + $"track={value.Source.TrackId}; note={value.Source.DirectMidiObjectId}; origin={value.Source.Origin}; "
                + $"order={value.StableOrder}; group={value.SemanticGroup}");
    }
}
