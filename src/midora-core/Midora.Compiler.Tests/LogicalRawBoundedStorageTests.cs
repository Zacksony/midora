using System.Collections;
using System.Reflection;
using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class LogicalRawBoundedStorageTests
{
    [Theory]
    [InlineData(63)]
    [InlineData(16_385)]
    public void RawSequencesRoundTripUnderZeroResidentBudget(int count)
    {
        CompilerStorageBudget budget = new(maximumResidentBytes: 0);
        object pool = Pool(budget);
        SourceReference context = new(TrackId: new(1), SegmentId: new(2), LogicalNoteId: new(3),
            EventInstrumentId: new(4), SubVoiceId: new(5), Tick: 900,
            EventInstrumentUsageId: new(6));
        Array events = Array.CreateInstance(RawEventType, count);
        for (int index = 0; index < count; index++)
        {
            SourceReference source = context with
            {
                SourceEventId = new(70 + index % 3),
                Tick = context.Tick + index,
                LogicalParameterId = new(90),
                Origin = SourceOrigin.TemplateEvent
            };
            events.SetValue(Event(context.Tick + index, index, index / 4, 11, index % 128, source), index);
        }
        object sequence = pool.GetType().GetMethod("Freeze")!.Invoke(pool,
            [events, context, 0L, CancellationToken.None])!;
        pool.GetType().GetMethod("Seal")!.Invoke(pool, null);
        Assert.True(budget.SpillBytes > 0);
        Assert.Equal(0, budget.ResidentBytes);
        Assert.Equal(events.Cast<object>(), ((IEnumerable)sequence).Cast<object>());
        Assert.Equal(events.Cast<object>(), ((IEnumerable)sequence).Cast<object>());
    }

    [Fact]
    public void RawBufferCanReadPrefixBeforeContinuingAndSealing()
    {
        CompilerStorageBudget budget = new(maximumResidentBytes: 0);
        object pool = Pool(budget);
        SourceReference context = new(Tick: 0);
        object buffer = CreateBuffer(pool, context);
        MethodInfo add = buffer.GetType().GetMethod("Add")!;
        List<object> expected = [];
        using ((IDisposable)buffer)
        {
            for (int i = 0; i < 8200; i++)
            {
                object value = Event(i, i, i, 11, i % 128, context with { Tick = i });
                add.Invoke(buffer, [value]);
                expected.Add(value);
            }
            Assert.Equal(expected, ((IEnumerable)buffer).Cast<object>());
            object last = Event(9000, 9000, 9000, 11, 25, context with { Tick = 9000 });
            add.Invoke(buffer, [last]);
            expected.Add(last);
            object frozen = buffer.GetType().GetMethod("Freeze")!.Invoke(buffer, null)!;
            pool.GetType().GetMethod("Seal")!.Invoke(pool, null);
            Assert.Equal(expected, ((IEnumerable)frozen).Cast<object>());
        }
    }

    [Fact]
    public void RepeatedLargePatternsStillShareOneBoundedResidentPattern()
    {
        CompilerStorageBudget budget = new();
        object pool = Pool(budget);
        SourceReference context = new(Tick: 0);
        object? first = null;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            object buffer = CreateBuffer(pool, context);
            using ((IDisposable)buffer)
            {
                MethodInfo add = buffer.GetType().GetMethod("Add")!;
                for (int index = 0; index < 8200; index++)
                    add.Invoke(buffer, [Event(index, index, index, 11, index % 128, context with { Tick = index })]);
                object sequence = buffer.GetType().GetMethod("Freeze")!.Invoke(buffer, null)!;
                object records = sequence.GetType().GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(sequence)!;
                if (first is null) first = records;
                else Assert.Same(first, records);
            }
        }
        pool.GetType().GetMethod("Seal")!.Invoke(pool, null);
        Assert.InRange(budget.ResidentBytes, 1, 1L << 20);
        Assert.Equal(0, budget.SpillBytes);
    }

    [Fact]
    public void BoundedTargetStateGroupingPreservesFirstTickLastValueAndStableOrder()
    {
        CompilerStorageBudget budget = new(maximumResidentBytes: 0);
        object pool = Pool(budget);
        SourceReference context = new(Tick: 0);
        object buffer = CreateBuffer(pool, context);
        MethodInfo add = buffer.GetType().GetMethod("Add")!;
        List<(long Tick, long Sequence, long Group, int Value, int Ordinal)> source = [];
        using ((IDisposable)buffer)
        {
            for (int i = 0; i < 8400; i++)
            {
                long group = i % 4200;
                // The same group is deliberately non-contiguous in emission
                // order. The bounded reducer must match the old GroupBy path.
                long tick = (i % 37) * 3;
                int value = i % 128;
                source.Add((tick, i, group, value, i));
                add.Invoke(buffer, [Event(tick, i, group, 11, value, context with { Tick = tick })]);
            }
            object timeline = typeof(MidoraCompiler).GetMethod("BuildBoundedTargetStateTimeline",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [buffer, MidiValueTarget.ControlChange(11)])!;
            using ((IDisposable)timeline)
            {
                (long Tick, double Value)[] expected = source.GroupBy(value => value.Group)
                    .Select(group => (Tick: group.First().Tick, Value: (double)group.Last().Value,
                        Sequence: group.Max(value => value.Sequence), Ordinal: group.First().Ordinal))
                    .OrderBy(value => value.Tick).ThenBy(value => value.Sequence).ThenBy(value => value.Ordinal)
                    .Select(value => (value.Tick, value.Value)).ToArray();
                var actual = ((IEnumerable)timeline).Cast<object>().Select(value => (
                    (long)value.GetType().GetProperty("Tick")!.GetValue(value)!,
                    (double)value.GetType().GetProperty("Value")!.GetValue(value)!)).ToArray();
                Assert.Equal(expected, actual);
            }
        }
    }

    private static readonly Type PoolType = typeof(MidoraCompiler).GetNestedType("RawEventPatternPool", BindingFlags.NonPublic)!;
    private static readonly Type RawEventType = typeof(MidoraCompiler).GetNestedType("RawMidiEvent", BindingFlags.NonPublic)!;
    private static readonly Type RawKindType = typeof(MidoraCompiler).GetNestedType("RawMessageKind", BindingFlags.NonPublic)!;
    private static object Pool(CompilerStorageBudget budget) => Activator.CreateInstance(PoolType, [budget])!;
    private static object CreateBuffer(object pool, SourceReference context) => PoolType.GetMethod("CreateBuffer")!
        .Invoke(pool, [context, 0L, CancellationToken.None])!;
    private static object Event(long tick, long sequence, long group, int controller, int value, SourceReference source)
    {
        long target = (long)typeof(MidoraCompiler).GetMethod("SemanticTargetForTarget", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [MidiValueTarget.ControlChange(controller)])!;
        return Activator.CreateInstance(RawEventType, tick, Enum.Parse(RawKindType, "ControlChange"), controller,
            value, CanonicalEventRole.ControlChange, sequence, target, group, source)!;
    }
}
