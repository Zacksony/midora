using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class CompiledOnionNoteIndexTests
{
    [Fact]
    public void UnchangedCompiledTrackKeepsItsFingerprintAndExtentAcrossOtherTrackEdits()
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        var first = Add(project, root, "First", 0, 10);
        var second = Add(project, root, "Second", 20, 5);
        second.Segments[0].Notes[0].Key = 61;
        using var compiler = new MidoraCompiler();
        using var original = CompiledOnionNoteIndex.Build(compiler.CompileFull(project));
        first.Segments[0].LengthTicks = 500;
        first.Segments[0].Notes[0].LengthTicks = 400;
        using var changed = CompiledOnionNoteIndex.Build(compiler.CompileFull(project));
        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
        Assert.NotEqual(original.GetTrackFingerprint(first.Id), changed.GetTrackFingerprint(first.Id));
        Assert.Equal(original.GetTrackFingerprint(second.Id), changed.GetTrackFingerprint(second.Id));
        Assert.Equal(25, changed.GetTrackEndTick(second.Id));
        Assert.Equal(400, changed.GetTrackEndTick(first.Id));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SameTickRawNotePairRemainsAZeroLengthDisplayNote(bool paged)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "onion-zero-note", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var project = new MidoraProject(192);
            var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
            var track = Add(project, root, "Track", 0, 10);
            var segment = track.Segments[0];
            segment.ChannelEvents.Add(new(project)
                { Tick = 20, Kind = DirectMidiChannelEventKind.NoteOn, Data1 = 27, Data2 = 3, Order = 100 });
            segment.ChannelEvents.Add(new(project)
                { Tick = 20, Kind = DirectMidiChannelEventKind.NoteOff, Data1 = 27, Data2 = 0, Order = 101 });
            using var writer = new PureMidiContentPackWriter(Path.Combine(directory, "source.mpk"));
            foreach (var note in segment.Notes)
                writer.AddNote(segment.Id, new(note.Id, note.StartTick, note.LengthTicks, note.Key,
                    note.NoteOnVelocity, note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder));
            foreach (var e in segment.ChannelEvents)
                writer.AddChannelEvent(segment.Id, new(e.Id, e.Tick, e.Kind, e.Data1, e.Data2, e.Order));
            using var pack = writer.Complete();
            if (paged) { segment.Notes.Clear(); segment.ChannelEvents.Clear(); segment.AttachPagedContent(pack.GetSegmentSource(segment.Id)); }
            using var compiler = new MidoraCompiler();
            var canonical = compiler.CompileFull(project);
            Assert.True(canonical.IsConsumable);
            Assert.Equal(2, canonical.TotalNoteOnEventCount);
            using var index = CompiledOnionNoteIndex.Build(canonical);
            Assert.Equal(2, index.Count);
            List<CompiledOnionNote> found = [];
            index.Visit(track.Id, 20, 21, 27, 27, found.Add);
            Assert.Equal(new(track.Id, 20, 20, 27), Assert.Single(found));
            found.Clear(); index.Visit(track.Id, 21, 22, 27, 27, found.Add); Assert.Empty(found);
            index.Visit(track.Id, 19, 20, 27, 27, found.Add); Assert.Empty(found);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void PagedAndMaterializedCanonicalAgreeAcrossWindowsSharedChannelsAndLogicalSources()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "onion-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var project = new MidoraProject(192);
            var root = new MidiChannelRoot(project) { Name = "Fixed", RoutingMode = MidiChannelRootRoutingMode.Fixed,
                FixedZeroBasedPort = 0, FixedZeroBasedChannel = 0 };
            project.MidiChannelRoots.Add(root);
            var a = Add(project, root, "A", 3060, 24);
            var b = Add(project, root, "B", 3070, 4);
            foreach (var track in new[] { a, b }) track.Segments[0].LengthTicks = 6500;
            a.Segments[0].Notes.Add(new(project) { StartTick = 6144, LengthTicks = 500, Key = 80, NoteOnVelocity = 90 });
            b.Segments[0].Notes.Add(new(project) { StartTick = 3072, LengthTicks = 1, Key = 81, NoteOnVelocity = 90 });
            var instrument = EventInstrumentLibrary.Create(project, "Logical");
            instrument.RootNote = 60;
            instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 48, 70, 100));
            var logical = new LogicalTrack(project) { Name = "Logical" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, logical, instrument.Id);
            var logicalSegment = new Segment(project) { LengthTicks = 6500 }; logical.Segments.Add(logicalSegment);
            logicalSegment.Notes.Add(new(project) { StartTick = 3072, LengthTicks = 48, Note = 62, Velocity = 100 });
            using var compiler = new MidoraCompiler();
            var materialized = compiler.CompileFull(project);
            Assert.True(materialized.IsConsumable);
            Assert.False(materialized.HasPagedEvents);
            using var expected = CompiledOnionNoteIndex.Build(materialized);
            using var logicalExpected = CompiledOnionNoteIndex.Build(materialized, scope: CompiledOnionNoteScope.LogicalTracks);
            Assert.Equal(1, logicalExpected.Count);
            Assert.Equal(logical.Id, Assert.Single(logicalExpected.TrackIds));
            using var writer = new PureMidiContentPackWriter(Path.Combine(directory, "source.mpk"));
            foreach (var track in new[] { a, b })
            foreach (var note in track.Segments[0].Notes)
                writer.AddNote(track.Segments[0].Id, new(note.Id, note.StartTick, note.LengthTicks,
                    note.Key, note.NoteOnVelocity, note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder));
            using var pack = writer.Complete();
            foreach (var track in new[] { a, b })
            { var segment = track.Segments[0]; segment.Notes.Clear(); segment.AttachPagedContent(pack.GetSegmentSource(segment.Id)); }
            var paged = compiler.CompileFull(project);
            Assert.True(paged.IsConsumable); Assert.True(paged.HasPagedEvents);
            using var actual = CompiledOnionNoteIndex.Build(paged);
            using var logicalActual = CompiledOnionNoteIndex.Build(paged, scope: CompiledOnionNoteScope.LogicalTracks);
            Assert.Equal(1, logicalActual.Count);
            Assert.Equal(logical.Id, Assert.Single(logicalActual.TrackIds));
            List<CompiledOnionNote> logicalNotes = [], expectedLogicalNotes = [];
            logicalActual.Visit(logical.Id, 0, 7000, 0, 127, logicalNotes.Add);
            logicalExpected.Visit(logical.Id, 0, 7000, 0, 127, expectedLogicalNotes.Add);
            Assert.Equal(expectedLogicalNotes, logicalNotes);
            Assert.Equal(new(logical.Id, 3072, 3120, 72), Assert.Single(logicalNotes));
            Assert.Equal(3120, logicalActual.EndTick);
            Assert.Equal(expected.Count, actual.Count);
            foreach (var track in project.ArrangementTracks)
            {
                List<CompiledOnionNote> left = [], right = [];
                expected.Visit(track.TrackId, 0, 7000, 0, 127, left.Add);
                actual.Visit(track.TrackId, 0, 7000, 0, 127, right.Add);
                Assert.Equal(left, right);
            }
            List<CompiledOnionNote> crossing = [];
            actual.Visit(a.Id, 3072, 3073, 60, 60, crossing.Add);
            Assert.Equal(new(a.Id, 3060, 3074, 60), Assert.Single(crossing));
            crossing.Clear(); actual.Visit(a.Id, 6499, 6501, 80, 80, crossing.Add);
            Assert.Equal(6500, Assert.Single(crossing).EndTick);

            // Cancellation while work is active must release every provisional spill file.
            using var canceled = new CancellationTokenSource();
            string spill = Path.Combine(directory, "cancel");
            Assert.Throws<OperationCanceledException>(() => CompiledOnionNoteIndex.Build(paged, canceled.Token, spill,
                progress => { if (progress.Stage == "Pairing canonical notes") canceled.Cancel(); }));
            Assert.Empty(Directory.Exists(spill) ? Directory.EnumerateFiles(spill, "*", SearchOption.AllDirectories) : []);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LogicalCompilationUsesEmittedPitchesAndRetainsSharedUsageTrackSources(bool isolated)
    {
        using var project = new MidoraProject(192);
        var instrument = EventInstrumentLibrary.Create(project, "Mapped");
        instrument.RequiresChannelIsolation = isolated;
        instrument.RootNote = 60;
        instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 48, 65, 100));
        var first = new LogicalTrack(project) { Name = "First" };
        var usage = ProjectGraphConstruction.AddIndependentLogicalTrack(project, first, instrument.Id);
        var second = new LogicalTrack(project) { Name = "Second" };
        ProjectGraphConstruction.AddLogicalTrack(project, second, usage);
        foreach (var (track, tick, key) in new[] { (first, 0L, 60), (second, 60L, 62) })
        {
            var segment = new Segment(project) { LengthTicks = 192 }; track.Segments.Add(segment);
            segment.Notes.Add(new(project) { StartTick = tick, LengthTicks = 48, Note = key, Velocity = 100 });
        }
        using var compiler = new MidoraCompiler(); var result = compiler.CompileFull(project);
        Assert.True(result.IsConsumable, string.Join("; ", result.Diagnostics));
        using var index = CompiledOnionNoteIndex.Build(result);
        List<CompiledOnionNote> notes = [];
        index.Visit(first.Id, 0, 192, 0, 127, notes.Add);
        Assert.Equal(new(first.Id, 0, 48, 65), Assert.Single(notes));
        notes.Clear(); index.Visit(second.Id, 0, 192, 0, 127, notes.Add);
        Assert.Equal(new(second.Id, 60, 108, 67), Assert.Single(notes));
    }
    [Fact]
    public void SharedChannelPairsFifoAndUsesNoteOnSourceTrack()
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Shared", RoutingMode = MidiChannelRootRoutingMode.Fixed,
            FixedZeroBasedPort = 0, FixedZeroBasedChannel = 0 };
        project.MidiChannelRoots.Add(root);
        var first = Add(project, root, "First", 0, 90);
        var second = Add(project, root, "Second", 10, 10);
        using var compiler = new MidoraCompiler();
        var canonical = compiler.CompileFull(project);
        Assert.True(canonical.IsConsumable, string.Join("; ", canonical.Diagnostics));
        using var index = CompiledOnionNoteIndex.Build(canonical);
        Assert.Equal(2, index.Count);
        using var logicalOnly = CompiledOnionNoteIndex.Build(canonical, scope: CompiledOnionNoteScope.LogicalTracks);
        Assert.Equal(0, logicalOnly.Count);
        Assert.Empty(logicalOnly.TrackIds);
        Assert.Equal(0, logicalOnly.EndTick);
        List<CompiledOnionNote> notes = [];
        index.Visit(first.Id, 15, 16, 0, 127, notes.Add);
        Assert.Equal(new(first.Id, 0, 20, 60), Assert.Single(notes));
        notes.Clear(); index.Visit(second.Id, 25, 30, 0, 127, notes.Add);
        Assert.Equal(new(second.Id, 10, 90, 60), Assert.Single(notes));
        notes.Clear(); index.Visit(first.Id, 20, 25, 0, 127, notes.Add); Assert.Empty(notes);
        index.Visit(second.Id, 15, 16, 61, 127, notes.Add); Assert.Empty(notes);
    }
    [Fact]
    public void FailedResultsAreRejectedAndCancellationReleasesProvisionalStorage()
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        Add(project, root, "Track", 0, 50);
        using var compiler = new MidoraCompiler();
        var result = compiler.CompileFull(project);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CompiledOnionNoteIndex.Build(result, cancellation.Token));
        using var index = CompiledOnionNoteIndex.Build(result);
        index.Dispose();
        Assert.Throws<OperationCanceledException>(() => index.Visit(project.PureMidiTracks[0].Id, 0, 100, 0, 127, _ => { }));
        root.FixedZeroBasedPort = 99;
        root.RoutingMode = MidiChannelRootRoutingMode.Fixed;
        var failed = compiler.CompileFull(project);
        Assert.False(failed.IsConsumable);
        Assert.Throws<ArgumentException>(() => CompiledOnionNoteIndex.Build(failed));
    }
    [Theory]
    [InlineData(4095)] [InlineData(4096)] [InlineData(4097)] [InlineData(18000)]
    public void PageBoundariesAndNotesStartingBeforeViewportRemainQueryable(int count)
    {
        using var project = new MidoraProject(192);
        var root = new MidiChannelRoot(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
        var track = Add(project, root, "Paged", 0, 1);
        var segment = track.Segments[0]; segment.Notes.Clear(); segment.LengthTicks = count * 3L + 10;
        for (int i = 0; i < count; i++) segment.Notes.Add(new(project)
            { StartTick = i * 3L, LengthTicks = 2, Key = i % 128, NoteOnVelocity = 90 });
        using var compiler = new MidoraCompiler(); var result = compiler.CompileFull(project);
        Assert.True(result.IsConsumable, string.Join("; ", result.Diagnostics));
        using var index = CompiledOnionNoteIndex.Build(result);
        Assert.Equal(count, index.Count);
        List<CompiledOnionNote> notes = [];
        long lastTick = (count - 1) * 3L;
        index.Visit(track.Id, lastTick + 1, lastTick + 2, 0, 127, notes.Add);
        Assert.Equal(new(track.Id, lastTick, lastTick + 2, (count - 1) % 128), Assert.Single(notes));
    }
    private static PureMidiTrack Add(MidoraProject project, MidiChannelRoot root, string name, long tick, long gate)
    {
        var track = new PureMidiTrack(project) { Name = name, MidiChannelRootId = root.Id };
        project.PureMidiTracks.Add(track); project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        var segment = new MidiSegment(project) { LengthTicks = 100 }; track.Segments.Add(segment);
        segment.Notes.Add(new(project) { StartTick = tick, LengthTicks = gate, Key = 60, NoteOnVelocity = 100 });
        return track;
    }
}
