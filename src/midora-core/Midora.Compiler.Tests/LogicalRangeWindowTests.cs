using System.Reflection;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class LogicalRangeWindowTests
{
    private delegate CompactCanonicalStore ApplyRange(
        CompactCanonicalStore source, ReadOnlySpan<ChannelUnitAllocation> allocations,
        long start, long end, MidiInitialState defaults, CompilerStorageBudget budget,
        LogicalCanonicalPageIndex index, bool held, CancellationToken cancellation);

    private static readonly ApplyRange Range = typeof(MidoraCompiler)
        .GetMethod("ApplyRange", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<ApplyRange>();
    private static readonly IComparer<CanonicalMidiEvent> Comparer =
        (IComparer<CanonicalMidiEvent>)typeof(MidoraCompiler)
            .GetNestedType("CanonicalComparer", BindingFlags.NonPublic)!
            .GetProperty("Instance")!.GetValue(null)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RangeDoesNotDecodeUnrelatedSuffixAndPreservesColdStartAndHardEnd(bool held)
    {
        CompilerStorageBudget budget = new(0);
        using CompactCanonicalStore source = new(budget);
        for (int tick = 16383; tick >= 0; tick--)
            source.Add(Note(tick, 60, tick));
        source.Seal();
        // The first physical page contains only the far-future suffix. A range
        // ending at 50 must not touch it (ascending reads begin at the file tail).
        FileStream file = (FileStream)typeof(CompilerValueStore<CompactCanonicalEvent>)
            .GetField("_file", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(source.Values)!;
        RandomAccess.Write(file.SafeFileHandle, new byte[] { 0x93, 0x27, 0xff }, 0);
        using LogicalCanonicalPageIndex index = new(budget);
        using CompactCanonicalStore result = Range(source, [], 10, 50, new(), budget, index, held, default);
        LogicalCanonicalEventSource published = new(result, default, index);
        CanonicalMidiEvent[] output = published.EnumerateForPublication(default).ToArray();
        Assert.Equal(Enumerable.Range(10, 40).Select(t => (long)t), output
            .Where(e => e.Message.MessageType == MidiMessageType.NoteOn).Select(e => e.Tick));
        CanonicalMidiEvent[] offs = output.Where(e => e.Message.MessageType == MidiMessageType.NoteOff).ToArray();
        Assert.Equal(held ? 0 : 50, offs.Length);
        Assert.All(offs, e => { Assert.Equal(50, e.Tick); Assert.Equal(50, e.Source.Tick);
            Assert.Equal(SourceOrigin.CompilerBoundaryCleanup, e.Source.Origin); });
        Assert.Equal(held ? 40 : 91, output.Length);
        Assert.Throws<InvalidDataException>(() => source.Enumerate().First());
    }

    [Fact]
    public void OrdinalWindowExcludesInterleavedBoundaryStateButPreservesExactDirectEndpoints()
    {
        CompilerStorageBudget budget = new(0);
        using CompactCanonicalStore source = new(budget);
        CanonicalMidiEvent[] input =
        [
            Note(0, 60, 0), Note(0, 61, 1),
            new(5, 0, 0, MidiMessage.ControlChange(0, 11, 10), CanonicalEventRole.ControlChange,
                2, 0x1000b, 2, new(Tick: 5)),
            Direct(10, MidiMessage.NoteOff(0, 60, 73), 3),
            Direct(10, MidiMessage.ControlChange(0, 11, 99), 4),
            Direct(10, MidiMessage.NoteOff(0, 61, 42), 5),
            Note(11, 62, 6)
        ];
        Array.Sort(input, Comparer);
        foreach (CanonicalMidiEvent value in input.Reverse()) source.Add(value);
        source.Seal();
        using LogicalCanonicalPageIndex index = new(budget);
        ChannelUnitAllocation[] allocations = [new(default, default, default, default, default, default, 0, 20, 0, 0)];
        using CompactCanonicalStore result = Range(source, allocations, 0, 10, new(), budget, index, false, default);
        CanonicalMidiEvent[] actual = result.Enumerate(reverse: true).ToArray();
        Assert.Equal(new byte[] { 73, 42 }, actual.Where(e => e.Role == CanonicalEventRole.DirectMidi).Select(e => e.Message.Byte2));
        Assert.DoesNotContain(actual, e => e.Tick > 10 || e.Message.MessageType == MidiMessageType.ControlChange && e.Message.Byte2 == 99);
        Assert.DoesNotContain(actual, e => e.Message.MessageType == MidiMessageType.NoteOff && e.Role != CanonicalEventRole.DirectMidi);
        Assert.Equal(actual.OrderBy(e => e, Comparer), actual);
    }

    [Fact]
    public void RangeCancellationLeavesSealedSourceReadableAndOnlyReleasesItsNewIndex()
    {
        CompilerStorageBudget budget = new(0);
        using CompactCanonicalStore source = new(budget);
        source.Add(Note(0, 60, 0)); source.Seal();
        long metadata = budget.MetadataBytes, spill = budget.SpillBytes;
        using LogicalCanonicalPageIndex index = new(budget);
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => Range(source, [], 0, 10, new(), budget, index, false, cancel.Token));
        Assert.Equal(Note(0, 60, 0), source.Single());
        Assert.Equal(metadata, budget.MetadataBytes);
        Assert.Equal(spill, budget.SpillBytes);
    }

    private static CanonicalMidiEvent Note(long tick, byte key, long order) =>
        new(tick, 0, 0, MidiMessage.NoteOn(0, key, 100), CanonicalEventRole.NoteOn, order,
            long.MinValue, order, new(LogicalNoteId: new(order + 1), Tick: tick, Origin: SourceOrigin.TemplateEvent));
    private static CanonicalMidiEvent Direct(long tick, MidiMessage message, long order) =>
        new(tick, 0, 0, message, CanonicalEventRole.DirectMidi, order, long.MinValue, order,
            new(Tick: tick), ExportTrackId: new(100), SmfTrackOrder: 0, SmfEventOrder: order);
}
