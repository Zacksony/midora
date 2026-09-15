using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand DuplicateTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long newEarliestTick,
        int pitchDelta) =>
        Command("Duplicate template notes", project =>
        {
            if (newEarliestTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newEarliestTick));
            }
            ArgumentNullException.ThrowIfNull(noteIds);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
                return PrepareBoundedTemplateDuplicate(project, instrument, voice, noteIds, newEarliestTick, pitchDelta);
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEvent[] notes = ResolveTemplateEventsByIds(voice.Events, noteIds);
            if (notes.Length != requested.Count || notes.Any(item => item.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }

            long earliest = notes.Min(item => item.Tick);
            long tickDelta = checked(newEarliestTick - earliest);
            TemplateEventValue[] replacements = notes
                .Select(item => CaptureTemplateEvent(item) with
                {
                    Tick = checked(item.Tick + tickDelta),
                    Number = checked(item.Number + pitchDelta)
                })
                .ToArray();
            for (int index = 0; index < notes.Length; index++)
            {
                ValidateTemplateEventEdit(notes[index], replacements[index]);
            }

            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = replacements.Max(value => checked(value.Tick + value.LengthTicks));
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            int insertionIndex = voice.Events.Count;
            TemplateEvent[]? copies = null;
            return ResolveTargetedExactTemplateNoteCollisions(Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                owner =>
                {
                    if (copies is null)
                    {
                        copies = new TemplateEvent[notes.Length];
                        for (int index = 0; index < notes.Length; index++)
                        {
                            TemplateEvent copy = new(owner);
                            SetTemplateEvent(copy, replacements[index]);
                            copies[index] = copy;
                        }
                    }
                    voice.Events.InsertRange(insertionIndex, copies);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    if (copies is null)
                    {
                        throw new InvalidOperationException(
                            "Template Note copies do not exist before the first Apply.");
                    }
                    int removed = voice.Events.RemoveRange(copies);
                    if (removed != copies.Length)
                        throw new InvalidOperationException("The Template Note copy set is no longer present.");
                    instrument.TemplateLengthTicks = oldTemplateLength;
                }), replacements.Select(value => new TemplateNoteCollisionTarget(
                    voice,
                    value.Tick,
                    value.Number)));
        });

    public static IProjectEditCommand MoveTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long tickDelta,
        int pitchDelta) =>
        Command("Move template notes", project =>
        {
            ArgumentNullException.ThrowIfNull(noteIds);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
                return PrepareBoundedTemplateNotes(project, instrument, voice, noteIds, _ => value =>
                {
                    int pitch = checked(value.Number + pitchDelta);
                    long tick = checked(value.Tick + tickDelta);
                    ValidateLogicalNote(tick, value.LengthTicks, pitch is < 0 or > 127 ? 0 : pitch, value.Value);
                    return pitch is < 0 or > 127 ? null : value with { Tick = tick, Number = pitch };
                }, collisions: tickDelta != 0 || pitchDelta != 0);
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEvent[] notes = ResolveTemplateEventsByIds(voice.Events, noteIds);
            (TemplateEvent Note, int Index)[] selected = notes
                .Select(static item => (Note: item, Index: -1))
                .ToArray();
            if (selected.Length != requested.Count || selected.Any(item => item.Note.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }
            TemplateEventValue[] old = selected.Select(item => CaptureTemplateEvent(item.Note)).ToArray();
            TemplateEventValue[] replacement = old.Select(value => value with
            {
                Tick = checked(value.Tick + tickDelta),
                Number = checked(value.Number + pitchDelta)
            }).ToArray();
            bool[] discarded = replacement.Select(value => value.Number is < 0 or > 127).ToArray();
            TemplateEvent[] discardedNotes = selected
                .Where((_, index) => discarded[index])
                .Select(static value => value.Note)
                .ToArray();
            Action? restoreDiscarded = null;
            for (int index = 0; index < selected.Length; index++)
            {
                ValidateTemplateEventEdit(
                    selected[index].Note,
                    discarded[index] ? replacement[index] with { Number = 0 } : replacement[index]);
            }
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = replacement
                .Where((_, index) => !discarded[index])
                .Select(value => checked(value.Tick + value.LengthTicks))
                .DefaultIfEmpty(oldTemplateLength)
                .Max();
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            IPreparedProjectEdit prepared = Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < selected.Length; index++)
                    {
                        if (!discarded[index])
                        {
                            SetTemplateEvent(selected[index].Note, replacement[index]);
                        }
                    }
                    if (discardedNotes.Length != 0)
                        restoreDiscarded = voice.Events.RemoveRangeWithUndo(discardedNotes);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < selected.Length; index++)
                    {
                        SetTemplateEvent(selected[index].Note, old[index]);
                    }
                    if (discardedNotes.Length != 0)
                    {
                        (restoreDiscarded ?? throw new InvalidOperationException(
                            "Discarded Template Notes do not have a pending removal to restore."))();
                        restoreDiscarded = null;
                    }
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
            TemplateNoteCollisionTarget[] collisionTargets = replacement
                .Where((value, index) => !discarded[index]
                    && (value.Tick != old[index].Tick
                        || value.Number != old[index].Number))
                .Select(value => new TemplateNoteCollisionTarget(
                    voice,
                    value.Tick,
                    value.Number))
                .ToArray();
            return collisionTargets.Length == 0
                ? prepared
                : ResolveTargetedExactTemplateNoteCollisions(prepared, collisionTargets);
        });

    public static IProjectEditCommand AdjustTemplateNoteEdges(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks = 1) =>
        PrepareTemplateNoteBatch(
            "Resize template notes",
            eventInstrumentId,
            subVoiceId,
            noteIds,
            startDelta,
            endDelta,
            minimumLengthTicks);

    private static TemplateEventValue AdjustTemplateNoteEdgesSaturated(
        TemplateEventValue value,
        long startDelta,
        long endDelta,
        long minimumLengthTicks)
    {
        long oldEnd = checked(value.Tick + value.LengthTicks);
        long effectiveMinimumLengthTicks = Math.Min(value.LengthTicks, minimumLengthTicks);
        long requestedStart = checked(value.Tick + startDelta);
        long requestedEnd = checked(oldEnd + endDelta);
        long start;
        long end;
        if (startDelta != 0 && endDelta == 0)
        {
            start = Math.Clamp(
                requestedStart,
                0,
                Math.Max(0, checked(oldEnd - effectiveMinimumLengthTicks)));
            end = oldEnd;
        }
        else if (startDelta == 0)
        {
            start = value.Tick;
            end = Math.Max(checked(start + effectiveMinimumLengthTicks), requestedEnd);
        }
        else
        {
            start = Math.Max(0, requestedStart);
            end = Math.Max(checked(start + effectiveMinimumLengthTicks), requestedEnd);
        }
        return value with
        {
            Tick = start,
            LengthTicks = checked(end - start)
        };
    }

    private static IProjectEditCommand PrepareTemplateNoteBatch(
        string commandName,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        long startDelta,
        long endDelta,
        long minimumLengthTicks) =>
        Command(commandName, project =>
        {
            if (minimumLengthTicks < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumLengthTicks));
            ArgumentNullException.ThrowIfNull(noteIds);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
                return PrepareBoundedTemplateNotes(project, instrument, voice, noteIds, selected =>
                {
                    long delta = startDelta < 0 ? Math.Max(startDelta, -selected.Min(v => v.Value.Tick)) : startDelta;
                    return value =>
                    {
                        var result = AdjustLogicalNoteEdgesSaturated(new(value.Tick, value.LengthTicks, value.Number, value.Value),
                            delta, endDelta, minimumLengthTicks);
                        return value with { Tick = result.StartTick, LengthTicks = result.LengthTicks };
                    };
                }, collisions: startDelta != 0);
            HashSet<MidoraId> requested = ValidateBatchIds(noteIds, nameof(noteIds), "Template Note");
            TemplateEvent[] notes = ResolveTemplateEventsByIds(voice.Events, noteIds);
            if (notes.Length != requested.Count || notes.Any(item => item.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a Template Note in the target SubVoice.",
                    nameof(noteIds));
            }
            TemplateEventValue[] old = notes.Select(CaptureTemplateEvent).ToArray();
            long boundedStartDelta = startDelta < 0
                ? Math.Max(startDelta, -old.Min(value => value.Tick))
                : startDelta;
            TemplateEventValue[] replacement = old
                .Select(value => AdjustTemplateNoteEdgesSaturated(
                    value,
                    boundedStartDelta,
                    endDelta,
                    minimumLengthTicks))
                .ToArray();
            for (int index = 0; index < notes.Length; index++)
            {
                ValidateTemplateEventEdit(notes[index], replacement[index]);
            }
            long oldTemplateLength = instrument.TemplateLengthTicks;
            long requiredBoundary = replacement.Max(value => checked(value.Tick + value.LengthTicks));
            long replacementTemplateLength = Math.Max(oldTemplateLength, requiredBoundary);
            IPreparedProjectEdit prepared = Prepared(
                old.Where((value, index) => value != replacement[index]).Any()
                    || oldTemplateLength != replacementTemplateLength,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < notes.Length; index++) SetTemplateEvent(notes[index], replacement[index]);
                    instrument.TemplateLengthTicks = replacementTemplateLength;
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < notes.Length; index++) SetTemplateEvent(notes[index], old[index]);
                    instrument.TemplateLengthTicks = oldTemplateLength;
                });
            return startDelta == 0
                ? prepared
                : ResolveTargetedExactTemplateNoteCollisions(
                    prepared,
                    replacement.Select(value => new TemplateNoteCollisionTarget(
                        voice,
                        value.Tick,
                        value.Number)));
        });

    private static TemplateEvent[] ResolveTemplateEventsByIds(
        TemplateEventCollection events,
        IReadOnlyCollection<MidoraId> ids)
        => events.ResolveByIdsInCollectionOrder(ids).ToArray();
}
