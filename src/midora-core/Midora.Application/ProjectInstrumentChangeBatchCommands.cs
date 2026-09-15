using Midora.Domain;

namespace Midora.Application;

public readonly record struct InstrumentChangeOwner(MidoraId OwnerId, MidoraId? InstrumentId = null)
{
    public bool IsDirectMidi => InstrumentId is null;
}
public enum InstrumentChangeOperation { Move, Delete, Flip, Scale, Quantize, Properties }
public sealed record InstrumentChangeEdit(InstrumentChangeOperation Operation, long TickDelta = 0,
    double Scale = 1, TimelineQuantizeGrid? Grid = null, long? Tick = null,
    int? BankMsb = null, int? BankLsb = null, int? Program = null);

public static class InstrumentChangeSelectionQuery
{
    public static void PrepareSource(TemplateEventQuerySnapshot source, CancellationToken token = default)
    { using var scope = BulkEditPreparationContext.Enter(token); source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, scope.Token); }
    public static IEnumerable<InstrumentChange> EnumerateGroups(InstrumentChangeSet groups, IReadOnlySet<MidoraId> selectedMembers,
        CancellationToken token = default)
    {
        if (selectedMembers.Count >= groups.Count)
        {
            // Dense selections are a sequential association-page walk. Avoid
            // looking up the same wrapper two times for each of its members.
            foreach (var group in groups.Values)
            {
                token.ThrowIfCancellationRequested();
                if (selectedMembers.Contains(group.ProgramEventId) && selectedMembers.Contains(group.BankEventId)
                    && (group.BankLsbEventId is not { } lsb || selectedMembers.Contains(lsb))) yield return group;
            }
            yield break;
        }
        // Only one member (Program) emits its wrapper. No result-sized HashSet.
        foreach (var id in selectedMembers)
        {
            token.ThrowIfCancellationRequested();
            if (!groups.TryGetByMember(id, out var group) || group.ProgramEventId != id) continue;
            if (selectedMembers.Contains(group.BankEventId)
                && (group.BankLsbEventId is not { } lsb || selectedMembers.Contains(lsb))) yield return group;
        }
    }
    public static (InstrumentChangeSet Groups, Func<InstrumentChange, InstrumentChangeValue?> Read) Capture(
        MidoraProject project, InstrumentChangeOwner owner)
    {
        if (owner.IsDirectMidi)
        {
            var segment = project.PureMidiTracks.SelectMany(static track => track.Segments).Single(value => value.Id == owner.OwnerId);
            var source = segment.ChannelEvents.CreateQuerySnapshot();
            return (segment.InstrumentChanges, group => InstrumentChangeResolver.TryRead(source, group, out var value) ? value : null);
        }
        var voice = project.EventInstruments.Single(value => value.Id == owner.InstrumentId).SubVoices.Single(value => value.Id == owner.OwnerId);
        var snapshot = voice.Events.CreateQuerySnapshot();
        if (voice.InstrumentChanges.Count != 0) PrepareSource(snapshot, BulkEditPreparationContext.Current?.Token ?? default);
        return (voice.InstrumentChanges, group => InstrumentChangeResolver.TryRead(snapshot, group, out var value) ? value : null);
    }
}

public static partial class ProjectDomainEditCommands
{
    public static ITimelineSelectionResultEditCommand EditInstrumentChanges(InstrumentChangeOwner owner,
        IReadOnlySet<MidoraId> selectedMembers, InstrumentChangeEdit edit) =>
        ResultCommand($"{edit.Operation} instrument changes", (project, publish, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            if (edit.BankMsb is < 0 or > 127 || edit.BankLsb is < 0 or > 127 || edit.Program is < 0 or > 127)
                throw new ArgumentOutOfRangeException(nameof(edit), "Bank / Program must be between 0 and 127.");
            if (edit.Tick is < 0 or long.MaxValue) throw new ArgumentOutOfRangeException(nameof(edit));
            if (edit.Operation == InstrumentChangeOperation.Scale) ValidateScaleFactor(edit.Scale);
            long step = edit.Operation == InstrumentChangeOperation.Quantize
                ? ResolveQuantizeStep(project, edit.Grid ?? throw new ArgumentNullException(nameof(edit.Grid))) : 1;
            var context = InstrumentChangeSelectionQuery.Capture(project, owner);
            using var members = new BoundedEditRecordStore<MidoraId>(scope.Resources);
            long left = long.MaxValue, right = 0; int count = 0;
            foreach (var group in InstrumentChangeSelectionQuery.EnumerateGroups(context.Groups, selectedMembers, token))
            {
                if (context.Read(group) is not { } value) throw new InvalidOperationException("The Instrument Change is no longer complete.");
                left = Math.Min(left, value.Tick); right = Math.Max(right, value.Tick);
                foreach (var id in group.MemberIds) members.Add(id, token);
                if ((++count & 255) == 0) scope.Checkpoint(members.Count, selectedMembers.Count, TimelineEditPreparationPhase.ResolvingSelection);
            }
            members.Seal();
            if (count == 0) throw new InvalidOperationException("Select complete Instrument Changes first.");
            var midiOwner = owner.IsDirectMidi ? FindMidiSegment(project, owner.OwnerId).Segment : null;
            long ChangeTick(long value) => edit.Operation switch
            {
                InstrumentChangeOperation.Move => checked(value + Math.Max(-left, edit.TickDelta)),
                InstrumentChangeOperation.Flip => checked(left + (right - value)),
                InstrumentChangeOperation.Scale => ScaleTick(left, value, edit.Scale),
                InstrumentChangeOperation.Quantize => midiOwner is null ? SnapProjectAbsolute(value, step)
                    : SegmentAbsoluteToLocal(SnapProjectAbsolute(SegmentLocalToAbsolute(value,
                        midiOwner.ProjectStartTick, midiOwner.ContentOffsetTick), step), midiOwner.ProjectStartTick, midiOwner.ContentOffsetTick),
                InstrumentChangeOperation.Properties => edit.Tick ?? value,
                _ => value
            };
            // Reconcile deferred member associations on the detached mirror,
            // not during the UI-thread publication (large Delete can dissolve
            // hundreds of thousands of groups).
            var prepared = new SequentialProjectEditCommand("Edit Instrument Change members",
                [_ => Command("Transform Instrument Change messages", PrepareMembers)]).Prepare(project);
            IPreparedProjectEdit PrepareMembers(MidoraProject target)
            {
            if (owner.IsDirectMidi)
                return PrepareBoundedDirectMidiEventTransform(target, owner.OwnerId, members, _ => value =>
                {
                    if (edit.Operation == InstrumentChangeOperation.Delete) return null;
                    var result = value with { Tick = ChangeTick(value.Tick) };
                    if (result.Tick < 0) return null;
                    if (edit.Operation == InstrumentChangeOperation.Properties)
                        result = value.Kind == DirectMidiChannelEventKind.ProgramChange
                            ? result with { Data1 = edit.Program ?? value.Data1 }
                            : result with { Data2 = (value.Data1 == 0 ? edit.BankMsb : edit.BankLsb) ?? value.Data2 };
                    return result;
                }, edit.Operation is InstrumentChangeOperation.Flip or InstrumentChangeOperation.Scale
                    ? BoundedEventCollisionMode.Reject : BoundedEventCollisionMode.Overwrite, publish);
            else
            {
                var instrument = FindEventInstrument(target, owner.InstrumentId!.Value);
                var voice = FindSubVoice(instrument, owner.OwnerId);
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
                using var plan = BoundedTemplatePointPlan.Transform(voice.Events.CreateQuerySnapshot(), members,
                    static value => value.Kind is TemplateEventKind.Bank or TemplateEventKind.Program, _ => value =>
                    {
                        if (edit.Operation == InstrumentChangeOperation.Delete) return null;
                        var result = value with { Tick = ChangeTick(value.Tick) };
                        if (edit.Operation == InstrumentChangeOperation.Properties)
                            result = value.Kind == TemplateEventKind.Bank
                                ? result with { Value = edit.BankMsb ?? value.Value, SecondaryValue = edit.BankLsb ?? value.SecondaryValue }
                                : result with { Value = edit.Program ?? value.Value };
                        return result;
                    }, true, false, edit.Operation == InstrumentChangeOperation.Quantize,
                    () => throw new InvalidOperationException("Instrument transforms cannot allocate raw event identities."), ValidateTemplateEventValue,
                    rejectCollisions: edit.Operation is InstrumentChangeOperation.Flip or InstrumentChangeOperation.Scale);
                return PublishBoundedTemplatePoints(target, instrument, voice, stamp, plan,
                    target.NextStableId, target.NextStableId, publish);
            }
            }
            var processed = context.Groups;
            bool IsProcessed(MidoraId id) => processed.TryGetByMember(id, out var group)
                && selectedMembers.Contains(group.ProgramEventId) && selectedMembers.Contains(group.BankEventId)
                && (group.BankLsbEventId is not { } lsb || selectedMembers.Contains(lsb));
            var selectedResult = ((IPreparedTimelineSelectionEdit)prepared).PreparedSelection.ResultSelectionIds;
            return PublishBoundedNoteSelection(prepared, selectedMembers,
                members.Count == selectedMembers.Count ? selectedResult
                    : selectedMembers.Where(id => !IsProcessed(id)).Concat(selectedResult), publish, scope);
        });
}
