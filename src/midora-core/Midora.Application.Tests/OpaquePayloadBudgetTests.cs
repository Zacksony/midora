using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class OpaquePayloadBudgetTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public void NinetySixMiBIdLookupRetainsOnlyAddressesAndKeepsHotLookup()
    {
        var cache = new PureMidiOpaqueIdResolutionCache();
        var source = new PayloadSource(96, MiB);
        HashSet<MidoraId> ids = Enumerable.Range(0, 96).Select(source.IdAt).ToHashSet();
        Assert.Equal(96L * MiB, ReadPayloads(cache, source, ids));
        Assert.Equal(1, source.QueryCalls);
        Assert.Equal(96, cache.RetainedCount);
        Assert.Equal(0, cache.RetainedPayloadBytes);
        Assert.InRange(cache.RetainedBytes, 1, PureMidiSourceIdResolutionCache<OpaqueSourceAddress>.MaximumRetainedBytes);
        Collect();
        Assert.All(source.Allocations, reference => Assert.False(reference.TryGetTarget(out _)));
        Assert.Equal(96L * MiB, ReadPayloads(cache, source, ids));
        Assert.Equal(1, source.QueryCalls);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadPayloads(PureMidiOpaqueIdResolutionCache cache, PayloadSource source,
        IReadOnlySet<MidoraId> ids) => cache.Resolve(ids, source).Sum(match => (long)match.Value.Payload.Length);

    [Fact]
    public void OpaqueVirtualPagesRespectPayloadAndRecordLimitsWithoutLosingTail()
    {
        using var fixture = new Fixture();
        var source = new PayloadSource(96, MiB);
        var segment = new MidiSegment(fixture.Project);
        segment.AttachPagedContent(source);
        var objects = segment.OpaqueEvents.CreateObjectSource();
        int ordinal = 0, pages = 0;
        while (objects.TryGetPageByOrdinal(ordinal, 4096, out var page))
        {
            Assert.Equal(4, page.Count);
            Assert.Equal(4L * MiB, page.RetainedPayloadBytes);
            for (int i = 0; i < page.Count; i++) Assert.Equal(source.IdAt(ordinal + i), page.Values[i].Id);
            ordinal += page.Count;
            pages++;
        }
        Assert.Equal(96, ordinal);
        Assert.Equal(24, pages);
    }

    [Fact]
    public void SingleLargerThanPagePayloadIsDeliveredWhole()
    {
        using var fixture = new Fixture();
        var segment = new MidiSegment(fixture.Project);
        segment.AttachPagedContent(new PayloadSource(2, 5 * MiB));
        var objects = segment.OpaqueEvents.CreateObjectSource();
        Assert.True(objects.TryGetPageByOrdinal(0, 4096, out var first));
        Assert.Single(first.Values);
        Assert.Equal(5L * MiB, first.RetainedPayloadBytes);
        Assert.True(objects.TryGetPageByOrdinal(1, 4096, out var second));
        Assert.Single(second.Values);
        Assert.Equal(5L * MiB, second.RetainedPayloadBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpaqueSelectionBorrowsOnePayloadAndReleasesOnCancellationOrCompletion(bool cancel)
    {
        using var fixture = new Fixture();
        var source = new PayloadSource(96, MiB);
        var segment = new MidiSegment(fixture.Project);
        segment.AttachPagedContent(source);
        HashSet<MidoraId> ids = Enumerable.Range(0, 96).Select(source.IdAt).ToHashSet();
        using var cancellation = new CancellationTokenSource();
        using (var iterator = ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(fixture.Project,
            segment.OpaqueEvents.CreateObjectSource(), ids, cancellation.Token).GetEnumerator())
        {
            int count = 0;
            while (iterator.MoveNext())
            {
                Assert.Equal(MiB, fixture.Resources.BorrowedPayloadBytes);
                Assert.Equal(source.IdAt(count++), iterator.Current.Id);
                if (cancel)
                {
                    cancellation.Cancel();
                    Assert.ThrowsAny<OperationCanceledException>(() => iterator.MoveNext());
                    break;
                }
            }
            Assert.Equal(cancel ? 1 : 96, count);
        }
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
        Assert.Equal(MiB, fixture.Resources.PeakBorrowedPayloadBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void BorrowedSlicesCountActualBackingOnceAndDoNotCreateANewPayloadLimit()
    {
        using var fixture = new Fixture();
        byte[] backing = new byte[5 * MiB];
        using (var first = fixture.Resources.BorrowPayload(backing.AsMemory(0, 1)))
        {
            using (var second = fixture.Resources.BorrowPayload(backing.AsMemory(1, 1)))
                Assert.Equal(backing.Length, fixture.Resources.BorrowedPayloadBytes);
            Assert.Equal(backing.Length, fixture.Resources.BorrowedPayloadBytes);
        }
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
    }

    [Fact]
    public void PayloadBankCanRoundTripSingleEventLargerThanWorkingPageBudget()
    {
        using var fixture = new Fixture();
        byte[] expected = new byte[5 * MiB];
        expected[0] = 37; expected[^1] = 191;
        using var writer = new BoundedOpaqueMidiSource.PayloadWriter(0, BulkEditPreparationContext.Current!);
        Assert.Equal(0, writer.Add(expected));
        var bank = writer.Publish();
        Assert.NotNull(bank);
        try
        {
            Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
            Assert.True(expected.AsSpan().SequenceEqual(bank.Read(0).Span));
            Assert.Equal(5L * MiB, fixture.Resources.PeakBorrowedPayloadBytes);
            Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
            Assert.Equal(0, fixture.Resources.WorkingBytes);
        }
        finally { bank.Descriptors.Dispose(); bank.Blocks.Dispose(); }
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
    }

    [Fact]
    public void DenseOpaqueSelectionStreamsWithoutIdLookupOrRetainingPayloadPages()
    {
        using var fixture = new Fixture();
        var source = new PayloadSource(5_000, 64);
        var segment = new MidiSegment(fixture.Project);
        segment.AttachPagedContent(source);
        var ids = Enumerable.Range(0, source.OpaqueEventCount).Select(source.IdAt).ToHashSet();
        int count = 0;
        foreach (var value in ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(fixture.Project,
            segment.OpaqueEvents.CreateObjectSource(), ids))
        {
            Assert.Equal(source.IdAt(count++), value.Id);
            Assert.Equal(64, fixture.Resources.BorrowedPayloadBytes);
        }
        Assert.Equal(5_000, count);
        Assert.Equal(0, source.QueryCalls);
        Assert.Equal(64, fixture.Resources.PeakBorrowedPayloadBytes);
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
    }

    [Fact]
    public void OpaqueScalarAddressesPreserveRemovedReplacedAndAddedFormalSequence()
    {
        using var fixture = new Fixture();
        var source = new PayloadSource(6, 32);
        var segment = new MidiSegment(fixture.Project);
        segment.AttachPagedContent(source);
        var before = segment.OpaqueEvents.CreateObjectSource();
        segment.OpaqueEvents.RemoveAt(1);
        segment.OpaqueEvents[1].Tick = 900;
        var added = new OpaqueMidiEvent(fixture.Project)
        { Tick = 20, Kind = OpaqueMidiEventKind.SystemExclusive, Payload = [1, 2, 3], Order = 9 };
        segment.OpaqueEvents.Add(added);
        var after = segment.OpaqueEvents.CreateObjectSource();
        var ids = Enumerable.Range(0, 6).Select(source.IdAt).Append(added.Id).ToHashSet();
        var addresses = after.QueryAddressesByIds(ids).OrderBy(value => value.Index).ToArray();
        Assert.Equal(new[] { source.IdAt(0), source.IdAt(2), source.IdAt(3), source.IdAt(4), source.IdAt(5), added.Id },
            addresses.Select(value => value.Id));
        Assert.Equal(Enumerable.Range(0, 6), addresses.Select(value => value.Index));
        Assert.Equal(900, after.GetByOrdinal(1).Tick);
        Assert.Equal(source.IdAt(1), before.GetByOrdinal(1).Id);
        Assert.Equal(2, before.GetByOrdinal(2).Tick);
        Assert.Throws<ArgumentException>(() => ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(
            fixture.Project, after, ids).ToArray());
        var resolved = ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(fixture.Project, after,
            ids, requireAll: false).ToArray();
        Assert.Equal(addresses.Select(value => value.Id), resolved.Select(value => value.Id));
        Assert.Equal(new byte[] { 1, 2, 3 }, resolved[^1].Payload.ToArray());
    }

    [Fact]
    public void OpaqueDescriptorBudgetMeasuresBackingCapacityAtMaximumAdmission()
    {
        var source = new PayloadSource(65_536, 0);
        var cache = new PureMidiOpaqueIdResolutionCache();
        var ids = Enumerable.Range(0, source.OpaqueEventCount).Select(source.IdAt).ToHashSet();
        Assert.Equal(65_536, cache.ResolveAddresses(ids, source).Count);
        Assert.Equal(65_536, cache.RetainedCount);
        Assert.InRange(cache.RetainedBytes, 1, PureMidiSourceIdResolutionCache<OpaqueSourceAddress>.MaximumRetainedBytes);
        Assert.Equal(0, cache.RetainedPayloadBytes);
        var absent = new HashSet<MidoraId> { new(1_000_000) };
        Assert.Empty(cache.ResolveAddresses(absent, source));
        Assert.Equal(65_536, cache.RetainedCount);
    }

    [Fact]
    public void ReadOnlyPropertiesDoNotRetainFacadesOrPayloads()
    {
        using var fixture = new Fixture();
        var source = new PayloadSource(24, MiB);
        var segment = new MidiSegment(fixture.Project);
        segment.AttachPagedContent(source);
        var objects = segment.OpaqueEvents.CreateObjectSource();
        for (int ordinal = 0; ordinal < 24; ordinal++)
        {
            var properties = ProjectTimelineReadPreparation.ReadOpaqueProperties(fixture.Project, objects, source.IdAt(ordinal));
            Assert.NotNull(properties);
            Assert.Equal(MiB, properties.PayloadLength);
            Assert.Equal(513, properties.PayloadHexPreview.Length);
            Assert.Equal(ordinal, properties.Tick);
        }
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
        Assert.Equal(MiB, fixture.Resources.PeakBorrowedPayloadBytes);
        Collect();
        Assert.All(source.Allocations, reference => Assert.False(reference.TryGetTarget(out _)));
        Assert.True(segment.OpaqueEvents.IsPristinePagedSource);
    }

    [Fact]
    public void PayloadLruSharesImmutableArraysCountsCapacityAndSkipsOversizedRetention()
    {
        long identity = BoundedOpaquePayloadCache.AllocateIdentity();
        for (int index = 0; index < 10_000; index++)
            BoundedOpaquePayloadCache.Add(identity, index + 10_000, new byte[] { (byte)index });
        Assert.Equal(BoundedOpaquePayloadCache.MaximumEntries, BoundedOpaquePayloadCache.Snapshot.Entries);
        Assert.InRange(BoundedOpaquePayloadCache.Snapshot.RetainedBytes, 1, BoundedOpaquePayloadCache.MaximumBytes);
        for (int index = 0; index < 96; index++)
        {
            byte[] bytes = new byte[MiB];
            bytes[0] = (byte)index;
            BoundedOpaquePayloadCache.Add(identity, index, bytes.AsMemory(0, 16));
            Assert.True(BoundedOpaquePayloadCache.TryGet(identity, index, out var cached));
            Assert.True(MemoryMarshal.TryGetArray(cached, out var array));
            Assert.Same(bytes, array.Array);
            Assert.Equal((byte)index, cached.Span[0]);
            Assert.InRange(BoundedOpaquePayloadCache.Snapshot.RetainedBytes, MiB,
                BoundedOpaquePayloadCache.MaximumBytes);
        }
        Assert.False(BoundedOpaquePayloadCache.TryGet(identity, 0, out _));
        byte[] oversized = new byte[9 * MiB];
        BoundedOpaquePayloadCache.Add(identity, 100, oversized.AsMemory(0, 1));
        Assert.False(BoundedOpaquePayloadCache.TryGet(identity, 100, out _));
    }

    [Fact]
    public void ClipboardPayloadBytesSpillAndReleaseAfterLastLease()
    {
        using var fixture = new Fixture();
        ProjectObjectClipboardPayload payload;
        OpaqueClipboardList list;
        using (var capture = ClipboardCaptureScope.Enter())
        {
            var source = new PayloadSource(12, MiB);
            list = OpaqueClipboardList.Capture(Enumerable.Range(0, 12).Select(source.GetOpaqueEvent));
            Assert.Equal(12L * MiB, list.PayloadBytes);
            payload = new(new object(), ProjectObjectClipboardKind.OpaqueMidiEvents, 12,
                "12 events", new OpaqueMidiEventClipboardData(list));
        }
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.True(fixture.Resources.SpillBytes >= 12L * MiB);
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
        using (var iterator = list.GetEnumerator())
        {
            int count = 0;
            while (iterator.MoveNext())
            {
                Assert.Equal(MiB, fixture.Resources.BorrowedPayloadBytes);
                Assert.Equal((byte)count++, iterator.Current.Payload[0]);
            }
            Assert.Equal(12, count);
        }
        var lease = payload.AcquireStorageLease();
        payload.Dispose();
        Assert.True(fixture.Resources.SpillBytes > 0);
        lease.Dispose();
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
    }

    [Fact]
    public void RealPackSelectionClipboardAndRewritePreserveEveryOpaqueByte()
    {
        using var fixture = new Fixture();
        var generated = new PayloadSource(24, MiB);
        var segment = new MidiSegment(fixture.Project);
        string original = Path.Combine(fixture.Path, "original.mpk");
        using (var writer = new PureMidiContentPackWriter(original))
        {
            for (int index = 0; index < 24; index++) writer.AddOpaqueEvent(segment.Id, generated.GetOpaqueEvent(index));
            using var completed = writer.Complete();
        }
        using var pack = PureMidiContentPack.Open(original);
        segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
        var ids = Enumerable.Range(0, 24).Select(generated.IdAt).ToHashSet();
        using var capture = ClipboardCaptureScope.Enter();
        var values = OpaqueClipboardList.Capture(ProjectTimelineReadPreparation.ReadSelectedOpaqueValues(
            fixture.Project, segment.OpaqueEvents.CreateObjectSource(), ids));
        Assert.Equal(24L * MiB, values.PayloadBytes);
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
        string rewritten = Path.Combine(fixture.Path, "rewritten.mpk");
        using (var writer = new PureMidiContentPackWriter(rewritten))
        {
            int index = 0;
            foreach (var value in values)
                writer.AddOpaqueEvent(segment.Id, new(generated.IdAt(index++), value.Tick, value.Kind,
                    value.MetaType, value.Payload, value.Order));
            using var completed = writer.Complete();
        }
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(rewritten));
        Assert.InRange(fixture.Resources.PeakBorrowedPayloadBytes, MiB, 2L * MiB);
        Assert.InRange(pack.DecodedCacheByteCount, 0, pack.DecodedCacheByteLimit);
    }

    [Fact]
    public void CancelledOpaqueClipboardCaptureReleasesPayloadAndSpill()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        using var context = BulkEditPreparationContext.Enter(cancellation.Token, resources: fixture.Resources);
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            using var capture = ClipboardCaptureScope.Enter();
            _ = OpaqueClipboardList.Capture(Values());
        });
        Assert.Equal(0, fixture.Resources.BorrowedPayloadBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        IEnumerable<OpaqueMidiEventValue> Values()
        {
            var source = new PayloadSource(24, MiB);
            for (int index = 0; index < 24; index++)
            {
                if (index == 3) cancellation.Cancel();
                yield return source.GetOpaqueEvent(index);
            }
        }
    }

    private static void Collect()
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class PayloadSource(int count, int payloadLength) : IPureMidiSegmentContentSource
    {
        public int NoteCount => 0;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => count;
        public string ContentFingerprint => $"opaque-budget-{count}-{payloadLength}";
        public int QueryCalls { get; private set; }
        public List<WeakReference<byte[]>> Allocations { get; } = [];
        public MidoraId IdAt(int ordinal) => new(10_000 + ordinal);
        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
            byte[] payload = new byte[payloadLength];
            if (payloadLength != 0) payload[0] = (byte)index;
            Allocations.Add(new(payload));
            return new(IdAt(index), index, OpaqueMidiEventKind.Meta, 1, payload, index);
        }
        public IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(IReadOnlySet<MidoraId> ids)
        {
            QueryCalls++;
            foreach (MidoraId id in ids)
            {
                int index = FindOpaqueEventIndex(id);
                if (index >= 0) yield return new(index, GetOpaqueEvent(index));
            }
        }
        public int FindOpaqueEventIndex(MidoraId id) => id.Value >= 10_000 && id.Value < 10_000L + count
            ? (int)(id.Value - 10_000) : -1;
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) =>
            Enumerable.Range(0, count).Where(index => index >= startTick && index < endTick).Select(GetOpaqueEvent);
        public DirectMidiNoteValue GetNote(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public DirectMidiChannelEventValue GetChannelEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public int FindNoteIndex(MidoraId id) => -1;
        public int FindChannelEventIndex(MidoraId id) => -1;
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => [];
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(AppContext.BaseDirectory, ".tmp", "opaque-budget-" + Guid.NewGuid().ToString("N"));
        private readonly BulkEditPreparationContext _scope;
        public MidoraProject Project { get; } = new(480);
        public string Path => _path;
        public BoundedEditResources Resources { get; }
        public Fixture()
        {
            Directory.CreateDirectory(_path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 64 * 1024,
                maximumWorkingBytes: 4 * MiB), _path);
            _scope = BulkEditPreparationContext.Enter(resources: Resources, project: Project);
        }
        public void Dispose()
        { _scope.Dispose(); Project.Dispose(); Directory.Delete(_path, true); }
    }
}
