namespace Midora.Domain;

public readonly record struct InstrumentChangeValue(MidoraId Id, long Tick, int BankMsb, int BankLsb, int Program,
    long Order = 0, MidoraId BankEventId = default, MidoraId? BankLsbEventId = null, MidoraId ProgramEventId = default);

/// <summary>Reads only explicitly associated member identities, never discovers groups from raw MIDI.</summary>
public static class InstrumentChangeResolver
{
    public static bool TryRead(MidiSegment owner, InstrumentChange group, out InstrumentChangeValue value) =>
        TryRead(owner.ChannelEvents.CreateQuerySnapshot(), group, out value);

    public static bool TryRead(DirectMidiChannelEventQuerySnapshot source, InstrumentChange group,
        out InstrumentChangeValue value)
    {
        value = default;
        if (group.BankLsbEventId is not { } lsbId) return false;
        DirectMidiChannelEventValue bank = default, lsb = default, program = default;
        foreach (var item in source.ResolveByIds(new HashSet<MidoraId>(group.MemberIds)))
        {
            if (item.Id == group.BankEventId) bank = item;
            if (item.Id == lsbId) lsb = item;
            if (item.Id == group.ProgramEventId) program = item;
        }
        if (bank.Id == default || lsb.Id == default || program.Id == default
            || bank.Kind != DirectMidiChannelEventKind.ControlChange || bank.Data1 != 0
            || lsb.Kind != DirectMidiChannelEventKind.ControlChange || lsb.Data1 != 32
            || program.Kind != DirectMidiChannelEventKind.ProgramChange
            || bank.Tick != lsb.Tick || bank.Tick != program.Tick) return false;
        value = new(group.Id, bank.Tick, bank.Data2, lsb.Data2, program.Data1, program.Order,
            group.BankEventId, group.BankLsbEventId, group.ProgramEventId);
        return true;
    }

    public static bool TryRead(SubVoice owner, InstrumentChange group, out InstrumentChangeValue value) =>
        TryRead(owner.Events.CreateQuerySnapshot(), group, out value);

    public static bool TryRead(TemplateEventQuerySnapshot source, InstrumentChange group,
        out InstrumentChangeValue value)
    {
        value = default;
        if (group.BankLsbEventId.HasValue) return false;
        TemplateEventSnapshotValue bank = default, program = default;
        foreach (var item in source.ResolveByIds([group.BankEventId, group.ProgramEventId]))
        {
            if (item.Id == group.BankEventId) bank = item;
            if (item.Id == group.ProgramEventId) program = item;
        }
        if (bank.Id == default || program.Id == default || bank.Kind != TemplateEventKind.Bank
            || !bank.HasBankMsb || !bank.HasBankLsb || program.Kind != TemplateEventKind.Program
            || bank.Tick != program.Tick) return false;
        if (!source.TryFindOrdinalById(program.Id, out int ordinal)) return false;
        value = new(group.Id, bank.Tick, bank.Value, bank.SecondaryValue, program.Value, ordinal,
            group.BankEventId, null, group.ProgramEventId);
        return true;
    }

    public static InstrumentChangeSet RemoveInvalid(InstrumentChangeSet groups,
        Func<InstrumentChange, bool> valid, CancellationToken token = default)
    {
        var result = groups;
        foreach (var group in groups.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!valid(group)) result = result.Remove(group.Id);
        }
        return result;
    }
}
