using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private sealed class DirectContentCopyProgress(long total)
    {
        private long _completed;
        public IEnumerable<TValue> Read<TValue>(ITimelineObjectSource<TValue> source) where TValue : struct
        {
            for (int ordinal = 0; ordinal < source.Count; ordinal++)
            {
                BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                TValue value = source.GetByOrdinal(ordinal);
                _completed++;
                if ((_completed & 255) == 0 || _completed == total)
                    BulkEditPreparationContext.Current?.Checkpoint(_completed, total);
                yield return value;
            }
        }
    }

    private static long DirectContentRecordCount(MidiSegment segment) =>
        checked((long)segment.Notes.Count + segment.ChannelEvents.Count + segment.OpaqueEvents.Count);

    private static void AdoptBoundedDirectMidiNotes(MidoraProject project, MidiSegment target,
        IEnumerable<DirectMidiNoteValue> values)
    {
        if (target.Notes.Count != 0) throw new InvalidOperationException("Detached MIDI content must start empty.");
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiNoteSource.Capture(target.Notes);
        using var store = new BoundedEditRecordStore<BoundedDirectNoteDelta>(scope.Resources);
        int ordinal = 0;
        foreach (var value in values)
        {
            ValidateDirectMidiNote(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity);
            store.Add(new(ordinal++, false, default, value), scope.Token);
        }
        store.Seal();
        if (store.Count == 0) return;
        using var patch = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(store);
        target.Notes.AdoptContentSource(source.Apply(patch, scope.Resources, scope.Token, ordinal), target.Notes.Generation + 1);
    }

    private static void AdoptBoundedDirectMidiEvents(MidoraProject project, MidiSegment target,
        IEnumerable<DirectMidiChannelEventValue> values)
    {
        if (target.ChannelEvents.Count != 0) throw new InvalidOperationException("Detached MIDI content must start empty.");
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        var source = BoundedDirectMidiEventSource.Capture(target.ChannelEvents);
        using var store = new BoundedEditRecordStore<BoundedDirectEventDelta>(scope.Resources);
        int ordinal = 0;
        foreach (var value in values)
        {
            ValidateDirectMidiEvent(value.Tick, value.Kind, value.Data1, value.Data2);
            store.Add(new(ordinal++, false, default, value), scope.Token);
        }
        store.Seal();
        if (store.Count == 0) return;
        using var patch = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
        target.ChannelEvents.AdoptContentSource(source.Apply(patch, scope.Resources, scope.Token, ordinal), target.ChannelEvents.Generation + 1);
    }
}
