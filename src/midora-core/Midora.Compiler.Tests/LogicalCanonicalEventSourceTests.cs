using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class LogicalCanonicalEventSourceTests
{
    [Fact]
    public void PublicationMetadataExhaustionDoesNotInvalidateTheOldResultOrLeakReservations()
    {
        CompilerStorageBudget oldBudget = new(0), newBudget = new(0);
        using CompactCanonicalStore oldValues = new(oldBudget), newValues = new(newBudget);
        for (int i = 4999; i >= 0; i--)
        {
            CanonicalMidiEvent item = new(i, 0, 0, MidiMessage.NoteOn(0, 60, 100),
                CanonicalEventRole.NoteOn, i, long.MinValue, i, new(Tick: i));
            oldValues.Add(item); newValues.Add(item);
        }
        oldValues.Seal(); newValues.Seal();
        LogicalCanonicalEventSource oldSource = new(oldValues, default);
        long oldMetadata = oldBudget.MetadataBytes;
        long occupied = newBudget.MaximumMetadataBytes - newBudget.MetadataBytes;
        newBudget.ReserveMetadata(occupied);
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => new LogicalCanonicalEventSource(newValues, default));
        Assert.Contains("metadata-memory budget", failure.Message);
        newBudget.ReleaseMetadata(occupied);
        newBudget.Abort();
        Assert.Equal(0, newBudget.MetadataBytes);
        Assert.Equal(oldMetadata, oldBudget.MetadataBytes);
        Assert.Equal(Enumerable.Range(0, 5000).Select(i => (long)i), oldSource.Enumerate(0, 5000, false).Select(e => e.Tick));
        oldValues.Dispose();
        Assert.Equal(0, oldBudget.MetadataBytes);
    }

    [Fact]
    public void CancelledPublicationReleasesOnlyItsNewIndexReservation()
    {
        CompilerStorageBudget budget = new(0);
        using CompactCanonicalStore values = new(budget);
        values.Add(new(10, 0, 0, MidiMessage.NoteOn(0, 60, 100), CanonicalEventRole.NoteOn, 0, long.MinValue, 0, new(Tick: 10)));
        values.Seal();
        long metadata = budget.MetadataBytes;
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LogicalCanonicalEventSource(values, cancellation.Token));
        Assert.Equal(metadata, budget.MetadataBytes);
        Assert.Single(values.Enumerate());
        values.Dispose(); Assert.Equal(0, budget.MetadataBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(2097152)]
    public void PublicationUsesFreedResidentBudgetWithoutLosingTheSpilledRemainder(long residentBudget)
    {
        CompilerStorageBudget budget = new(residentBudget);
        Assert.True(budget.TryReserveResident(residentBudget));
        using CompactCanonicalStore values = new(budget);
        for (int i = 4999; i >= 0; i--)
            values.Add(new(i, 0, 0, MidiMessage.NoteOn(0, 60, 100),
                CanonicalEventRole.NoteOn, i, long.MinValue, i, new(Tick: i)));
        values.Seal();
        Assert.Equal(0, values.ResidentBytes);
        Assert.True(values.SpillBytes > 0);
        budget.ReleaseResident(residentBudget);
        LogicalCanonicalEventSource source = new(values, default);
        Assert.InRange(budget.ResidentBytes, 0, residentBudget);
        Assert.Equal(5000, source.NoteOnEventCount);
        Assert.Equal(Enumerable.Range(0, 5000).Select(i => (long)i), source.Enumerate(0, 5000, false).Select(e => e.Tick));
        Assert.Equal(new long[] { 4095, 4096, 4097 }, source.Enumerate(4095, 4098, false).Select(e => e.Tick));
        if (residentBudget == 2097152) Assert.Equal(0, values.SpillBytes);
        else Assert.True(values.SpillBytes > 0);
        values.Dispose();
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(0, budget.SpillBytes);
    }

    [Theory]
    [InlineData(4095, false)]
    [InlineData(4096, false)]
    [InlineData(4097, false)]
    [InlineData(8201, false)]
    [InlineData(4095, true)]
    [InlineData(4096, true)]
    [InlineData(4097, true)]
    [InlineData(8201, true)]
    public void SameTickAcrossPhysicalPagesPreservesEveryEventAndExactUnitWindows(int count, bool prepared)
    {
        CanonicalMidiEvent[] expected = Enumerable.Range(0, count).Select(Create).ToArray();
        CompilerStorageBudget budget = new(0);
        using CompactCanonicalStore values = new(budget);
        using LogicalCanonicalPageIndex index = new(budget);
        foreach (CanonicalMidiEvent value in expected.Reverse()) { values.Add(value); index.Add(in value); }
        index.SealPage();
        values.Seal();
        LogicalCanonicalEventSource source = new(values, default, prepared ? index : null);
        Assert.Equal(count, source.EventCount);
        Assert.Equal(count, source.NoteOnEventCount);
        Assert.Equal(expected, source.Enumerate(0, 30, includeEnd: true));
        Assert.Equal(expected, source.EnumerateForPublication(default));
        Assert.Equal(expected.Where(item => item.Tick < 30), source.Enumerate(0, 30, includeEnd: false));
        Assert.Equal(expected.Where(item => item.Tick == 20), source.Enumerate(20, 21, includeEnd: false));
        Assert.Equal(expected.Where(item => item.Tick <= 20), source.Enumerate(0, 20, includeEnd: true));
        Assert.Empty(source.Enumerate(31, 100, includeEnd: true));
        foreach (int unit in new[] { 0, 63, 64, 127, 128, 191, 192, 255 })
        {
            byte port = (byte)(unit / 16), channel = (byte)(unit % 16);
            var expectedUnit = expected.Where(item => item.ZeroBasedPort == port && item.ZeroBasedChannel == channel && item.Tick == 20);
            Assert.Equal(expectedUnit, source.EnumerateUnit(port, channel, 20, 21, includeEnd: false));
        }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Enumerate(0, 30, true, cancellation.Token).ToArray());

        CanonicalMidiEvent Create(int index)
        {
            long tick = index == 0 ? 10 : index == count - 1 ? 30 : 20;
            byte port = (byte)(index % 256 / 16), channel = (byte)(index % 16);
            SourceReference source = new(TrackId: MidoraId.FromSequence(1), SegmentId: MidoraId.FromSequence(2),
                LogicalNoteId: MidoraId.FromSequence(100 + index), Tick: tick, Origin: SourceOrigin.TemplateEvent);
            return new(tick, port, channel, MidiMessage.NoteOn(channel, (byte)(index % 128), 100),
                CanonicalEventRole.NoteOn, index, long.MinValue, index, source);
        }
    }
}
