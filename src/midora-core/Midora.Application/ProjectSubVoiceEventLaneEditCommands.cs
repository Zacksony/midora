using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreateSubVoiceEventLane(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidiValueTarget target) =>
        Command("Create SubVoice event lane", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEventMappingTarget mappingTarget = TemplateEventMidiTargets.ToMappingTarget(target);
            if (voice.EventMappings.Any(value => value.Target == mappingTarget))
            {
                throw new InvalidOperationException(
                    "The requested SubVoice event lane already exists.");
            }

            SubVoiceEventMapping? created = null;
            int insertionIndex = voice.EventMappings.Count;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    created ??= new SubVoiceEventMapping(owner, mappingTarget);
                    InsertAt(
                        voice.EventMappings,
                        insertionIndex,
                        created,
                        "SubVoice event Mapping");
                },
                _ => RemoveRequired(
                    voice.EventMappings,
                    created ?? throw new InvalidOperationException(
                        "The SubVoice event Mapping does not exist before Undo."),
                    "SubVoice event Mapping"));
        });

    public static IProjectEditCommand AdjustSubVoiceEventPoints(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> templateEventIds,
        MidiValueTarget target,
        long tickDelta,
        int valueDelta,
        bool duplicate) =>
        templateEventIds.Count >= BoundedPointThreshold
        ? BoundedTemplatePoints(duplicate ? "Duplicate SubVoice event points" : "Move SubVoice event points",
            eventInstrumentId, subVoiceId, templateEventIds, BoundedPointOperation.Adjust, target, tickDelta, valueDelta, duplicate)
        :
        Command(duplicate ? "Duplicate SubVoice event points" : "Move SubVoice event points", project =>
        {
            ArgumentNullException.ThrowIfNull(templateEventIds);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return BoundedTemplatePoints(duplicate ? "Duplicate SubVoice event points" : "Move SubVoice event points",
                    eventInstrumentId, subVoiceId, templateEventIds, BoundedPointOperation.Adjust, target, tickDelta, valueDelta, duplicate).Prepare(project);
            HashSet<MidoraId> requested = ValidateBatchIds(
                templateEventIds,
                nameof(templateEventIds),
                "Template Event");
            IndexedEventLaneTemplateEvent[] selected = voice.Events
                .ResolveByIdsWithIndicesInCollectionOrder(requested)
                .Select(static value => new IndexedEventLaneTemplateEvent(
                    value.Value,
                    value.Index,
                    CaptureTemplateEvent(value.Value)))
                .ToArray();
            if (selected.Length != requested.Count
                || selected.Any(value => value.Event.Kind == TemplateEventKind.Note
                    || !TemplateEventMidiTargets.Enumerate(value.Event).Contains(target)))
            {
                throw new ArgumentException(
                    "Every selected ID must identify an event point in the requested SubVoice lane.",
                    nameof(templateEventIds));
            }

            TemplateEventValue[] replacements = selected
                .Select(value => AdjustEventPointValue(
                    value.Original with { Tick = checked(value.Original.Tick + tickDelta) },
                    target,
                    valueDelta))
                .ToArray();
            for (int index = 0; index < selected.Length; index++)
            {
                ValidateTemplateEventEdit(selected[index].Event, replacements[index]);
            }

            // Same-target, same-tick point conflicts are resolved at the edit
            // transaction boundary. A newcomer from this edit replaces the former
            // point, so drag, copy-drag and paste share one deterministic policy.

            long oldTemplateLength = instrument.TemplateLengthTicks;
            long replacementTemplateLength = Math.Max(
                oldTemplateLength,
                replacements.Max(value => checked(value.Tick + 1)));
            int insertionIndex = voice.Events.Count;
            TemplateEvent[]? copies = null;
            IPreparedProjectEdit prepared = Prepared(
                duplicate || selected.Where((value, index) => value.Original != replacements[index]).Any(),
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    if (duplicate)
                    {
                        if (copies is null)
                        {
                            copies = replacements.Select(value =>
                            {
                                TemplateEvent copy = new TemplateEvent(owner);
                                SetTemplateEvent(copy, value);
                                return copy;
                            }).ToArray();
                        }
                        voice.Events.InsertRange(insertionIndex, copies);
                    }
                    else
                    {
                        using IDisposable batch = voice.Events.BeginBatchChange();
                        for (int index = 0; index < selected.Length; index++)
                        {
                            SetTemplateEvent(selected[index].Event, replacements[index]);
                        }
                    }
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (duplicate)
                    {
                        if (copies is null)
                        {
                            throw new InvalidOperationException(
                                "Template Event copies do not exist before the first Apply.");
                        }
                        int removed = voice.Events.RemoveRange(copies);
                        if (removed != copies.Length)
                            throw new InvalidOperationException("The Template Event copy set is no longer present.");
                    }
                    else
                    {
                        using IDisposable batch = voice.Events.BeginBatchChange();
                        foreach (IndexedEventLaneTemplateEvent value in selected)
                        {
                            SetTemplateEvent(value.Event, value.Original);
                        }
                    }
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
            return duplicate || tickDelta != 0
                ? ResolveTargetedExactTemplateEventPointCollisions(
                    prepared,
                    replacements.SelectMany(value =>
                        CreateTemplateEventPointCollisionTargets(voice, value)))
                : prepared;
        });

    public static IProjectEditCommand DeleteSubVoiceEventLane(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        MidiValueTarget target,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete SubVoice event lane", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return PrepareBoundedTemplateLaneDelete(project, instrument, voice, target, nonEmptyDeletionConfirmed);
            TemplateEventMappingTarget requestedTarget = TemplateEventMidiTargets.ToMappingTarget(target);
            TemplateEventMappingTarget[] mappingTargets = target.Kind is
                MidiValueKind.PitchBendRangeSemitones or MidiValueKind.PitchBendRangeCents
                    ?
                    [
                        TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.PitchBendRangeSemitones),
                        TemplateEventMidiTargets.ToMappingTarget(MidiValueTarget.PitchBendRangeCents)
                    ]
                    : [requestedTarget];
            IndexedSubVoiceEventMapping[] mappings = voice.EventMappings
                .Select((value, index) => new IndexedSubVoiceEventMapping(value, index))
                .Where(value => mappingTargets.Contains(value.Mapping.Target))
                .ToArray();
            if (mappings.All(value => value.Mapping.Target != requestedTarget))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(target),
                    "The requested SubVoice event lane does not exist.");
            }

            IndexedEventLaneTemplateEvent[] affected = TemplateEventTargetQueryIndex
                .Query(voice.Events, target)
                .Select(static value => new IndexedEventLaneTemplateEvent(
                    value.Value,
                    value.Index,
                    CaptureTemplateEvent(value.Value)))
                .ToArray();
            if (affected.Length != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty SubVoice event lane requires explicit confirmation.");
            }

            bool bankComponent = target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb;
            IndexedEventLaneTemplateEvent[] retainedBankEvents = bankComponent
                ? affected.Where(value => target.Kind == MidiValueKind.BankMsb
                        ? value.Event.HasBankLsb
                        : value.Event.HasBankMsb)
                    .ToArray()
                : [];
            HashSet<MidoraId> retainedIds = retainedBankEvents
                .Select(value => value.Event.Id)
                .ToHashSet();
            IndexedEventLaneTemplateEvent[] removedEvents = affected
                .Where(value => !retainedIds.Contains(value.Event.Id))
                .ToArray();
            TemplateEvent[] removedValues = removedEvents
                .Select(static value => value.Event)
                .ToArray();
            Action? restoreRemovedEvents = null;

            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    foreach (IndexedEventLaneTemplateEvent value in retainedBankEvents)
                    {
                        TemplateEventValue replacement = value.Original with
                        {
                            HasBankMsb = target.Kind != MidiValueKind.BankMsb
                                && value.Original.HasBankMsb,
                            HasBankLsb = target.Kind != MidiValueKind.BankLsb
                                && value.Original.HasBankLsb
                        };
                        SetTemplateEvent(value.Event, replacement);
                    }
                    if (removedValues.Length != 0)
                        restoreRemovedEvents = voice.Events.RemoveRangeWithUndo(removedValues);
                    foreach (IndexedSubVoiceEventMapping value in mappings)
                    {
                        RemoveRequired(voice.EventMappings, value.Mapping, "SubVoice event Mapping");
                    }
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    foreach (IndexedSubVoiceEventMapping value in mappings.OrderBy(value => value.Index))
                    {
                        InsertAt(
                            voice.EventMappings,
                            value.Index,
                            value.Mapping,
                            "SubVoice event Mapping");
                    }
                    foreach (IndexedEventLaneTemplateEvent value in retainedBankEvents)
                    {
                        SetTemplateEvent(value.Event, value.Original);
                    }
                    if (removedValues.Length != 0)
                    {
                        (restoreRemovedEvents ?? throw new InvalidOperationException(
                            "Deleted Template Events do not have a pending removal to restore."))();
                        restoreRemovedEvents = null;
                    }
                });
        });

    private readonly record struct IndexedSubVoiceEventMapping(
        SubVoiceEventMapping Mapping,
        int Index);

    private readonly record struct IndexedEventLaneTemplateEvent(
        TemplateEvent Event,
        int Index,
        TemplateEventValue Original);

    private static TemplateEventValue AdjustEventPointValue(
        TemplateEventValue value,
        MidiValueTarget target,
        int valueDelta) =>
        target.Kind is MidiValueKind.BankLsb or MidiValueKind.PitchBendRangeCents
            ? value with { SecondaryValue = checked(value.SecondaryValue + valueDelta) }
            : value with { Value = checked(value.Value + valueDelta) };

    private static bool TemplateEventValuesConflict(
        TemplateEventValue left,
        TemplateEventValue right)
    {
        if (left.Tick != right.Tick
            || left.Kind == TemplateEventKind.Note
            || right.Kind == TemplateEventKind.Note)
        {
            return false;
        }
        return TemplateEventExactCollision.Conflicts(
            left.Kind,
            left.Number,
            left.HasBankMsb,
            left.HasBankLsb,
            right.Kind,
            right.Number,
            right.HasBankMsb,
            right.HasBankLsb);
    }
}
