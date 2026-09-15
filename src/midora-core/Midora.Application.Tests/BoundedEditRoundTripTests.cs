using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Application.Tests;

public sealed class BoundedEditRoundTripTests
{
    [Theory]
    [InlineData("Direct")]
    [InlineData("Logical")]
    [InlineData("SubVoice")]
    public async Task PublishedSpillRootsCompileIncrementallyAndRoundTripWithoutSemanticChanges(string kind)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "bounded-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using MidoraProject project = new(480);
        try
        {
            IProjectEditCommand move;
            if (kind == "Direct")
            {
                var root = new MidiChannelRoot(project) { Name = "Root" };
                var track = new PureMidiTrack(project) { Name = "Direct", MidiChannelRootId = root.Id };
                var segment = new MidiSegment(project) { LengthTicks = 50_000 };
                project.MidiChannelRoots.Add(root); project.PureMidiTracks.Add(track); track.Segments.Add(segment);
                project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
                segment.Notes.AddRange(Enumerable.Range(0, 4_100).Select(i => new DirectMidiNote(project)
                { StartTick = i * 4L, LengthTicks = 2, Key = 60, NoteOnVelocity = 80, NoteOffVelocity = 31,
                    NoteOnOrder = i * 2L, NoteOffOrder = i * 2L + 1 }));
                move = ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, segment.Notes.Select(n => n.Id).ToArray(), 1, 1);
            }
            else
            {
                var instrument = EventInstrumentLibrary.Create(project, "Instrument");
                instrument.TemplateLengthTicks = 50_000;
                var usage = new EventInstrumentUsage(project) { EventInstrumentId = instrument.Id };
                var track = new LogicalTrack(project) { Name = "Logical", EventInstrumentUsageId = usage.Id };
                var segment = new Segment(project) { LengthTicks = 50_000 };
                project.EventInstrumentUsages.Add(usage); project.Tracks.Add(track); track.Segments.Add(segment);
                project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, track.Id));
                var voice = instrument.SubVoices[0];
                if (kind == "Logical")
                {
                    voice.Events.Add(TemplateEvent.Note(project, 0, 2, 60, 80));
                    segment.Notes.AddRange(Enumerable.Range(0, 4_100).Select(i => new LogicalNote(project)
                    { StartTick = i * 4L, LengthTicks = 2, Note = 60, Velocity = 80 }));
                    move = ProjectDomainEditCommands.MoveLogicalNotes(segment.Id, segment.Notes.Select(n => n.Id).ToArray(), 1, 1);
                }
                else
                {
                    segment.Notes.Add(new LogicalNote(project) { StartTick = 0, LengthTicks = 40_000, Note = 60, Velocity = 80 });
                    voice.Events.AddRange(Enumerable.Range(0, 4_100).Select(i => TemplateEvent.Note(project, i * 4L, 2, 60, 80)));
                    move = ProjectDomainEditCommands.MoveTemplateNotes(instrument.Id, voice.Id, voice.Events.Select(n => n.Id).ToArray(), 1, 1);
                }
            }
            var compiler = new MidoraCompiler();
            var before = compiler.CompileFull(project);
            Assert.True(before.IsConsumable, string.Join("\n", before.Diagnostics));
            var resources = new BoundedEditResources(temporaryRoot: directory);
            using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
            using var lease = resources.BeginResourceLease();
            var prepared = new SequentialProjectEditCommand("Move", [_ => move]).Prepare(project);
            using var publication = lease.Complete();
            prepared.Apply(project); publication.MarkPublished();
            Assert.Equal(0, resources.ResidentBytes);
            var incremental = compiler.CompileIncremental(project, prepared.Changes);
            var full = new MidoraCompiler().CompileFull(project);
            Assert.True(full.IsConsumable, string.Join("\n", full.Diagnostics));
            Assert.Equal(full.Fingerprint, incremental.Fingerprint);
            Assert.Equal(Events(full), Events(incremental));
            Assert.Equal(full.TotalEventCount, incremental.TotalEventCount);
            Assert.Equal(full.TotalNoteOnEventCount, incremental.TotalNoteOnEventCount);
            var packages = new MidoraProjectPackageV1("1.0.0-test");
            string path = Path.Combine(directory, "edited.midora");
            await packages.SaveCopyAsync(project, path);
            var opened = await packages.OpenAsync(path);
            using var restored = opened.Project;
            Assert.Empty(opened.Diagnostics);
            var restoredResult = new MidoraCompiler().CompileFull(restored);
            // Pure MIDI uses the SRS 12.25.3 source-aware cache fingerprint:
            // saving flattens the editable source+delta into a new content pack.
            // Compare every canonical event, not that representation's cache
            // identity. Full/incremental identity on the SAME revision above
            // remains strict for all three source kinds.
            if (kind != "Direct") Assert.Equal(full.Fingerprint, restoredResult.Fingerprint);
            Assert.Equal(Events(full), Events(restoredResult));
            Assert.Equal(full.TotalEventCount, restoredResult.TotalEventCount);
            Assert.Equal(full.TotalNoteOnEventCount, restoredResult.TotalNoteOnEventCount);
            prepared.Undo(project);
            Assert.Equal(before.Fingerprint, new MidoraCompiler().CompileFull(project).Fingerprint);
            prepared.Apply(project);
            Assert.Equal(full.Fingerprint, new MidoraCompiler().CompileFull(project).Fingerprint);
            (prepared as IDisposable)?.Dispose();
        }
        finally { project.Dispose(); Directory.Delete(directory, recursive: true); }
    }

    private static CanonicalMidiEvent[] Events(CanonicalCompiledResult result) =>
        result.QueryEventPages(result.StartTick, result.EndTick).SelectMany(static p => p.Items).ToArray();
}
