using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand SetInitialInstrument(MidoraId instrumentId, MidoraId? subVoiceId,
        InstrumentSelectionValues values) => Command("Change initial instrument", project =>
    {
        values.Validate();
        var instrument = FindEventInstrument(project, instrumentId);
        var state = subVoiceId is { } id ? FindSubVoice(instrument, id).InitialState : instrument.InitialState;
        var previous = InstrumentSelectionValues.From(state);
        return Prepared(previous != values, EventInstrumentChange(instrumentId),
            _ => values.ApplyTo(state), _ => previous.ApplyTo(state));
    });

    public static IProjectEditCommand SetMidiInstrumentChange(MidoraId segmentId, long tick,
        InstrumentAddress address, MidoraId? changeId = null)
    {
        address.Validate();
        if (tick < 0 || tick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(tick));
        if (changeId is { } existingId)
            return Command("Change instrument", project =>
            {
                var owner = FindMidiSegment(project, segmentId).Segment;
                if (!owner.InstrumentChanges.TryGet(existingId, out var group)
                    || !InstrumentChangeResolver.TryRead(owner, group, out _))
                    throw new InvalidOperationException("The instrument change no longer exists.");
                return PrepareBoundedDirectMidiEventTransform(project, segmentId, group.MemberIds.ToArray(),
                    _ => value => value with { Tick = tick, Data1 = value.Kind == DirectMidiChannelEventKind.ProgramChange
                        ? address.Program : value.Data1, Data2 = value.Kind == DirectMidiChannelEventKind.ProgramChange
                            ? 0 : value.Data1 == 0 ? address.BankMsb : address.BankLsb });
            });

        return new SequentialProjectEditCommand("Create instrument change",
        [
            _ => ReserveInstrumentChangeOrder(segmentId, tick),
            _ => Command("Set Bank MSB", project => PrepareBoundedDirectMidiEventLine(project, segmentId,
                DirectMidiChannelEventKind.ControlChange, 0, [new(tick, 0, address.BankMsb)])),
            _ => Command("Set Bank LSB", project => PrepareBoundedDirectMidiEventLine(project, segmentId,
                DirectMidiChannelEventKind.ControlChange, 32, [new(tick, 32, address.BankLsb)])),
            _ => Command("Set Program", project => PrepareBoundedDirectMidiEventLine(project, segmentId,
                DirectMidiChannelEventKind.ProgramChange, 0, [new(tick, address.Program, 0)])),
            _ => Command("Order instrument members", project =>
            {
                var members = ResolveMidiInstrumentMembers(FindMidiSegment(project, segmentId).Segment, tick);
                return PrepareBoundedDirectMidiEventTransform(project, segmentId, members,
                    _ => value => value with { Order = value.Kind == DirectMidiChannelEventKind.ProgramChange
                        ? 2 : value.Data1 == 0 ? 0 : 1 }, BoundedEventCollisionMode.None);
            }),
            _ => Command("Link instrument members", project =>
            {
                var location = FindMidiSegment(project, segmentId);
                var members = ResolveMidiInstrumentMembers(location.Segment, tick);
                var previous = location.Segment.InstrumentChanges;
                InstrumentChange? created = null;
                return Prepared(true, PureMidiTrackChange(location.Track.Id), owner =>
                {
                    var groups = previous;
                    foreach (var member in members)
                        if (groups.TryGetByMember(member, out var old)) groups = groups.Remove(old.Id);
                    created ??= new(owner.AllocateStableId(), members[0], members[1], members[2]);
                    location.Segment.InstrumentChanges = groups.Add(created.Value, true)
                        .ValidatedAt(location.Segment.ChannelEvents.Generation);
                }, _ => location.Segment.InstrumentChanges = previous);
            })
        ]);
    }

    private static MidoraId[] ResolveMidiInstrumentMembers(MidiSegment owner, long tick)
    {
        var keys = new HashSet<DirectMidiEventStartKey>
        {
            new(tick, DirectMidiChannelEventKind.ControlChange, 0),
            new(tick, DirectMidiChannelEventKind.ControlChange, 32),
            new(tick, DirectMidiChannelEventKind.ProgramChange, 0)
        };
        MidoraId[] result = new MidoraId[3];
        foreach (var value in owner.ChannelEvents.QueryStartKeys(keys))
        {
            int index = value.Kind == DirectMidiChannelEventKind.ProgramChange ? 2 : value.Data1 == 0 ? 0 : 1;
            if (result[index] != default) throw new InvalidOperationException("Instrument member collision was not resolved.");
            result[index] = value.Id;
        }
        if (result.Any(static id => id == default)) throw new InvalidOperationException("An instrument change is incomplete.");
        return result;
    }

    public static IProjectEditCommand SetSubVoiceInstrumentChange(MidoraId instrumentId, MidoraId voiceId,
        long tick, InstrumentAddress address, MidoraId? changeId = null)
    {
        address.Validate();
        if (tick < 0 || tick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(tick));
        return Command(changeId.HasValue ? "Change instrument" : "Create instrument change", original =>
        {
        var originalVoice = FindSubVoice(FindEventInstrument(original, instrumentId), voiceId);
        InstrumentChange? originalGroup = null;
        if (changeId is { } existingId)
        {
            if (!originalVoice.InstrumentChanges.TryGet(existingId, out var found)
                || !InstrumentChangeResolver.TryRead(originalVoice, found, out _))
                throw new InvalidOperationException("The instrument change no longer exists.");
            originalGroup = found;
        }
        MidoraId bankId = originalGroup?.BankEventId ?? default;
        MidoraId programId = originalGroup?.ProgramEventId ?? default;
        return new SequentialProjectEditCommand("Set instrument members",
        [
            project =>
            {
                var voice = FindSubVoice(FindEventInstrument(project, instrumentId), voiceId);
                if (originalGroup.HasValue)
                {
                    return UpdateTemplateBank(instrumentId, voiceId, bankId, tick, address.BankMsb, address.BankLsb);
                }
                bankId = new(project.NextStableId);
                return CreateTemplateBank(instrumentId, voiceId, tick, address.BankMsb, address.BankLsb);
            },
            project =>
            {
                var voice = FindSubVoice(FindEventInstrument(project, instrumentId), voiceId);
                if (originalGroup.HasValue)
                {
                    return UpdateTemplateProgram(instrumentId, voiceId, programId, tick, address.Program);
                }
                programId = new(project.NextStableId);
                return CreateTemplateProgram(instrumentId, voiceId, tick, address.Program);
            },
            _ => Command("Link instrument members", project =>
            {
                var instrument = FindEventInstrument(project, instrumentId);
                var voice = FindSubVoice(instrument, voiceId);
                var previous = voice.InstrumentChanges;
                InstrumentChange? created = null;
                return Prepared(true, EventInstrumentChange(instrumentId), owner =>
                {
                    var groups = previous;
                    foreach (var member in new[] { bankId, programId })
                        if (groups.TryGetByMember(member, out var old)) groups = groups.Remove(old.Id);
                    created ??= originalGroup ?? new InstrumentChange(owner.AllocateStableId(), bankId, null, programId);
                    voice.InstrumentChanges = groups.Add(created.Value, false).ValidatedAt(voice.Events.Generation);
                }, _ => voice.InstrumentChanges = previous);
            })
        ]).Prepare(original);
        });
    }
}
