using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class CompactCanonicalStorageTests
{
    [Fact]
    public void RecordSizesAreMeasuredWithoutRemovingAnyPublicField()
    {
        Assert.Equal(232, Unsafe.SizeOf<CanonicalMidiEvent>());
        Assert.Equal(160, Unsafe.SizeOf<SourceReference>());
        Assert.Equal(96, Unsafe.SizeOf<CompactCanonicalEvent>());
        Assert.Equal(32, Unsafe.SizeOf<CompactCanonicalSource>());
    }

    [Fact]
    public void HighEntropySourcesBeyondInternLimitAndReaderCapacityRoundTripEveryField()
    {
        CompilerStorageBudget budget = new(0);
        using CanonicalSourceTable table = new(budget, default);
        CompactCanonicalEvent[] values = new CompactCanonicalEvent[23001];
        for (int i = 0; i < values.Length; i++) values[i] = table.Compact(Create(i));
        table.Seal();
        Assert.Equal(values.Length, table.Count);
        Assert.True(table.SpillBytes > 0);
        using (var a = table.OpenReader())
        using (var b = table.OpenReader())
        {
            for (int i = 0; i < values.Length; i++)
                Assert.Equal(Create(i), a.Restore(values[i]));
            for (int i = 0; i < 200; i++)
            {
                int index = (i * 7919) % values.Length;
                Assert.Equal(Create(index), a.Restore(values[index]));
                Assert.Equal(Create(values.Length - index - 1), b.Restore(values[values.Length - index - 1]));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => a.Pattern(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => a.Pattern(table.Count));
        }
        table.Dispose();
        Assert.Equal(0, budget.ResidentBytes); Assert.Equal(0, budget.SpillBytes);
        Assert.Equal(0, budget.MetadataBytes);
        using var allWorking = budget.ReserveWorking(budget.MaximumWorkingBytes);
    }

    [Fact]
    public void MutableInlineFieldsDoNotConsumeColdEntriesAndOverlayKeepsParentImmutable()
    {
        CompilerStorageBudget budget = new(0);
        using CanonicalSourceTable parent = new(budget, default);
        SourceReference original = Create(0).Source;
        CompactCanonicalSource a = parent.Capture(original);
        CompactCanonicalSource b = parent.Capture(original with { Tick = -100, LogicalNoteId = Id(999), SourceEventId = Id(888) });
        Assert.Equal(a.Index, b.Index); parent.Seal();
        using CanonicalSourceTable overlay = new(budget, default, parent);
        SourceReference distinct = original with { Origin = SourceOrigin.CompilerBoundaryCleanup, EventInstrumentUsageId = Id(777) };
        CompactCanonicalSource c = overlay.Capture(distinct); overlay.Seal();
        using (var reader = overlay.OpenReader())
        {
            Assert.Equal(original, reader.Restore(a));
            Assert.Equal(-100, reader.Restore(b).Tick);
            Assert.Equal(distinct, reader.Restore(c));
        }
        overlay.Dispose();
        using var survivor = parent.OpenReader(); Assert.Equal(original, survivor.Restore(a));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactComparisonHasExactlyTheOriginalTieOrder(bool descending)
    {
        CompilerStorageBudget budget = new(0);
        using CanonicalSourceTable table = new(budget, default);
        List<CanonicalMidiEvent> all = [];
        CanonicalMidiEvent seed = Create(5) with { Tick = 100, StableOrder = 0 };
        all.Add(seed);
        foreach (FieldInfo field in typeof(SourceReference).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object boxed = seed.Source;
            if (field.FieldType == typeof(MidoraId)) field.SetValue(boxed, Id(1234567));
            else if (field.FieldType == typeof(long)) field.SetValue(boxed, -1L);
            else if (field.FieldType == typeof(SourceOrigin)) field.SetValue(boxed, SourceOrigin.CompilerBoundaryCleanup);
            all.Add(seed with { Source = (SourceReference)boxed });
        }
        all.Add(seed with { ExportTrackId = Id(1) });
        all.Add(seed with { SemanticTargetKey = -1 });
        all.Add(seed with { SemanticGroup = -1 });
        all.Add(seed with { Message = MidiMessage.NoteOff(5, 67, 23) });
        all.Add(seed with { SmfTrackOrder = 1 });
        all.Add(seed with { SmfEventOrder = 1 });
        all.Add(seed with { Role = CanonicalEventRole.NoteOn });
        all.Add(seed with { StableOrder = -1 });
        all.Add(seed with { Tick = 101 });
        CompactCanonicalEvent[] compact = all.Select(value => table.Compact(value)).ToArray();
        table.Seal();
        using var reader = table.OpenReader();
        Type rawType = typeof(MidoraCompiler).GetNestedType("CanonicalComparer", BindingFlags.NonPublic)!;
        IComparer<CanonicalMidiEvent> expected = (IComparer<CanonicalMidiEvent>)rawType.GetProperty("Instance")!.GetValue(null)!;
        Type newType = typeof(MidoraCompiler).GetNestedType("CompactCanonicalComparer", BindingFlags.NonPublic)!;
        var actual = (IComparer<CompactCanonicalEvent>)Activator.CreateInstance(newType, reader, descending)!;
        for (int i = 0; i < all.Count; i++) for (int j = 0; j < all.Count; j++)
            Assert.Equal(Math.Sign(descending ? expected.Compare(all[j], all[i]) : expected.Compare(all[i], all[j])),
                Math.Sign(actual.Compare(compact[i], compact[j])));
    }

    [Fact]
    public void SourceReaderCancellationAndAbortRemainIsolated()
    {
        CompilerStorageBudget budget = new(0);
        using CanonicalSourceTable table = new(budget, default);
        CompactCanonicalSource source = table.Capture(Create(1).Source); table.Seal();
        using CancellationTokenSource cancel = new();
        using (var cancelled = table.OpenReader(cancel.Token))
        { cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => cancelled.Restore(source)); }
        using var working = table.OpenReader(); Assert.Equal(Create(1).Source, working.Restore(source));
    }

    [Fact]
    public void SourceDiscoveryIndexStopsGrowingWithoutDroppingUnknownSources()
    {
        CompilerStorageBudget budget = new();
        using CanonicalSourceTable table = new(budget, default);
        CompactCanonicalSource first = default, last = default;
        for (int i = 0; i < CanonicalSourceTable.InternLimit + 17; i++)
        {
            last = table.Capture(Create(i).Source);
            if (i == 0) first = last;
        }
        var intern = (Dictionary<SourceReference, long>)typeof(CanonicalSourceTable)
            .GetField("_intern", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
        Assert.Equal(CanonicalSourceTable.InternLimit, intern.Count);
        long before = table.Count;
        Assert.Equal(first, table.Capture(Create(0).Source));
        CompactCanonicalSource uncached = table.Capture(Create(CanonicalSourceTable.InternLimit + 16).Source);
        Assert.Equal(before + 1, table.Count);
        Assert.NotEqual(last.Index, uncached.Index);
        table.Seal();
        using var reader = table.OpenReader();
        Assert.Equal(reader.Restore(last), reader.Restore(uncached));
    }

    [Fact]
    public void ReaderKeepsItsFinalizableSourceOwnerAliveWithoutExternalTableReference()
    {
        CompilerStorageBudget budget = new(0);
        using var reader = CreateReaderOnly(budget, out WeakReference owner, out CompactCanonicalSource source);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.True(owner.IsAlive);
        Assert.Equal(Create(7).Source, reader.Restore(source));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CanonicalSourceTable.Reader CreateReaderOnly(CompilerStorageBudget budget,
        out WeakReference owner, out CompactCanonicalSource source)
    {
        CanonicalSourceTable table = new(budget, default);
        source = table.Capture(Create(7).Source);
        table.Seal();
        owner = new(table);
        return table.OpenReader();
    }

    [Fact]
    public void OverlayRejectsMutableOrDisposedParentsAndReturnsWorkingReservation()
    {
        CompilerStorageBudget budget = new();
        using CanonicalSourceTable parent = new(budget, default);
        Assert.Throws<InvalidOperationException>(() => new CanonicalSourceTable(budget, default, parent));
        parent.Seal();
        parent.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new CanonicalSourceTable(budget, default, parent));
        Assert.Throws<ObjectDisposedException>(() => parent.OpenReader());
        Assert.Throws<ObjectDisposedException>(() => parent.Seal());
        using var all = budget.ReserveWorking(budget.MaximumWorkingBytes);
    }

    [Fact]
    public void ConstructorAndOverlayReaderBudgetFailuresDoNotLeakReservations()
    {
        CompilerStorageBudget denied = new(maximumWorkingBytes: CanonicalSourceTable.InternWorkingBytes - 1);
        Assert.Throws<InvalidOperationException>(() => new CanonicalSourceTable(denied, default));
        using (denied.ReserveWorking(denied.MaximumWorkingBytes)) { }

        CompilerStorageBudget budget = new();
        using CanonicalSourceTable parent = new(budget, default);
        parent.Seal();
        using CanonicalSourceTable overlay = new(budget, default, parent);
        overlay.Seal();
        // The overlay reader can reserve its own directory but not its parent's.
        using (budget.ReserveWorking(budget.MaximumWorkingBytes -
            CompilerValueStore<SourceReference>.RandomReader.DirectoryWorkingBytes))
            Assert.Throws<InvalidOperationException>(() => overlay.OpenReader());
        using var all = budget.ReserveWorking(budget.MaximumWorkingBytes);
    }

    [Fact]
    public void FailedSpillReadDoesNotPublishACacheSlotOrLeakWorkingMemory()
    {
        CompilerStorageBudget budget = new(0);
        using CompilerValueStore<long> store = new(budget);
        for (int i = 0; i < CompilerValueStore<long>.PageCapacity * 5; i++) store.Add(i);
        store.Seal();
        using (var reader = store.OpenRandomReader())
        {
            // No space for even the first decoded page. The same reader can
            // recover after the unrelated reservation is released.
            using (budget.ReserveWorking(budget.MaximumWorkingBytes -
                CompilerValueStore<long>.RandomReader.DirectoryWorkingBytes))
                Assert.Throws<InvalidOperationException>(() => reader.Read(0));
            Assert.Equal(0, reader.Read(0));
            var file = (FileStream)typeof(CompilerValueStore<long>)
                .GetField("_file", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            file.Position = 0;
            int original = file.ReadByte();
            file.Position = 0;
            file.WriteByte((byte)(original ^ 0x80));
            file.Flush();
            // Evict page 0 from the four-page reader cache; rereading must verify it.
            for (int page = 1; page <= 4; page++)
                Assert.Equal(page * CompilerValueStore<long>.PageCapacity,
                    reader.Read(page * CompilerValueStore<long>.PageCapacity));
            Assert.Throws<InvalidDataException>(() => reader.Read(0));
            Assert.Throws<InvalidDataException>(() => reader.Read(0));
            Assert.Equal(CompilerValueStore<long>.PageCapacity,
                reader.Read(CompilerValueStore<long>.PageCapacity));
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.Read(0));
        }
        using var all = budget.ReserveWorking(budget.MaximumWorkingBytes);
    }

    [Fact]
    public async Task SealedSourcesSupportIndependentConcurrentReaders()
    {
        CompilerStorageBudget budget = new(0);
        using CanonicalSourceTable table = new(budget, default);
        const int count = 9001;
        CompactCanonicalEvent[] values = new CompactCanonicalEvent[count];
        for (int i = 0; i < count; i++) values[i] = table.Compact(Create(i));
        table.Seal();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            using var reader = table.OpenReader();
            for (int i = 0; i < 2000; i++)
            {
                int index = (i * 199 + worker * 503) % count;
                Assert.Equal(Create(index), reader.Restore(values[index]));
            }
        })));
        table.Dispose();
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(0, budget.SpillBytes);
        Assert.Equal(0, budget.MetadataBytes);
        using var all = budget.ReserveWorking(budget.MaximumWorkingBytes);
    }

    private static MidoraId Id(long value) => MidoraId.FromSequence(value + 1);
    private static CanonicalMidiEvent Create(int i) => new(i * 7L, (byte)(i % 16), (byte)(i % 16),
        MidiMessage.NoteOn((byte)(i % 16), (byte)(i % 128), (byte)(1 + i % 127)),
        CanonicalEventRole.DirectMidi, i, i + 77, i + 88,
        new(TrackId: Id(i + 100), SegmentId: Id(i + 200), LogicalNoteId: Id(i + 300),
            EventInstrumentId: Id(i + 400), SubVoiceId: Id(i + 500), SourceEventId: Id(i + 600), Tick: -1 - i,
            LogicalParameterId: Id(i + 700), LogicalParameterMappingId: Id(i + 800), MappingStepId: Id(i + 900),
            MappingFunctionId: Id(i + 1000), ValueCurveId: Id(i + 1100), EnvelopeId: Id(i + 1200),
            Origin: SourceOrigin.TemplateEvent, MidiChannelRootId: Id(i + 1300), PureMidiTrackId: Id(i + 1400),
            MidiSegmentId: Id(i + 1500), DirectMidiObjectId: Id(i + 1600), ExportTrackId: Id(i + 1700),
            EventInstrumentUsageId: Id(i + 1800)), Id(i + 1900), i % 5, i + 55);
}
