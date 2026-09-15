using System.Reflection;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler.Tests;

public sealed class LogicalRawPatternRoundTripTests
{
    [Fact]
    public void EverySourceFieldAndRelativeGroupSentinelRoundTripsExactly()
    {
        object pool = CreatePool();
        SourceReference context = Context(100, long.MaxValue - 100);
        long[] boundary = [long.MinValue, long.MinValue + 1, long.MaxValue, -1, 0, 1];
        Array events = Array.CreateInstance(RawEventType, 64);
        for (int mask = 0; mask < events.Length; mask++)
        {
            SourceReference source = Source(context, mask) with { Tick = boundary[mask % boundary.Length] };
            events.SetValue(CreateEvent(boundary[(mask + 1) % boundary.Length], mask,
                boundary[(mask + 2) % boundary.Length], boundary[(mask + 3) % boundary.Length],
                boundary[mask % boundary.Length], source), mask);
        }
        // For group=MinValue+1 and base=1, the relative group itself equals
        // MinValue, but it is not the semantic no-group sentinel.
        object frozen = Freeze(pool, events, context, 1);
        Seal(pool);
        AssertRoundTrip(events, frozen);
    }

    [Fact]
    public void SharedPatternsRebaseAllInheritedIdsAndKeepExplicitDefaultIds()
    {
        object pool = CreatePool();
        SourceReference firstContext = Context(100, 900);
        SourceReference secondContext = Context(9000, 90_000);
        Array first = Events(firstContext, 1000);
        Array second = Events(secondContext, 90_000);
        object firstFrozen = Freeze(pool, first, firstContext, 1000);
        object secondFrozen = Freeze(pool, second, secondContext, 90_000);
        Assert.Same(Field(firstFrozen, "_events"), Field(secondFrozen, "_events"));
        Seal(pool);
        AssertRoundTrip(first, firstFrozen);
        AssertRoundTrip(second, secondFrozen);

        static Array Events(SourceReference context, long sequence)
        {
            Array values = Array.CreateInstance(RawEventType, 64);
            for (int mask = 0; mask < values.Length; mask++)
                values.SetValue(CreateEvent(context.Tick + mask, mask, sequence + mask, mask + 1000,
                    mask % 3 == 0 ? long.MinValue : sequence + mask / 3,
                    Source(context, mask) with { Tick = context.Tick - mask }), mask);
            return values;
        }
    }

    [Fact]
    public void VoiceStateAndTargetClosureAreRebuiltForTheChangedRevision()
    {
        var fixture = CompilerTestProject.Create(2);
        using var project = fixture.Project;
        fixture.Instrument.OverlapPolicy = OverlapPolicy.LetOverlap;
        fixture.Instrument.RequiresChannelIsolation = true;
        foreach (SubVoice voice in fixture.Instrument.SubVoices)
            voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        fixture.Voice.InitialState.Controllers[11] = 80;
        fixture.Instrument.SubVoices[1].InitialState.Controllers[11] = 70;
        TemplateEvent oldTarget = TemplateEvent.ControlChange(project, 10, 71, 30);
        fixture.Voice.Events.Add(oldTarget);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 0, 240);
        CompilerTestProject.AddNote(fixture.Segment, fixture.Instrument, 480, 240);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult before = compiler.CompileFull(project);
        Assert.True(before.IsConsumable);
        CanonicalMidiEvent[] frozenBefore = before.Events.ToArray();
        Assert.Contains(frozenBefore, value => IsControl(value, 71) && value.Role == CanonicalEventRole.Reset);
        Assert.Contains(frozenBefore, value => IsControl(value, 11, 80)
            && value.Source.SubVoiceId == fixture.Voice.Id);

        project.GlobalInitialState.BankMsb = 12;
        fixture.Instrument.InitialState.Program = 47;
        fixture.Voice.InitialState.Controllers[11] = 25;
        fixture.Voice.InitialState.Controllers[74] = 12;
        fixture.Voice.Events.Remove(oldTarget);
        fixture.Voice.Events.Add(TemplateEvent.ControlChange(project, 0, 74, 65));
        using MidoraCompiler reference = new();
        CanonicalCompiledResult incremental = compiler.CompileIncremental(project,
            new ProjectChangeSet { EventInstrumentIds = { fixture.Instrument.Id } });
        CanonicalCompiledResult full = reference.CompileFull(project);
        Assert.True(incremental.IsConsumable);
        Assert.Equal(full.Events.ToArray(), incremental.Events.ToArray());
        Assert.Equal(full.Fingerprint, incremental.Fingerprint);
        CanonicalMidiEvent[] changed = incremental.Events.ToArray();
        Assert.DoesNotContain(changed, value => IsControl(value, 71));
        Assert.Contains(changed, value => IsControl(value, 11, 25)
            && value.Source.SubVoiceId == fixture.Voice.Id);
        Assert.Contains(changed, value => IsControl(value, 11, 70)
            && value.Source.SubVoiceId == fixture.Instrument.SubVoices[1].Id);
        Assert.Contains(changed, value => IsControl(value, 74, 65) && value.Tick == 0);
        Assert.DoesNotContain(changed, value => IsControl(value, 74, 12));
        Assert.Contains(changed, value => IsControl(value, 0, 12));
        Assert.Contains(changed, value => value.Message.MessageType == MidiMessageType.ProgramChange && value.Message.Byte1 == 47);
        Assert.Equal(frozenBefore, before.Events.ToArray());
    }

    private static bool IsControl(CanonicalMidiEvent value, byte controller, byte? data = null) =>
        value.Message.MessageType == MidiMessageType.ControlChange && value.Message.Byte1 == controller
        && (data is null || value.Message.Byte2 == data);

    private static readonly Type RawEventType = typeof(MidoraCompiler).GetNestedType("RawMidiEvent", BindingFlags.NonPublic)!;
    private static readonly Type RawKindType = typeof(MidoraCompiler).GetNestedType("RawMessageKind", BindingFlags.NonPublic)!;
    private static object CreatePool() => Activator.CreateInstance(
        typeof(MidoraCompiler).GetNestedType("RawEventPatternPool", BindingFlags.NonPublic)!, nonPublic: true)!;
    private static object CreateEvent(long tick, int index, long sequence, long target, long group, SourceReference source) =>
        Activator.CreateInstance(RawEventType, tick, Enum.ToObject(RawKindType, index % 5),
            index + 1, index + 2, (CanonicalEventRole)(index % Enum.GetValues<CanonicalEventRole>().Length),
            sequence, target, group, source)!;
    private static object Freeze(object pool, Array values, SourceReference context, long sequence) =>
        pool.GetType().GetMethod("Freeze")!.Invoke(pool, [values, context, sequence, CancellationToken.None])!;
    private static void Seal(object pool) => pool.GetType().GetMethod("Seal")!.Invoke(pool, null);
    private static object Field(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void AssertRoundTrip(Array expected, object frozen)
    {
        Assert.Equal(expected.Length, frozen.GetType().GetProperty("Count")!.GetValue(frozen));
        PropertyInfo indexer = frozen.GetType().GetProperty("Item")!;
        for (int index = 0; index < expected.Length; index++)
            Assert.Equal(expected.GetValue(index), indexer.GetValue(frozen, [index]));
    }

    private static SourceReference Context(long firstId, long tick) => new(
        TrackId: new(firstId), SegmentId: new(firstId + 1), LogicalNoteId: new(firstId + 2),
        EventInstrumentId: new(firstId + 3), SubVoiceId: new(firstId + 4), Tick: tick,
        EventInstrumentUsageId: new(firstId + 5));

    private static SourceReference Source(SourceReference context, int inherited) => new(
        TrackId: (inherited & 1) != 0 ? context.TrackId : default,
        SegmentId: (inherited & 2) != 0 ? context.SegmentId : new(301),
        LogicalNoteId: (inherited & 4) != 0 ? context.LogicalNoteId : default,
        EventInstrumentId: (inherited & 8) != 0 ? context.EventInstrumentId : new(303),
        SubVoiceId: (inherited & 16) != 0 ? context.SubVoiceId : default,
        SourceEventId: new(305), Tick: -1, LogicalParameterId: new(306),
        LogicalParameterMappingId: new(307), MappingStepId: new(308), MappingFunctionId: new(309),
        ValueCurveId: new(310), EnvelopeId: new(311), Origin: SourceOrigin.LogicalParameterMapping,
        MidiChannelRootId: new(312), PureMidiTrackId: new(313), MidiSegmentId: new(314),
        DirectMidiObjectId: new(315), ExportTrackId: new(316),
        EventInstrumentUsageId: (inherited & 32) != 0 ? context.EventInstrumentUsageId : new(317));
}
