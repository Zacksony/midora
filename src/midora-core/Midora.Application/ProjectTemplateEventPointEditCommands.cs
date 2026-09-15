using Midora.Domain;

namespace Midora.Application;

public sealed record TemplateEventPointEdit(long Tick, int Value);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpsertTemplateEventPoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        IReadOnlyCollection<TemplateEventPointEdit> points) =>
        points.Count >= BoundedPointThreshold
        ? BoundedUpsertTemplatePoints(eventInstrumentId, subVoiceId, target, points)
        :
        Command("Draw template event points", project =>
        {
            ArgumentNullException.ThrowIfNull(points);
            if (points.Count == 0)
            {
                throw new ArgumentException("At least one Template Event point is required.", nameof(points));
            }
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return BoundedUpsertTemplatePoints(eventInstrumentId, subVoiceId, target, points).Prepare(project);
            TemplateEventPointEdit[] edits = points
                .OrderBy(value => value.Tick)
                .ToArray();
            if (edits.Select(value => value.Tick).Distinct().Count() != edits.Length)
            {
                throw new ArgumentException("Template Event point ticks must be unique.", nameof(points));
            }
            foreach (TemplateEventPointEdit edit in edits)
            {
                ValidateTemplateEventPoint(target, edit);
            }

            HashSet<long> editedTicks = edits
                .Select(static value => value.Tick)
                .ToHashSet();
            MidoraId[] candidateIds = voice.Events.CreateQuerySnapshot()
                .QueryEventTicks(editedTicks)
                .Select(static value => value.Id)
                .ToArray();
            Dictionary<long, TemplateEvent[]> candidatesByTick = voice.Events
                .ResolveByIdsInCollectionOrder(candidateIds)
                .GroupBy(static value => value.Tick)
                .ToDictionary(
                    static values => values.Key,
                    static values => values.ToArray());
            List<ExistingTemplateEventPointEdit> existing = [];
            List<TemplateEventPointEdit> additions = [];
            foreach (TemplateEventPointEdit edit in edits)
            {
                TemplateEvent? current = FindTemplateEventForTarget(
                    candidatesByTick.GetValueOrDefault(edit.Tick, []),
                    target);
                if (current is null)
                {
                    additions.Add(edit);
                }
                else
                {
                    existing.Add(new(current, CaptureTemplateEvent(current), edit.Value));
                }
            }

            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = edits.Max(value => checked(value.Tick + 1));
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            TemplateEvent[]? created = null;
            bool existingChanges = existing.Any(value =>
                !TemplateEventMidiTargets.Enumerate(value.Event).Contains(target)
                || TemplateEventMidiTargets.GetValue(value.Event, target) != value.Value);
            return ResolveTargetedExactTemplateEventPointCollisions(Prepared(
                existingChanges || additions.Count != 0 || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    foreach (ExistingTemplateEventPointEdit edit in existing)
                    {
                        SetTemplateEventTarget(edit.Event, target, edit.Value);
                    }
                    if (created is null)
                    {
                        created = additions
                            .Select(value => CreateTemplateEventPoint(owner, target, value))
                            .ToArray();
                    }
                    foreach (TemplateEvent value in created)
                    {
                        if (voice.Events.TryGetById(value.Id, out _))
                        {
                            throw new InvalidOperationException("A pasted Template Event point stable ID is already present.");
                        }
                        voice.Events.Add(value);
                    }
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    if (created is null)
                    {
                        throw new InvalidOperationException("Template Event points do not exist before the first Apply.");
                    }
                    int removed = voice.Events.RemoveRange(created);
                    if (removed != created.Length)
                        throw new InvalidOperationException("The drawn Template Event point set is no longer present.");
                    foreach (ExistingTemplateEventPointEdit edit in existing)
                    {
                        SetTemplateEvent(edit.Event, edit.OldValue);
                    }
                    instrument.TemplateLengthTicks = oldTemplateLength;
                }), edits.SelectMany(edit => GetTemplateEventPointCollisionDetails(target)
                    .Select(detail => new TemplateEventPointCollisionTarget(
                        voice,
                        edit.Tick,
                        detail))));
        });

    private static TemplateEvent? FindTemplateEventForTarget(
        IEnumerable<TemplateEvent> candidates,
        MidiValueTarget target)
    {
        return target.Kind switch
        {
            MidiValueKind.BankMsb or MidiValueKind.BankLsb =>
                candidates.SingleOrDefault(value => value.Kind == TemplateEventKind.Bank),
            MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents =>
                candidates.SingleOrDefault(value => value.Kind == TemplateEventKind.PitchBendRange),
            _ => candidates.SingleOrDefault(value =>
                TemplateEventMidiTargets.Enumerate(value).Contains(target))
        };
    }

    private static int[] GetTemplateEventPointCollisionDetails(MidiValueTarget target) =>
        target.Kind switch
        {
            MidiValueKind.ControlChange => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.ControlChange,
                target.Number,
                false,
                false),
            MidiValueKind.BankMsb => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.Bank,
                0,
                true,
                false),
            MidiValueKind.BankLsb => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.Bank,
                0,
                false,
                true),
            MidiValueKind.Program => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.Program,
                0,
                false,
                false),
            MidiValueKind.PitchBend => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.PitchBend,
                0,
                false,
                false),
            MidiValueKind.RegisteredParameter => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.RegisteredParameter,
                target.Number,
                false,
                false),
            MidiValueKind.NonRegisteredParameter => TemplateEventExactCollision.GetNonNoteDetails(
                TemplateEventKind.NonRegisteredParameter,
                target.Number,
                false,
                false),
            MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents =>
                TemplateEventExactCollision.GetNonNoteDetails(
                    TemplateEventKind.PitchBendRange,
                    0,
                    false,
                    false),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

    private static void ValidateTemplateEventPoint(MidiValueTarget target, TemplateEventPointEdit point)
    {
        if (point.Tick < 0 || point.Tick == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(point.Tick));
        }
        bool valid = target.Kind switch
        {
            MidiValueKind.ControlChange => target.Number is >= 0 and <= 119 and not 91 and not 93
                && point.Value is >= 0 and <= 127,
            MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program
                or MidiValueKind.PitchBendRangeSemitones => point.Value is >= 0 and <= 127,
            MidiValueKind.PitchBend => point.Value is >= -8192 and <= 8191,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter =>
                target.Number is >= 0 and <= 16_383 && point.Value is >= 0 and <= 16_383,
            MidiValueKind.PitchBendRangeCents => point.Value is >= 0 and <= 99,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(nameof(point));
        }
    }

    private static TemplateEvent CreateTemplateEventPoint(
        MidoraProject project,
        MidiValueTarget target,
        TemplateEventPointEdit point)
    {
        TemplateEvent result = new(project)
        {
            Tick = point.Tick,
            HasBankMsb = false,
            HasBankLsb = false
        };
        SetTemplateEventTarget(result, target, point.Value);
        return result;
    }

    private static void SetTemplateEventTarget(
        TemplateEvent targetEvent,
        MidiValueTarget target,
        int value)
    {
        TemplateEventKind kind = targetEvent.Kind;
        int number = targetEvent.Number;
        int primaryValue = targetEvent.Value;
        int secondaryValue = targetEvent.SecondaryValue;
        bool hasBankMsb = targetEvent.HasBankMsb;
        bool hasBankLsb = targetEvent.HasBankLsb;
        switch (target.Kind)
        {
            case MidiValueKind.ControlChange:
                kind = TemplateEventKind.ControlChange;
                number = target.Number;
                primaryValue = value;
                break;
            case MidiValueKind.BankMsb:
                kind = TemplateEventKind.Bank;
                hasBankMsb = true;
                primaryValue = value;
                break;
            case MidiValueKind.BankLsb:
                kind = TemplateEventKind.Bank;
                hasBankLsb = true;
                secondaryValue = value;
                break;
            case MidiValueKind.Program:
                kind = TemplateEventKind.Program;
                primaryValue = value;
                break;
            case MidiValueKind.PitchBend:
                kind = TemplateEventKind.PitchBend;
                primaryValue = value;
                break;
            case MidiValueKind.RegisteredParameter:
                kind = TemplateEventKind.RegisteredParameter;
                number = target.Number;
                primaryValue = value;
                break;
            case MidiValueKind.NonRegisteredParameter:
                kind = TemplateEventKind.NonRegisteredParameter;
                number = target.Number;
                primaryValue = value;
                break;
            case MidiValueKind.PitchBendRangeSemitones:
                kind = TemplateEventKind.PitchBendRange;
                primaryValue = value;
                break;
            case MidiValueKind.PitchBendRangeCents:
                kind = TemplateEventKind.PitchBendRange;
                secondaryValue = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }
        targetEvent.SetValues(
            kind,
            targetEvent.Tick,
            targetEvent.LengthTicks,
            number,
            primaryValue,
            secondaryValue,
            hasBankMsb,
            hasBankLsb,
            targetEvent.FollowPitchDelta);
        targetEvent.EnsureMappings();
    }

    private sealed record ExistingTemplateEventPointEdit(
        TemplateEvent Event,
        TemplateEventValue OldValue,
        int Value);
}
