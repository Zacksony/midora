using System.Reflection;
using Midora.Domain;

namespace Midora.Persistence.Tests;

public sealed class PureMidiReadOnlyPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveCopyAndReopenDoNotMaterializeBaseOrEditedRecords(bool edit)
    {
        using Fixture fixture = new(70_000);
        if (edit) fixture.Edit();
        MidiSegment source = fixture.Segment;
        var expectedNotes = source.Notes.EnumerateValues().ToArray();
        var expectedEvents = source.ChannelEvents.EnumerateValues().ToArray();
        var expectedOpaque = OpaqueValues(source).ToArray();
        long[] before = FacadeCounts(source);
        long highWater = fixture.Project.NextStableId;
        var generations = (source.Notes.Generation, source.ChannelEvents.Generation, source.OpaqueEvents.Generation);
        var metadata = fixture.Project.Metadata.Snapshot();
        var service = new MidoraProjectPackageV1("1.0.0-dev", new FixedClock());
        string first = fixture.PathFor("first.midora"), second = fixture.PathFor("second.midora");
        await service.SaveCopyAsync(fixture.Project, first);
        await service.SaveCopyAsync(fixture.Project, second);
        Assert.Equal(before, FacadeCounts(source));
        Assert.Equal(metadata, fixture.Project.Metadata.Snapshot());
        Assert.Equal(highWater, fixture.Project.NextStableId);
        Assert.Equal(generations, (source.Notes.Generation, source.ChannelEvents.Generation, source.OpaqueEvents.Generation));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        using var opened = await service.OpenAsync(first);
        Assert.False(opened.IsModified);
        Assert.Empty(opened.Diagnostics);
        Assert.Empty(opened.Project.DamagedPureMidiTracks);
        MidiSegment restored = Assert.Single(Assert.Single(opened.Project.PureMidiTracks).Segments);
        Assert.All(FacadeCounts(restored), count => Assert.Equal(0, count));
        Assert.Equal(expectedNotes, restored.Notes.EnumerateValues());
        Assert.Equal(expectedEvents, restored.ChannelEvents.EnumerateValues());
        Assert.Equal(expectedOpaque, OpaqueValues(restored));
        Assert.All(FacadeCounts(restored), count => Assert.Equal(0, count));
        Assert.Equal(source.ContentOffsetTick, restored.ContentOffsetTick);
        Assert.Equal(source.LengthTicks, restored.LengthTicks);
        // The first two notes and events intentionally occupy the same key/tick.
        Assert.Equal(expectedNotes[0].StartTick, expectedNotes[1].StartTick);
        Assert.Equal(expectedEvents[0].Tick, expectedEvents[1].Tick);
    }

    [Fact]
    public void MergedValueWriterIsByteIdenticalToTheEditableListReferenceWriter()
    {
        using Fixture fixture = new(65_537);
        fixture.Edit();
        string actualDirectory = fixture.PathFor("actual");
        long[] before = FacadeCounts(fixture.Segment);
        var entry = PureMidiContentPackPersistenceV1.Materialize(fixture.Track, actualDirectory, default);
        Assert.Equal(before, FacadeCounts(fixture.Segment));
        string expectedPath = fixture.PathFor("expected.mpk");
        using (PureMidiContentPackWriter writer = new(expectedPath))
        {
            foreach (DirectMidiNote note in fixture.Segment.Notes)
                writer.AddNote(fixture.Segment.Id, new(note.Id, note.StartTick, note.LengthTicks, note.Key,
                    note.NoteOnVelocity, note.NoteOffVelocity, note.NoteOnOrder, note.NoteOffOrder));
            foreach (DirectMidiChannelEvent value in fixture.Segment.ChannelEvents)
                writer.AddChannelEvent(fixture.Segment.Id, new(value.Id, value.Tick, value.Kind, value.Data1, value.Data2, value.Order));
            foreach (OpaqueMidiEvent value in fixture.Segment.OpaqueEvents)
                writer.AddOpaqueEvent(fixture.Segment.Id, new(value.Id, value.Tick, value.Kind, value.MetaType, value.Payload, value.Order));
            using var completed = writer.Complete();
        }
        Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(Path.Combine(actualDirectory, entry.Path)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task DuplicateIdentityOutsideExposedWindowStillFailsBeforePublishing(int kind)
    {
        using Fixture fixture = new(300, source => new InjectedSource(source, duplicateKind: kind));
        string target = fixture.PathFor("target.midora");
        byte[] original = [4, 3, 2, 1];
        File.WriteAllBytes(target, original);
        var error = await Assert.ThrowsAsync<MidoraPackageExceptionV1>(() => new MidoraProjectPackageV1("1.0.0-dev")
            .SaveProjectAsync(fixture.Project, target, overwriteAuthorized: true));
        Assert.Equal(MidoraPackageStageV1.Serialization, error.Stage);
        Assert.Contains("duplicated", error.InnerException!.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.All(FacadeCounts(fixture.Segment).Take(4), count => Assert.Equal(0, count));
    }

    [Fact]
    public async Task CancellationInsideReadonlyPreflightDoesNotPublishOrPolluteEditIndexes()
    {
        using CancellationTokenSource cancellation = new();
        InjectedSource? wrapped = null;
        using Fixture fixture = new(4000, source => wrapped = new InjectedSource(source, cancellation));
        string target = fixture.PathFor("target.midora");
        byte[] original = [1, 2, 3];
        File.WriteAllBytes(target, original);
        var metadata = fixture.Project.Metadata.Snapshot();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MidoraProjectPackageV1("1.0.0-dev")
            .SaveProjectAsync(fixture.Project, target, overwriteAuthorized: true, cancellationToken: cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(metadata, fixture.Project.Metadata.Snapshot());
        Assert.InRange(wrapped!.ReadCount, 1, 256);
        Assert.All(FacadeCounts(fixture.Segment), count => Assert.Equal(0, count));
        Assert.Empty(Directory.GetDirectories(fixture.Directory, ".midora-save-*"));
    }

    private static IEnumerable<(MidoraId, long, OpaqueMidiEventKind, byte, string, long)> OpaqueValues(MidiSegment segment) =>
        segment.OpaqueEvents.EnumerateValues().Select(v => (v.Id, v.Tick, v.Kind, v.MetaType, Convert.ToHexString(v.Payload.Span), v.Order));

    private static long[] FacadeCounts(MidiSegment segment) => new object[] { segment.Notes, segment.ChannelEvents, segment.OpaqueEvents }
        .SelectMany(collection => new[] { "_materializedSourceIds", "_materializedSourceValues", "_materializedSourceItems", "_sourceIndices" }
            .Select(name => collection.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(collection))
            .Select(value => value is null ? 0 : Convert.ToInt64(value.GetType().GetProperty("Count")!.GetValue(value))))
        .ToArray();

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly PureMidiContentPack pack;
        public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, ".tmp", "readonly-persistence", Guid.NewGuid().ToString("N"));
        public MidoraProject Project { get; } = new(480, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public PureMidiTrack Track { get; }
        public MidiSegment Segment { get; }
        public IPureMidiSegmentContentSource Source => pack.GetSegmentSource(Segment.Id);
        public Fixture(int count, Func<IPureMidiSegmentContentSource, IPureMidiSegmentContentSource>? wrap = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            MidiChannelRoot root = new(Project) { Name = "Root" };
            Track = new(Project) { Name = "Read-only persistence", MidiChannelRootId = root.Id };
            // Retain records on both sides of this exposed content window.
            Segment = new(Project) { ProjectStartTick = 100, ContentOffsetTick = 20, LengthTicks = 50 };
            Project.MidiChannelRoots.Add(root);
            Project.PureMidiTracks.Add(Track);
            Project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, Track.Id));
            Track.Segments.Add(Segment);
            using PureMidiContentPackWriter writer = new(PathFor("base.mpk"));
            for (int i = 0; i < count; i++)
            {
                writer.AddNote(Segment.Id, new(Project.AllocateStableId(), i / 2, 7, 60, 100, i % 128, i * 2L + 1, i * 2L));
                writer.AddChannelEvent(Segment.Id, new(Project.AllocateStableId(), i / 2,
                    DirectMidiChannelEventKind.ControlChange, 11, i % 128, count - i));
                if (i < 300) writer.AddOpaqueEvent(Segment.Id, new(Project.AllocateStableId(), i,
                    i % 2 == 0 ? OpaqueMidiEventKind.Meta : OpaqueMidiEventKind.SystemExclusive,
                    i % 2 == 0 ? (byte)1 : (byte)0, new byte[] { 0x41, 0xf7 }, count - i));
            }
            pack = writer.Complete();
            Segment.AttachPagedContent(wrap is null ? Source : wrap(Source));
        }
        public string PathFor(string name) => Path.Combine(Directory, name);
        public void Edit()
        {
            Segment.Notes[12].StartTick = 999_999;
            Segment.Notes[12].NoteOffVelocity = 123;
            Segment.ChannelEvents[12].Tick = 999_999;
            Segment.OpaqueEvents[12].Payload = [5, 7, 0xf7];
            Segment.Notes.RemoveAt(20); Segment.ChannelEvents.RemoveAt(20); Segment.OpaqueEvents.RemoveAt(20);
            Segment.Notes.Add(new(Project) { StartTick = 3, LengthTicks = 999, Key = 65 });
            Segment.ChannelEvents.Add(new(Project) { Tick = 3, Kind = DirectMidiChannelEventKind.ControlChange, Data1 = 93, Data2 = 127 });
            Segment.OpaqueEvents.Add(new(Project) { Tick = 3, Kind = OpaqueMidiEventKind.Meta, MetaType = 1, Payload = [9, 8] });
        }
        public void Dispose()
        {
            Project.Dispose();
            pack.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class InjectedSource(IPureMidiSegmentContentSource source, CancellationTokenSource? cancellation = null, int duplicateKind = -1) : IPureMidiSegmentContentSource
    {
        public int ReadCount { get; private set; }
        public int NoteCount => source.NoteCount;
        public int ChannelEventCount => source.ChannelEventCount;
        public int OpaqueEventCount => source.OpaqueEventCount;
        public string ContentFingerprint => source.ContentFingerprint;
        public DirectMidiNoteValue GetNote(int index)
        {
            ReadCount++; cancellation?.Cancel();
            var value = source.GetNote(index);
            return duplicateKind == 0 && index == NoteCount - 1 ? value with { Id = source.GetNote(0).Id, StartTick = 1_000_000 } : value;
        }
        public DirectMidiChannelEventValue GetChannelEvent(int index)
        {
            var value = source.GetChannelEvent(index);
            return duplicateKind == 1 && index == ChannelEventCount - 1 ? value with { Id = source.GetNote(0).Id, Tick = 1_000_000 } : value;
        }
        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            var value = source.GetOpaqueEvent(index);
            return duplicateKind == 2 && index == OpaqueEventCount - 1 ? value with { Id = source.GetNote(0).Id, Tick = 1_000_000 } : value;
        }
        public int FindNoteIndex(MidoraId id) => source.FindNoteIndex(id);
        public int FindChannelEventIndex(MidoraId id) => source.FindChannelEventIndex(id);
        public int FindOpaqueEventIndex(MidoraId id) => source.FindOpaqueEventIndex(id);
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => source.QueryNotes(startTick, endTick, minimumKey, maximumKey);
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => source.QueryChannelEvents(startTick, endTick);
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) => source.QueryOpaqueEvents(startTick, endTick);
    }
}
