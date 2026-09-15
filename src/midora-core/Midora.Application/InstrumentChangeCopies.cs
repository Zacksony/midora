using Midora.Domain;

namespace Midora.Application;

// Ordinals belong only to the frozen clipboard source, never to Project
// persistence. Allocation during a full-owner copy is consecutive for events.
internal readonly record struct InstrumentChangeClipboardRecord(int BankOrdinal, int LsbOrdinal, int ProgramOrdinal);

internal static class InstrumentChangeCopies
{
    internal static IEnumerable<InstrumentChangeClipboardRecord> Capture<T>(InstrumentChangeSet groups,
        ITimelineObjectSource<T> source)
    {
        foreach (var group in groups.Values)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            yield return new(Ordinal(group.BankEventId), group.BankLsbEventId is { } id ? Ordinal(id) : -1,
                Ordinal(group.ProgramEventId));
        }
        int Ordinal(MidoraId id) => source.TryFindOrdinalById(id, out int ordinal) ? ordinal
            : throw new InvalidOperationException("Instrument Change member is missing from its frozen source.");
    }

    internal static InstrumentChangeSet Restore(MidoraProject project,
        IEnumerable<InstrumentChangeClipboardRecord> records, long firstEventId, bool direct,
        Func<InstrumentChange, bool> valid)
    {
        return new InstrumentChangeSet(BoundedInstrumentChangeStorage.Create(Read()));
        IEnumerable<InstrumentChange> Read()
        {
        foreach (var record in records)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            var group = new InstrumentChange(project.AllocateStableId(),
                new(checked(firstEventId + record.BankOrdinal)),
                record.LsbOrdinal >= 0 ? new MidoraId(checked(firstEventId + record.LsbOrdinal)) : null,
                new(checked(firstEventId + record.ProgramOrdinal)));
            group.ValidateShape(direct);
            if (valid(group)) yield return group;
        }
        }
    }
}
