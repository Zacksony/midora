using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand PaintLogicalNoteVelocities(
        MidoraId segmentId,
        IReadOnlyDictionary<MidoraId, int> velocities) =>
        Command("Paint logical note velocities", project =>
        {
            ArgumentNullException.ThrowIfNull(velocities);
            if (velocities.Count == 0)
            {
                throw new ArgumentException("At least one Logical Note velocity is required.", nameof(velocities));
            }
            SegmentLocation location = FindSegment(project, segmentId);
            SelectedLogicalNote[] selected = SelectLogicalNotes(location.Segment, velocities.Keys.ToArray());
            LogicalNoteValue[] old = selected.Select(item => Snapshot(item.Note)).ToArray();
            LogicalNoteValue[] replacement = selected.Select(item =>
            {
                int velocity = velocities[item.Note.Id];
                return Snapshot(item.Note) with { Velocity = velocity };
            }).ToArray();
            ValidateLogicalNoteBatch(replacement);
            return PrepareLogicalNoteBatch(
                location.Track.Id,
                location.Segment.Notes,
                selected,
                old,
                replacement);
        });

    public static IProjectEditCommand PaintTemplateNoteVelocities(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyDictionary<MidoraId, int> velocities) =>
        Command("Paint template note velocities", project =>
        {
            ArgumentNullException.ThrowIfNull(velocities);
            if (velocities.Count == 0)
            {
                throw new ArgumentException("At least one Template Note velocity is required.", nameof(velocities));
            }
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            TemplateEvent[] notes = ResolveTemplateEventsByIds(
                voice.Events,
                velocities.Keys.ToArray());
            if (notes.Length != velocities.Count
                || notes.Any(item => item.Kind != TemplateEventKind.Note))
            {
                throw new ArgumentException("Velocity painting accepts Template Note events only.", nameof(velocities));
            }
            TemplateEventValue[] old = notes.Select(CaptureTemplateEvent).ToArray();
            TemplateEventValue[] replacement = notes
                .Select(item => CaptureTemplateEvent(item) with { Value = velocities[item.Id] })
                .ToArray();
            for (int index = 0; index < notes.Length; index++)
            {
                ValidateTemplateEventEdit(notes[index], replacement[index]);
            }
            return Prepared(
                old.Where((value, index) => value != replacement[index]).Any(),
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < notes.Length; index++) SetTemplateEvent(notes[index], replacement[index]);
                },
                _ =>
                {
                    using IDisposable batch = voice.Events.BeginBatchChange();
                    for (int index = 0; index < notes.Length; index++) SetTemplateEvent(notes[index], old[index]);
                });
        });
}
