using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private readonly record struct BoundedTemplateDrawPoint(long Tick, int Value, int Order);
    private static IProjectEditCommand BoundedUpsertTemplatePoints(MidoraId instrumentId, MidoraId voiceId,
        MidiValueTarget target, IReadOnlyCollection<TemplateEventPointEdit> points) =>
        BoundedUpsertTemplatePoints(instrumentId, voiceId, target, _ => points, mergeRepeatedTicks: false);

    public static IProjectEditCommand DrawTemplateEventPoints(MidoraId instrumentId, MidoraId voiceId,
        MidiValueTarget target, Func<CancellationToken, IEnumerable<TemplateEventPointEdit>> points) =>
        BoundedUpsertTemplatePoints(instrumentId, voiceId, target, points, mergeRepeatedTicks: true);

    private static IProjectEditCommand BoundedUpsertTemplatePoints(MidoraId instrumentId, MidoraId voiceId,
        MidiValueTarget target, Func<CancellationToken, IEnumerable<TemplateEventPointEdit>> points, bool mergeRepeatedTicks) =>
        new BoundedProjectEditCommand("Draw template event points", (project, token, progress) =>
        {
            var scope = BulkEditPreparationContext.Current!;
            EventInstrument instrument = FindEventInstrument(project, instrumentId);
            SubVoice voice = FindSubVoice(instrument, voiceId);
            var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            var source = voice.Events.CreateQuerySnapshot();
            source.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, token);
            using var ordered = BoundedEditSort.Sort(points(token).Select(static (p, index) => new BoundedTemplateDrawPoint(p.Tick, p.Value, index)),
                Comparer<BoundedTemplateDrawPoint>.Create((a, b) =>
                { int tick = a.Tick.CompareTo(b.Tick); return tick != 0 ? tick : a.Order.CompareTo(b.Order); }), scope.Resources, token);
            long expectedId = project.NextStableId, nextId = expectedId;
            using var plan = BoundedTemplatePointPlan.Build(source, Rows());
            return PublishBoundedTemplatePoints(project, instrument, voice, stamp, plan, expectedId, nextId,
                static ids => ids, publishSelection: false);

            IEnumerable<BoundedTemplatePointPlan.Row> Rows()
            {
                int additionOrdinal = source.Count, processed = 0;
                foreach (var point in FinalPoints())
                {
                    scope.Checkpoint(processed++, ordered.Count);
                    ValidateTemplateEventPoint(target, new(point.Tick, point.Value));
                    TemplateEventSnapshotValue? found = null;
                    foreach (var candidate in source.EnumerateEventExactTick(point.Tick))
                    {
                        token.ThrowIfCancellationRequested();
                        bool matches = target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb
                            ? candidate.Kind == TemplateEventKind.Bank
                            : target.Kind is MidiValueKind.PitchBendRangeCents or MidiValueKind.PitchBendRangeSemitones
                                ? candidate.Kind == TemplateEventKind.PitchBendRange : BoundedTemplateMatchesTarget(candidate, target);
                        if (!matches) continue;
                        if (found is not null) throw new InvalidOperationException("More than one event occupies the requested lane tick.");
                        found = candidate;
                    }
                    bool created = found is null;
                    var old = found.GetValueOrDefault();
                    var value = created ? new TemplateEventSnapshotValue(MidoraId.FromSequence(checked(nextId++)),
                        default, point.Tick, 0, 0, 0, 0, false, false, true) : old;
                    value = AssignBoundedTemplateTarget(value, target, point.Value);
                    ValidateTemplateEventValue(value);
                    int ordinal = additionOrdinal;
                    if (created) additionOrdinal++;
                    else if (!source.TryFindOrdinalById(old.Id, out ordinal)) throw new InvalidOperationException("The selected event identity is missing.");
                    var details = TemplateEventExactCollision.Details(value);
                    yield return new(ordinal, old, value, created, false, details.First, details.Second, details.Count);
                }
            }

            IEnumerable<BoundedTemplateDrawPoint> FinalPoints()
            {
                BoundedTemplateDrawPoint? pending = null;
                foreach (var point in ordered.ReadValues(token))
                {
                    token.ThrowIfCancellationRequested();
                    ValidateTemplateEventPoint(target, new(point.Tick, point.Value));
                    if (pending is { } previous)
                    {
                        if (previous.Tick != point.Tick) yield return previous;
                        else if (!mergeRepeatedTicks) throw new ArgumentException("Template Event point ticks must be unique.", nameof(points));
                    }
                    pending = point;
                }
                if (pending is { } last) yield return last;
            }
        });

    private static TemplateEventSnapshotValue AssignBoundedTemplateTarget(TemplateEventSnapshotValue value,
        MidiValueTarget target, int number) => target.Kind switch
    {
        MidiValueKind.ControlChange => value with { Kind = TemplateEventKind.ControlChange, Number = target.Number, Value = number },
        MidiValueKind.BankMsb => value with { Kind = TemplateEventKind.Bank, HasBankMsb = true, Value = number },
        MidiValueKind.BankLsb => value with { Kind = TemplateEventKind.Bank, HasBankLsb = true, SecondaryValue = number },
        MidiValueKind.Program => value with { Kind = TemplateEventKind.Program, Value = number },
        MidiValueKind.PitchBend => value with { Kind = TemplateEventKind.PitchBend, Value = number },
        MidiValueKind.RegisteredParameter => value with { Kind = TemplateEventKind.RegisteredParameter, Number = target.Number, Value = number },
        MidiValueKind.NonRegisteredParameter => value with { Kind = TemplateEventKind.NonRegisteredParameter, Number = target.Number, Value = number },
        MidiValueKind.PitchBendRangeSemitones => value with { Kind = TemplateEventKind.PitchBendRange, Value = number },
        MidiValueKind.PitchBendRangeCents => value with { Kind = TemplateEventKind.PitchBendRange, SecondaryValue = number },
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };
}
