using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class NoteCopyPitchBoundaryTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (int kind in new[] { 0, 1, 2 })
        foreach (int count in new[] { 10, 4_101 })
        foreach (int delta in new[] { -2, 2, -128, 128 })
            yield return [kind, count, delta];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CopyDropsOnlyOutOfRangeTargetsPreservesSourcesAndUndoRedo(int kind, int count, int delta)
    {
        string path = Path.Combine(AppContext.BaseDirectory, ".tmp", "copy-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var project = new MidoraProject(480);
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumResidentBytes: 64 * 1024, maximumWorkingBytes: 8 * 1024 * 1024), path);
            using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
            LogicalTrack logical = new(project) { Name = "Logical" };
            Segment segment = new(project) { LengthTicks = 1_000_000 };
            logical.Segments.Add(segment); project.Tracks.Add(logical);
            MidiChannelRoot root = new(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
            PureMidiTrack midi = new(project) { Name = "MIDI", MidiChannelRootId = root.Id };
            MidiSegment midiSegment = new(project) { LengthTicks = 1_000_000 };
            midi.Segments.Add(midiSegment); project.PureMidiTracks.Add(midi);
            EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 100_000 };
            SubVoice voice = new(project) { Name = "Voice" };
            instrument.SubVoices.Add(voice); project.EventInstruments.Add(instrument);
            int[] keys = [0, 1, 64, 126, 127];
            for (int i = 0; i < count; i++) Add(i * 10L, keys[i % keys.Length]);
            var selected = Read();
            const long tickDelta = 500_000;
            // An incumbent at one of the valid targets must win independently
            // of the key-boundary filter; none of the sources may be deleted.
            var firstSurvivor = selected.FirstOrDefault(v => (long)v.Key + delta is >= 0 and <= 127);
            if (firstSurvivor.Id != default) Add(firstSurvivor.Tick + tickDelta, firstSurvivor.Key + delta);
            var before = Read(); long nextId = project.NextStableId;
            var ids = selected.Select(v => v.Id).ToArray();
            var command = kind switch
            {
                0 => ProjectDomainEditCommands.DuplicateDirectMidiNotes(midiSegment.Id, ids, tickDelta, delta),
                1 => ProjectDomainEditCommands.DuplicateLogicalNotes(segment.Id, ids, segment.Id, tickDelta, delta),
                _ => ProjectDomainEditCommands.DuplicateTemplateNotes(instrument.Id, voice.Id, ids, tickDelta, delta)
            };
            var edit = ExactTimelineCollisionPolicy.Wrap(project, command.Prepare(project));
            try
            {
                Assert.Equal(before, Read()); Assert.Equal(nextId, project.NextStableId);
                var expected = selected.Where(v => (long)v.Key + delta is >= 0 and <= 127)
                    .Select(v => v with { Id = default, Tick = v.Tick + tickDelta, Key = v.Key + delta })
                    .Where(v => !before.Any(b => b.Tick == v.Tick && b.Key == v.Key)).ToArray();
                Assert.Equal(expected.Length != 0, edit.HasChanges);
                edit.Apply(project);
                var after = Read();
                Assert.Equal(before, after.Where(v => v.Id.Value < nextId));
                Assert.Equal(expected, after.Where(v => v.Id.Value >= nextId).Select(v => v with { Id = default }));
                if (expected.Length == 0) Assert.Equal(nextId, project.NextStableId);
                edit.Undo(project); Assert.Equal(before, Read());
                edit.Apply(project); Assert.Equal(after, Read());
                edit.Undo(project); Assert.Equal(before, Read());
                Assert.InRange(resources.PeakResidentBytes, 0, resources.Budget.MaximumResidentBytes);
                Assert.InRange(resources.PeakWorkingBytes, 0, resources.Budget.MaximumWorkingBytes);
            }
            finally { (edit as IDisposable)?.Dispose(); }

            void Add(long tick, int key)
            {
                if (kind == 0) midiSegment.Notes.Add(new DirectMidiNote(project)
                { StartTick = tick, LengthTicks = 7, Key = key, NoteOnVelocity = 91, NoteOffVelocity = 37 });
                else if (kind == 1) segment.Notes.Add(new LogicalNote(project)
                { StartTick = tick, LengthTicks = 7, Note = key, Velocity = 91 });
                else voice.Events.Add(new TemplateEvent(project)
                { Kind = TemplateEventKind.Note, Tick = tick, LengthTicks = 7, Number = key, Value = 91 });
            }
            Value[] Read() => kind switch
            {
                0 => midi.Segments[0].Notes.Select(v => new Value(v.Id, v.StartTick, v.Key, v.LengthTicks, v.NoteOnVelocity, v.NoteOffVelocity)).ToArray(),
                1 => logical.Segments[0].Notes.Select(v => new Value(v.Id, v.StartTick, v.Note, v.LengthTicks, v.Velocity, 0)).ToArray(),
                _ => instrument.SubVoices[0].Events.Select(v => new Value(v.Id, v.Tick, v.Number, v.LengthTicks, v.Value, 0)).ToArray()
            };
        }
        finally { Directory.Delete(path, true); }
    }

    private readonly record struct Value(MidoraId Id, long Tick, int Key, long Gate, int Velocity, int OffVelocity);
}
