using Midora.Domain;

namespace Midora.Application;

internal sealed record InstrumentChangeClipboardData(IReadOnlyList<InstrumentChangeValue> Values) : ProjectObjectClipboardData;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyInstrumentChanges(ProjectDocumentSession document,
        InstrumentChangeOwner owner, IReadOnlySet<MidoraId> members)
    {
        using var capture = ClipboardCaptureScope.Enter();
        var context = InstrumentChangeSelectionQuery.Capture(document.Project, owner);
        long earliest = long.MaxValue;
        var values = ClipboardCaptureScope.Capture(InstrumentChangeSelectionQuery.EnumerateGroups(context.Groups, members,
            BulkEditPreparationContext.Current!.Token).Select(group =>
            {
                var value = context.Read(group) ?? throw new InvalidOperationException("The Instrument Change is incomplete.");
                earliest = Math.Min(earliest, value.Tick); return value;
            }), members.Count / (owner.IsDirectMidi ? 3 : 2));
        if (values.Count == 0) throw new InvalidOperationException("Select complete Instrument Changes first.");
        var relative = new ProjectedClipboardList<InstrumentChangeValue, InstrumentChangeValue>(values,
            value => value with { Tick = checked(value.Tick - earliest) });
        return new(document.ClipboardSessionIdentity, ProjectObjectClipboardKind.InstrumentChanges, values.Count,
            $"{values.Count:N0} Instrument Changes", new InstrumentChangeClipboardData(relative));
    }
    public static IProjectEditCommand CreatePasteInstrumentChangesCommand(ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload, InstrumentChangeOwner owner, long tick,
        IReadOnlyCollection<MidoraId>? originalSelection = null)
    {
        var data = RequirePayload<InstrumentChangeClipboardData>(document, payload, ProjectObjectClipboardKind.InstrumentChanges);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.AppendInstrumentChanges(owner,
            data.Values.Select(value => value with { Tick = checked(tick + value.Tick) }), originalSelection),
            new(payload.Kind, owner.OwnerId, owner.InstrumentId, owner.IsDirectMidi), independentlyPreparedContent: true);
    }
}

public static partial class ProjectDomainEditCommands
{
    public static ITimelineSelectionResultEditCommand DuplicateInstrumentChanges(InstrumentChangeOwner owner,
        IReadOnlySet<MidoraId> selectedMembers, long delta) => ResultCommand("Duplicate instrument changes", (project, publish, token, progress) =>
    {
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var context = InstrumentChangeSelectionQuery.Capture(project, owner);
        using var values = new BoundedEditRecordStore<InstrumentChangeValue>(scope.Resources);
        long earliest = long.MaxValue;
        foreach (var group in InstrumentChangeSelectionQuery.EnumerateGroups(context.Groups, selectedMembers, token))
        {
            var value = context.Read(group) ?? throw new InvalidOperationException("Incomplete Instrument Change.");
            earliest = Math.Min(earliest, value.Tick); values.Add(value, token);
        }
        values.Seal();
        long effectiveDelta = Math.Max(delta, -earliest);
        return PrepareAppendInstrumentChanges(project, owner, values.Select(value => value with { Tick = checked(value.Tick + effectiveDelta) }), selectedMembers, publish);
    });

    internal static ITimelineSelectionResultEditCommand AppendInstrumentChanges(InstrumentChangeOwner owner,
        IEnumerable<InstrumentChangeValue> values, IReadOnlyCollection<MidoraId>? originalSelection = null) =>
        ResultCommand("Paste instrument changes", (project, publish, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            return PrepareAppendInstrumentChanges(project, owner, values, originalSelection ?? [], publish);
        });

    private static IPreparedProjectEdit PrepareAppendInstrumentChanges(MidoraProject project, InstrumentChangeOwner owner,
        IEnumerable<InstrumentChangeValue> values, IReadOnlyCollection<MidoraId> before, SelectionPublisher publish)
    {
        var scope = BulkEditPreparationContext.Current!;
        var originalGroups = InstrumentChangeSelectionQuery.Capture(project, owner).Groups;
        var originalSet = before as IReadOnlySet<MidoraId>;
        bool Retain(MidoraId id) => originalSet is not null && (!originalGroups.TryGetByMember(id, out var group)
            || !originalSet.Contains(group.ProgramEventId) || !originalSet.Contains(group.BankEventId)
            || group.BankLsbEventId is { } lsb && !originalSet.Contains(lsb));
        using var rows = BoundedEditSort.Sort(values, Comparer<InstrumentChangeValue>.Create(static (a, b) =>
        { int c = a.Tick.CompareTo(b.Tick); return c != 0 ? c : a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id); }), scope.Resources, scope.Token);
        if (rows.Count == 0) throw new InvalidOperationException("The Instrument Change clipboard is empty.");
        foreach (var value in rows)
        {
            if (value.Tick < 0 || value.Tick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(values));
            if (value.BankMsb is < 0 or > 127 || value.BankLsb is < 0 or > 127 || value.Program is < 0 or > 127)
                throw new ArgumentOutOfRangeException(nameof(values), "Bank / Program must be between 0 and 127.");
        }
        long firstId = 0;
        using var resultIds = new BoundedEditRecordStore<MidoraId>(scope.Resources);
        var commands = new List<Func<MidoraProject, IProjectEditCommand>>();
        if (owner.IsDirectMidi) commands.Add(_ => ReserveInstrumentChangeOrders(owner.OwnerId, rows.Select(static value => value.Tick)));
        commands.Add(_ => Command("Append Instrument Change messages", target =>
        {
            firstId = target.NextStableId;
            if (owner.IsDirectMidi)
                return PrepareBoundedDirectMidiEventAppend(target, owner.OwnerId, start => Direct(start));
            var instrument = FindEventInstrument(target, owner.InstrumentId!.Value);
            var voice = FindSubVoice(instrument, owner.OwnerId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            long next = firstId;
            using var plan = BoundedTemplatePointPlan.Append(voice.Events.CreateQuerySnapshot(), Template(),
                () => new(checked(next++)), ValidateTemplateEventValue);
            return PublishBoundedTemplatePoints(target, instrument, voice, stamp, plan, firstId, next, static ids => ids, false);
        }));
        commands.Add(_ => Command("Associate Instrument Change messages", target =>
        {
            long expected = target.NextStableId, next = expected;
            var context = InstrumentChangeSelectionQuery.Capture(target, owner);
            var groups = new InstrumentChangeSet(BoundedInstrumentChangeStorage.AddMany(context.Groups, ReadGroups()));
            if (owner.IsDirectMidi)
            {
                var location = FindMidiSegment(target, owner.OwnerId);
                var old = location.Segment;
                var replacement = new MidiSegment(target, old.Id) { ProjectStartTick = old.ProjectStartTick,
                    ContentOffsetTick = old.ContentOffsetTick, LengthTicks = old.LengthTicks,
                    InstrumentChanges = groups.ValidatedAt(old.ChannelEvents.Generation) };
                old.Notes.CloneTo(replacement.Notes, scope.Token); old.ChannelEvents.CloneTo(replacement.ChannelEvents, scope.Token);
                old.OpaqueEvents.CloneTo(replacement.OpaqueEvents, scope.Token);
                return ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(target, location.Track, old, replacement,
                    PureMidiTrackChange(location.Track.Id), expected, next, ProjectTimelineOwnerSourceStamp.Capture(old));
            }
            var instrument = FindEventInstrument(target, owner.InstrumentId!.Value);
            var voice = FindSubVoice(instrument, owner.OwnerId);
            var revised = ProjectTimelineOwnerRootClone.CloneSubVoice(target, voice, scope.Token);
            revised.InstrumentChanges = groups.ValidatedAt(voice.Events.Generation);
            return ProjectTimelineOwnerRootReplacement.PrepareSubVoice(target, instrument, voice, revised,
                EventInstrumentChange(instrument.Id), expected, next, ProjectTimelineOwnerSourceStamp.Capture(instrument, voice));

            IEnumerable<InstrumentChange> ReadGroups()
            {
                long raw = firstId;
                foreach (var row in rows)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    var group = owner.IsDirectMidi ? new InstrumentChange(new(next), new(raw), new(raw + 1), new(raw + 2))
                        : new InstrumentChange(new(next), new(raw), null, new(raw + 1));
                    raw = checked(raw + (owner.IsDirectMidi ? 3 : 2));
                    if (context.Read(group) is null) continue; // a later clipboard point won this exact tick
                    next = checked(next + 1);
                    foreach (var id in group.MemberIds) resultIds.Add(id, scope.Token);
                    yield return group;
                }
            }
        }));
        var prepared = new SequentialProjectEditCommand("Paste instrument changes", commands).Prepare(project);
        resultIds.Seal();
        return PublishBoundedNoteSelection(prepared, before, before.Where(Retain).Concat(resultIds), publish, scope);

        IEnumerable<DirectMidiChannelEventValue> Direct(long start)
        {
            foreach (var row in rows)
            {
                yield return new(new(checked(start++)), row.Tick, DirectMidiChannelEventKind.ControlChange, 0, row.BankMsb, 0);
                yield return new(new(checked(start++)), row.Tick, DirectMidiChannelEventKind.ControlChange, 32, row.BankLsb, 1);
                yield return new(new(checked(start++)), row.Tick, DirectMidiChannelEventKind.ProgramChange, row.Program, 0, 2);
            }
        }
        IEnumerable<TemplateEventSnapshotValue> Template()
        {
            foreach (var row in rows)
            {
                yield return new(new(firstId), TemplateEventKind.Bank, row.Tick, 0, 0, row.BankMsb, row.BankLsb, true, true, false);
                yield return new(new(firstId), TemplateEventKind.Program, row.Tick, 0, 0, row.Program, 0, false, false, false);
            }
        }
    }
}
