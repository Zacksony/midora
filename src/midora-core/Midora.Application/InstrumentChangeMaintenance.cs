using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Final-transaction safety net for legacy in-place and composite commands.
/// Detached point planners reconcile through their affected-member receipts.
/// No raw event list is scanned to discover or reconstruct associations.
/// </summary>
internal static class InstrumentChangeMaintenance
{
    public static IPreparedProjectEdit Wrap(MidoraProject project, IPreparedProjectEdit source)
    {
        if (!source.HasChanges) return source;
        var before = Capture(project, source.Changes);
        return before.Count == 0 ? source : new Prepared(source, before);
    }

    internal sealed record OwnerState(MidoraId Id, bool DirectMidi, InstrumentChangeSet Groups, long Revision);

    public static IReadOnlyList<OwnerState> Capture(MidoraProject project, ProjectChangeSet changes)
    {
        List<OwnerState> result = [];
        foreach (var track in project.PureMidiTracks)
        {
            if (!changes.AffectsEverything && !changes.PureMidiTrackIds.Contains(track.Id)) continue;
            foreach (var segment in track.Segments)
                if (segment.InstrumentChanges.Count != 0)
                    result.Add(new(segment.Id, true, segment.InstrumentChanges, segment.ChannelEvents.Generation));
        }
        foreach (var instrument in project.EventInstruments)
        {
            if (!changes.AffectsEverything && !changes.EventInstrumentIds.Contains(instrument.Id)) continue;
            foreach (var voice in instrument.SubVoices)
                if (voice.InstrumentChanges.Count != 0)
                    result.Add(new(voice.Id, false, voice.InstrumentChanges, voice.Events.Generation));
        }
        return result;
    }

    public static void Reconcile(MidoraProject target, IReadOnlyList<OwnerState> original,
        CancellationToken token = default)
    {
        foreach (var item in original)
        {
            token.ThrowIfCancellationRequested();
            if (item.DirectMidi)
            {
                var owner = FindMidi(target, item.Id);
                if (owner is null) continue;
                if (owner.ChannelEvents.Generation == item.Revision)
                {
                    if (owner.InstrumentChanges.Count == 0) owner.InstrumentChanges = item.Groups;
                    continue;
                }
                var snapshot = owner.ChannelEvents.CreateQuerySnapshot();
                owner.InstrumentChanges = MergeValid(item.Groups, owner.InstrumentChanges,
                    group => InstrumentChangeResolver.TryRead(snapshot, group, out _), true, owner.ChannelEvents.Generation, token);
            }
            else
            {
                var owner = FindVoice(target, item.Id);
                if (owner is null) continue;
                if (owner.Events.Generation == item.Revision)
                {
                    if (owner.InstrumentChanges.Count == 0) owner.InstrumentChanges = item.Groups;
                    continue;
                }
                var snapshot = owner.Events.CreateQuerySnapshot();
                owner.InstrumentChanges = MergeValid(item.Groups, owner.InstrumentChanges,
                    group => InstrumentChangeResolver.TryRead(snapshot, group, out _), false, owner.Events.Generation, token);
            }
        }
    }

    private static InstrumentChangeSet MergeValid(InstrumentChangeSet previous, InstrumentChangeSet current,
        Func<InstrumentChange, bool> valid, bool direct, long revision, CancellationToken token)
    {
        if (current.IsValidatedFor(revision))
        {
            // Bounded planners already checked affected members using the
            // reverse index. Only temporarily dissolved groups need revisiting.
            var pending = current.PendingReconciliation;
            foreach (var group in pending)
            {
                token.ThrowIfCancellationRequested();
                if (!current.TryGet(group.Id, out _) && valid(group)
                    && !group.MemberIds.Any(id => current.TryGetByMember(id, out _)))
                    current = current.Add(group, direct);
            }
            return current.ValidatedAt(revision, complete: true);
        }
        current = InstrumentChangeResolver.RemoveInvalid(current, valid, token);
        foreach (var group in previous.Values)
        {
            token.ThrowIfCancellationRequested();
            if (current.TryGet(group.Id, out _) || !valid(group)
                || group.MemberIds.Any(id => current.TryGetByMember(id, out _))) continue;
            current = current.Add(group, direct);
        }
        return current.ValidatedAt(revision, complete: true);
    }

    private static MidiSegment? FindMidi(MidoraProject project, MidoraId id) =>
        project.PureMidiTracks.SelectMany(static track => track.Segments).FirstOrDefault(value => value.Id == id);
    private static SubVoice? FindVoice(MidoraProject project, MidoraId id) =>
        project.EventInstruments.SelectMany(static instrument => instrument.SubVoices).FirstOrDefault(value => value.Id == id);

    private sealed class Prepared(IPreparedProjectEdit source, IReadOnlyList<OwnerState> before)
        : IPreparedTimelineSelectionEdit, IPreparedProjectEditPublicationGate, IDisposable
    {
        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;
        public bool HasPreparedSelection => source is IPreparedTimelineSelectionEdit { HasPreparedSelection: true };
        public PreparedTimelineSelection PreparedSelection => source is IPreparedTimelineSelectionEdit selection
            ? selection.PreparedSelection : new([], []);
        public void ValidateForPublication(MidoraProject project)
        {
            if (source is IPreparedProjectEditPublicationGate gate) gate.ValidateForPublication(project);
        }
        public void Apply(MidoraProject project)
        {
            source.Apply(project);
            // The document transaction owns rollback. Never undo twice if
            // final association validation fails after the raw publication.
            Reconcile(project, before);
        }
        public void Undo(MidoraProject project)
        {
            source.Undo(project);
            foreach (var state in before)
            {
                if (state.DirectMidi)
                {
                    if (FindMidi(project, state.Id) is { } owner) owner.InstrumentChanges = state.Groups;
                }
                else if (FindVoice(project, state.Id) is { } owner) owner.InstrumentChanges = state.Groups;
            }
        }
        public void Dispose() { if (source is IDisposable disposable) disposable.Dispose(); }
    }
}
