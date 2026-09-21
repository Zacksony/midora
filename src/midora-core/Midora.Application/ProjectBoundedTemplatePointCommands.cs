using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedTemplateUpdate(MidoraProject project, EventInstrument instrument,
        SubVoice voice, MidoraId id, TemplateEventKind expectedKind, Func<TemplateEventValue, TemplateEventValue> update)
    {
        using var scope = BulkEditPreparationContext.Enter(project: project);
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
        long nextId = project.NextStableId;
        using var plan = BoundedTemplatePointPlan.Transform(voice.Events.CreateQuerySnapshot(), [id],
            old => old.Kind == expectedKind, _ => old =>
            {
                var value = update(new(old.Kind, old.Tick, old.LengthTicks, old.Number, old.Value,
                    old.SecondaryValue, old.HasBankMsb, old.HasBankLsb, old.FollowPitchDelta));
                return new TemplateEventSnapshotValue(old.Id, value.Kind, value.Tick, value.LengthTicks,
                    value.Number, value.Value, value.SecondaryValue, value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta);
            }, true, false, false, () => throw new InvalidOperationException("A value edit cannot create an event ID."), ValidateTemplateEventValue);
        return PublishBoundedTemplatePoints(project, instrument, voice, stamp, plan, nextId, nextId, static ids => ids, false);
    }
    private static ITimelineSelectionResultEditCommand BoundedTemplatePoints(string name,
        MidoraId instrumentId, MidoraId subVoiceId, IReadOnlyCollection<MidoraId> ids,
        BoundedPointOperation operation, MidiValueTarget? target = null, long tickDelta = 0,
        int valueDelta = 0, bool duplicate = false, double factor = 1,
        BatchEditExpressionProgram? program = null, TimelineQuantizeGrid? grid = null) =>
        ResultCommand(name, (project, publish, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            EventInstrument instrument = FindEventInstrument(project, instrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            if (operation == BoundedPointOperation.Scale) ValidateScaleFactor(factor);
            long step = operation == BoundedPointOperation.Quantize ? ResolveQuantizeStep(project,
                grid ?? throw new ArgumentNullException(nameof(grid))) : 1;
            if (operation == BoundedPointOperation.Batch)
            {
                ArgumentNullException.ThrowIfNull(program); ValidatePointBatchProgram(program);
                var range = MidiEventTargetRange(target!.Value);
                int offset = MidiEditingValueDomain.Offset(target.Value);
                ValidateDirectRange(program, BatchEditField.PointValue, range.Minimum + offset, range.Maximum + offset);
            }
            long expectedId = project.NextStableId, nextId = expectedId;
            using var plan = BoundedTemplatePointPlan.Transform(voice.Events.CreateQuerySnapshot(), ids,
                old => operation == BoundedPointOperation.Delete || old.Kind != TemplateEventKind.Note
                    && (target is null || BoundedTemplateMatchesTarget(old, target.Value)),
                rows =>
                {
                    long left = rows.Min(static row => row.Value.Tick), right = rows.Max(static row => row.Value.Tick);
                    var clock = Stopwatch.StartNew();
                    return old =>
                    {
                        if (operation == BoundedPointOperation.Delete) return null;
                        var value = old with { Tick = operation switch
                        {
                            BoundedPointOperation.Adjust => checked(old.Tick + tickDelta),
                            BoundedPointOperation.Flip => checked(left + (right - old.Tick)),
                            BoundedPointOperation.Scale => ScaleTick(left, old.Tick, factor),
                            BoundedPointOperation.Quantize => SnapProjectAbsolute(old.Tick, step),
                            _ => old.Tick
                        }};
                        if (operation == BoundedPointOperation.Adjust)
                            value = SetBoundedTemplatePointValue(value, target!.Value,
                                checked(GetBoundedTemplatePointValue(old, target.Value) + valueDelta));
                        if (operation == BoundedPointOperation.Batch)
                        {
                            int offset = MidiEditingValueDomain.Offset(target!.Value);
                            var calculated = program!.Evaluate(new(0, GetBoundedTemplatePointValue(old, target.Value) + offset,
                                0, 0, old.Tick, checked(old.Tick - left)), clock, BatchExpressionTimeout);
                            if (RoundTickOrDiscard(calculated.Tick) is not long tick) return null;
                            var (minimum, maximum) = MidiEventTargetRange(target.Value);
                            value = SetBoundedTemplatePointValue(old with { Tick = tick }, target.Value,
                                checked((int)RoundAndClamp(calculated.PointValue, minimum + offset, maximum + offset) - offset));
                        }
                        return value;
                    };
                }, duplicate || operation is BoundedPointOperation.Flip or BoundedPointOperation.Scale
                    or BoundedPointOperation.Batch or BoundedPointOperation.Quantize
                    || operation == BoundedPointOperation.Adjust && tickDelta != 0,
                duplicate, operation == BoundedPointOperation.Quantize,
                () => MidoraId.FromSequence(checked(nextId++)), ValidateTemplateEventValue);
            return PublishBoundedTemplatePoints(project, instrument, voice, stamp, plan, expectedId, nextId, publish);
        });

    internal static IProjectEditCommand AppendBoundedTemplateEventPoints(MidoraId instrumentId,
        MidoraId subVoiceId, IEnumerable<TemplateEventSnapshotValue> values) =>
        ResultCommand("Paste SubVoice event points", (project, publish, token, progress) =>
        {
            using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
            EventInstrument instrument = FindEventInstrument(project, instrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            long expectedId = project.NextStableId, nextId = expectedId;
            using var plan = BoundedTemplatePointPlan.Append(voice.Events.CreateQuerySnapshot(), values,
                () => MidoraId.FromSequence(checked(nextId++)), value =>
                {
                    ValidateTemplateEventValue(value);
                });
            return PublishBoundedTemplatePoints(project, instrument, voice, stamp, plan, expectedId, nextId, publish);
        });

    private static IPreparedProjectEdit PublishBoundedTemplatePoints(MidoraProject project,
        EventInstrument instrument, SubVoice voice, ProjectTimelineOwnerSourceStamp stamp,
        BoundedTemplatePointPlan plan, long expectedId, long nextId, SelectionPublisher publish,
        bool publishSelection = true)
    {
        var scope = BulkEditPreparationContext.Current!;
        ProjectChangeSet changes = EventInstrumentChange(instrument.Id);
        if (!plan.HasChanges)
            return Finish(ProjectTimelineOwnerRootReplacement.PrepareSubVoiceRevisionGate(
                project, instrument, voice, changes, stamp));
        SubVoice replacement = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, scope.Token);
        replacement.Events.Clear();
        replacement.Events.AdoptEditedSnapshot(project, voice.Events.CreateQuerySnapshot(), plan.Changes, plan.Appended, scope.Token);
        EnsureBoundedTemplateAppendMappings(project, voice.Events.CreateQuerySnapshot(), replacement,
            plan.Changes.Where(static e => !e.IsDeleted).Select(static e => e.Replacement).Concat(plan.Appended), ref nextId);
        ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(changes, voice, replacement, plan.AffectedIds);
        IPreparedProjectEdit root = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project, instrument,
            voice, replacement, changes, nextId == expectedId ? null : expectedId,
            nextId == expectedId ? null : nextId, stamp);
        root = new BoundedTemplateRootEdit(root, instrument, Math.Max(instrument.TemplateLengthTicks, plan.MaximumEndTick));
        return Finish(root);

        IPreparedProjectEdit Finish(IPreparedProjectEdit edit) => publishSelection
            ? WithSelectionPublication(plan.Own(edit), plan.OriginalSelection, plan.ResultSelection, publish)
            : plan.Own(edit);
    }

    private static int GetBoundedTemplatePointValue(TemplateEventSnapshotValue value, MidiValueTarget target) =>
        target.Kind is MidiValueKind.BankLsb or MidiValueKind.PitchBendRangeCents ? value.SecondaryValue : value.Value;

    private static TemplateEventSnapshotValue SetBoundedTemplatePointValue(TemplateEventSnapshotValue value,
        MidiValueTarget target, int number) => target.Kind is MidiValueKind.BankLsb or MidiValueKind.PitchBendRangeCents
            ? value with { SecondaryValue = number } : value with { Value = number };

    private static bool BoundedTemplateMatchesTarget(TemplateEventSnapshotValue value, MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => value.Kind == TemplateEventKind.ControlChange && value.Number == target.Number,
        MidiValueKind.BankMsb => value.Kind == TemplateEventKind.Bank && value.HasBankMsb,
        MidiValueKind.BankLsb => value.Kind == TemplateEventKind.Bank && value.HasBankLsb,
        MidiValueKind.Program => value.Kind == TemplateEventKind.Program,
        MidiValueKind.PitchBend => value.Kind == TemplateEventKind.PitchBend,
        MidiValueKind.RegisteredParameter => value.Kind == TemplateEventKind.RegisteredParameter && value.Number == target.Number,
        MidiValueKind.NonRegisteredParameter => value.Kind == TemplateEventKind.NonRegisteredParameter && value.Number == target.Number,
        MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents => value.Kind == TemplateEventKind.PitchBendRange,
        _ => false
    };

    internal static void EnsureBoundedTemplateAppendMappings(MidoraProject project, TemplateEventQuerySnapshot source,
        SubVoice replacement, IEnumerable<TemplateEventSnapshotValue> appended, ref long nextId)
    {
        // The same new-target rule as TemplateEventCollection.Add, without its
        // per-event full-owner Any scan or lazy allocation after compilation.
        HashSet<long> oldTargets = [.. source.DiscoveryKeys];
        HashSet<TemplateEventMappingTarget> mapped = [.. replacement.EventMappings.Select(static m => m.Target)];
        foreach (var value in appended)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            if (value.Kind == TemplateEventKind.Note)
            {
                Add(TemplateEventMappingTarget.Create(TemplateEventKind.Note, 0, TemplateEventMappingParameter.Number), ref nextId);
                Add(TemplateEventMappingTarget.Create(TemplateEventKind.Note, 0, TemplateEventMappingParameter.Value), ref nextId);
            }
            else
            {
                foreach (long key in TemplateEventMidiTargets.EnumerateDiscoveryKeys(value))
                    if (!oldTargets.Contains(key) && TemplateEventMidiTargets.TryDecodeDiscoveryKey(key, out var target))
                        Add(TemplateEventMidiTargets.ToMappingTarget(target), ref nextId);
            }
        }

        void Add(TemplateEventMappingTarget target, ref long id)
        {
            if (!mapped.Add(target)) return;
            replacement.EventMappings.Add(new SubVoiceEventMapping(project, target, MidoraId.FromSequence(checked(id++))));
        }
    }
}
