using System.Globalization;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop;

internal static partial class ObjectPropertiesProjection
{
    // A fixed number of scalar accumulators, not a deferred collection that
    // rereads a cold source once for every Properties field.
    internal abstract class PropertySummary<T>
    {
        public abstract void Add(T value);
        public abstract PropertyField Field();
        public static PropertySummary<T, TValue> Create<TValue>(string key, string label,
            Func<T, TValue> select, bool editable = true) => new(key, label, select, editable);
    }

    internal sealed class PropertySummary<T, TValue>(string key, string label,
        Func<T, TValue> select, bool editable) : PropertySummary<T>
    {
        private bool _hasValue;
        public TValue First { get; private set; } = default!;
        public bool Same { get; private set; } = true;
        public override void Add(T value)
        {
            if (!Same) return;
            TValue candidate = select(value);
            if (!_hasValue) { First = candidate; _hasValue = true; }
            else Same = EqualityComparer<TValue>.Default.Equals(First, candidate);
        }
        public override PropertyField Field() => new(key, label,
            Same ? Convert.ToString(First, CultureInfo.InvariantCulture) ?? string.Empty : "Mixed",
            editable, Same ? PropertyFieldValueState.SameValue : PropertyFieldValueState.Mixed,
            typeof(TValue).IsEnum ? Enum.GetNames(typeof(TValue)) : null,
            typeof(TValue) == typeof(bool));
    }

    internal static bool SummarizePropertyOwner<T>(MidoraProject project,
        ITimelineObjectSource<T> source, IReadOnlyCollection<MidoraId> ids,
        IReadOnlyList<PropertySummary<T>> fields, bool knownOwner = false) where T : unmanaged
    {
        if (ids.Count == 0 || source.Count < ids.Count) return false;
        CancellationToken token = ReadProgress.Value?.Token ?? default;
        IProgress<TimelineEditPreparationProgress>? progress = ReadProgress.Value?.Progress;
        // Every valid homogeneous owner must contain the first stable identity.
        // Probe one ID instead of resolving the entire selection in each wrong
        // note/event/parameter owner. Do not report this unknown routing work as
        // a fabricated fraction of the later N-record read.
        progress?.Report(new(TimelineEditPreparationPhase.ResolvingSelection, 0, 0));
        if (!knownOwner)
        {
            bool ownsFirst = false;
            foreach (T _ in ProjectTimelineReadPreparation.ReadSelectedValues(project, source,
                new HashSet<MidoraId> { ids.First() }, token, requireAll: false, preserveFormalOrder: false)) ownsFirst = true;
            if (!ownsFirst) return false;
        }
        int count = 0;
        foreach (T value in ProjectTimelineReadPreparation.ReadSelectedValues(project, source,
            ids, token, progress, requireAll: false, preserveFormalOrder: false))
        {
            token.ThrowIfCancellationRequested();
            for (int index = 0; index < fields.Count; index++) fields[index].Add(value);
            count++;
        }
        return count == ids.Count;
    }

    private static void RebuildMultiSelection(ObjectPropertiesViewModel properties,
        MidoraProject project, ObjectPropertiesSelectionContext selection)
    {
        IReadOnlySet<MidoraId> ids = selection.Ids;
        if (selection.Mode == TimelineWorkspaceMode.Arrangement)
        {
            PropertySummary<ArrangementSegmentProperty>[] fields =
            [PropertySummary<ArrangementSegmentProperty>.Create("batch.segment.start", "PROJECT START TICK", static x => x.StartTick),
             PropertySummary<ArrangementSegmentProperty>.Create("batch.segment.length", "LENGTH TICKS", static x => x.LengthTicks),
             PropertySummary<ArrangementSegmentProperty>.Create("batch.segment.offset", "CONTENT OFFSET TICK", static x => x.ContentOffsetTick)];
            long total = project.Tracks.Sum(static t => (long)t.Segments.Count)
                + project.PureMidiTracks.Sum(static t => (long)t.Segments.Count);
            long visited = 0;
            int count = 0;
            foreach (var value in project.Tracks.SelectMany(static t => t.Segments)
                .Select(static s => new ArrangementSegmentProperty(s.Id, s.ProjectStartTick, s.LengthTicks, s.ContentOffsetTick))
                .Concat(project.PureMidiTracks.SelectMany(static t => t.Segments)
                    .Select(static s => new ArrangementSegmentProperty(s.Id, s.ProjectStartTick, s.LengthTicks, s.ContentOffsetTick))))
            {
                ReadProgress.Value?.Checkpoint();
                if (ids.Contains(value.Id)) { count++; foreach (var field in fields) field.Add(value); }
                if ((++visited & 255) == 0 || visited == total)
                    ReadProgress.Value?.Progress?.Report(new(TimelineEditPreparationPhase.ReadingSelection, visited, total));
            }
            if (count == ids.Count && count != 0)
            {
                Replace($"{count} Segments", "Logical and MIDI Segments share one transactional property editor.", fields);
                return;
            }
        }
        if (selection.Mode == TimelineWorkspaceMode.Segment
            && TimelineWorkspaceViewModel.FindSegment(project, selection.ObjectId) is { } location)
        {
            PropertySummary<LogicalNoteSnapshotValue>[] notes =
            [PropertySummary<LogicalNoteSnapshotValue>.Create("batch.note.start", "START TICK", static x => x.StartTick),
             PropertySummary<LogicalNoteSnapshotValue>.Create("batch.note.length", "LENGTH TICKS", static x => x.LengthTicks),
             PropertySummary<LogicalNoteSnapshotValue>.Create("batch.note.number", "MIDI NOTE", static x => x.Note),
             PropertySummary<LogicalNoteSnapshotValue>.Create("batch.note.velocity", "VELOCITY", static x => x.Velocity)];
            if (Allows(WorkspaceTimelineSelectionKind.LogicalNote, location.Segment.Id)
                && SummarizePropertyOwner(project, location.Segment.Notes.CreateQuerySnapshot(), ids, notes, Known(WorkspaceTimelineSelectionKind.LogicalNote, location.Segment.Id)))
            {
                Replace($"{ids.Count} Logical Notes", "Common fields use one atomic Exact Set operation. Mixed is distinct from Unavailable.", notes);
                return;
            }
            foreach (var lane in location.Segment.ParameterLanes)
            {
                PropertySummary<CurvePointSnapshotValue>[] points =
                [PropertySummary<CurvePointSnapshotValue>.Create("batch.parameterPoint.tick", "TICK", static x => x.Tick),
                 PropertySummary<CurvePointSnapshotValue>.Create("batch.parameterPoint.value", "VALUE", static x => x.Value)];
                if (!Allows(WorkspaceTimelineSelectionKind.LogicalParameterPoint, location.Segment.Id, lane.Id)
                    || !SummarizePropertyOwner(project, lane.Points.CreateQuerySnapshot(), ids, points, Known(WorkspaceTimelineSelectionKind.LogicalParameterPoint, location.Segment.Id, lane.Id))) continue;
                Replace($"{ids.Count} Logical Parameter Points", "All selected points share one Definition, value domain, and lane.", points);
                return;
            }
        }
        if (selection.Mode == TimelineWorkspaceMode.Segment
            && TimelineWorkspaceViewModel.FindMidiSegment(project, selection.ObjectId) is { } midi)
        {
            PropertySummary<DirectMidiNoteValue>[] notes =
            [PropertySummary<DirectMidiNoteValue>.Create("batch.midiNote.start", "START TICK", static x => x.StartTick),
             PropertySummary<DirectMidiNoteValue>.Create("batch.midiNote.length", "LENGTH TICKS", static x => x.LengthTicks),
             PropertySummary<DirectMidiNoteValue>.Create("batch.midiNote.key", "KEY NUMBER", static x => x.Key),
             PropertySummary<DirectMidiNoteValue>.Create("batch.midiNote.onVelocity", "NOTE ON VELOCITY", static x => x.NoteOnVelocity),
             PropertySummary<DirectMidiNoteValue>.Create("batch.midiNote.offVelocity", "NOTE OFF VELOCITY", static x => x.NoteOffVelocity)];
            if (Allows(WorkspaceTimelineSelectionKind.DirectMidiNote, midi.Segment.Id)
                && SummarizePropertyOwner(project, midi.Segment.Notes.CreateObjectSource(), ids, notes, Known(WorkspaceTimelineSelectionKind.DirectMidiNote, midi.Segment.Id)))
            {
                Replace($"{ids.Count} Direct MIDI Notes", "Common note fields are applied as one Project edit.", notes);
                return;
            }
            var tick = PropertySummary<DirectMidiChannelEventValue>.Create("batch.midiEvent.tick", "TICK", static x => x.Tick);
            var kind = PropertySummary<DirectMidiChannelEventValue>.Create("batch.midiEvent.kind", "EVENT TYPE", static x => x.Kind, editable: false);
            var data1 = PropertySummary<DirectMidiChannelEventValue>.Create("", "", static x => x.Data1);
            var data2 = PropertySummary<DirectMidiChannelEventValue>.Create("", "", static x => x.Data2);
            var bend = PropertySummary<DirectMidiChannelEventValue>.Create("batch.midiEvent.pitchBend", "PITCH BEND (0–16383)", static x => x.Data1 | x.Data2 << 7);
            if (Allows(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, midi.Segment.Id)
                && SummarizePropertyOwner(project, midi.Segment.ChannelEvents.CreateObjectSource(), ids,
                    [tick, kind, data1, data2, bend], Known(WorkspaceTimelineSelectionKind.DirectMidiEventPoint, midi.Segment.Id)))
            {
                List<PropertyField> fields = [tick.Field(), kind.Field()];
                if (kind.Same)
                {
                    switch (kind.First)
                    {
                        case DirectMidiChannelEventKind.NoteOn:
                        case DirectMidiChannelEventKind.NoteOff:
                            fields.Add(Rename(data1.Field(), "batch.midiEvent.key", "KEY NUMBER"));
                            fields.Add(Rename(data2.Field(), "batch.midiEvent.velocity", "VELOCITY")); break;
                        case DirectMidiChannelEventKind.PolyphonicKeyPressure:
                            fields.Add(Rename(data1.Field(), "batch.midiEvent.key", "KEY NUMBER"));
                            fields.Add(Rename(data2.Field(), "batch.midiEvent.pressure", "PRESSURE")); break;
                        case DirectMidiChannelEventKind.ControlChange:
                            fields.Add(Rename(data1.Field(), "batch.midiEvent.controller", "CONTROLLER"));
                            fields.Add(Rename(data2.Field(), "batch.midiEvent.value", "VALUE")); break;
                        case DirectMidiChannelEventKind.ProgramChange:
                            fields.Add(Rename(data1.Field(), "batch.midiEvent.program", "PROGRAM (0–127)",
                                data1.Same ? data1.First.ToString(CultureInfo.InvariantCulture) : "Mixed")); break;
                        case DirectMidiChannelEventKind.ChannelPressure:
                            fields.Add(Rename(data1.Field(), "batch.midiEvent.channelPressure", "PRESSURE")); break;
                        case DirectMidiChannelEventKind.PitchBend: fields.Add(bend.Field()); break;
                        default: throw new ArgumentOutOfRangeException(nameof(kind));
                    }
                }
                properties.Replace($"{ids.Count} Direct MIDI Events", "Common musical fields are applied as one Project edit. Event type remains fixed.", fields);
                return;
            }
        }
        if (selection.IsInstrument && selection.ObjectId is MidoraId instrumentId
            && project.EventInstruments.FirstOrDefault(x => x.Id == instrumentId) is { } instrument)
        {
            foreach (var voice in instrument.SubVoices)
            foreach (var curve in voice.Curves)
            {
                PropertySummary<CurvePointSnapshotValue>[] points =
                [PropertySummary<CurvePointSnapshotValue>.Create("batch.valueCurvePoint.tick", "TICK", static x => x.Tick),
                 PropertySummary<CurvePointSnapshotValue>.Create("batch.valueCurvePoint.value", "VALUE", static x => x.Value),
                 PropertySummary<CurvePointSnapshotValue>.Create("batch.valueCurvePoint.interpolation", "INTERPOLATION", static x => x.Interpolation)];
                if (!SummarizePropertyOwner(project, curve.Points.CreateQuerySnapshot(), ids, points)) continue;
                Replace($"{ids.Count} Value Curve Points", "All selected points share one SubVoice, target, value domain, and curve.", points);
                return;
            }
        }
        if (selection.Source is not null)
        {
            // A source hint is never authority: stale/incorrect hints must not
            // hide Properties for identities that still have a valid owner.
            RebuildMultiSelection(properties, project, selection with { Source = null });
            return;
        }
        properties.Replace($"{ids.Count} objects selected",
            "The selected objects do not expose a field with identical semantics and a safe atomic batch edit.",
            [new("selection.unavailable", "COMMON EDITABLE FIELDS", "Unavailable", false, PropertyFieldValueState.Unavailable)]);

        void Replace<T>(string title, string description, PropertySummary<T>[] fields) =>
            properties.Replace(title, description, fields.Select(static x => x.Field()).ToArray());

        bool Known(WorkspaceTimelineSelectionKind kind, MidoraId owner, MidoraId? secondary = null) =>
            selection.Source is { } hint && hint.Kind == kind && hint.OwnerId == owner
                && (secondary is null || hint.SecondaryOwnerId == secondary);
        bool Allows(WorkspaceTimelineSelectionKind kind, MidoraId owner, MidoraId? secondary = null) =>
            selection.Source is null || Known(kind, owner, secondary);
    }

    private static PropertyField Rename(PropertyField source, string key, string label, string? value = null) =>
        new(key, label, value ?? source.Value, source.IsEditable, source.ValueState);
}
