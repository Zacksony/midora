using System.Diagnostics;
using Midora.Domain;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

[Collection(Stage5BoundedEditScalabilityCollection.CollectionName)]
public sealed class TimelineGenerationPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasureOptInBoundedGeneration()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MIDORA_GENERATOR_BENCHMARK_COUNT"), out int count)
            || count <= 0) return;
        string[] kinds = (Environment.GetEnvironmentVariable("MIDORA_GENERATOR_BENCHMARK_KINDS") ?? "direct,logical,template").Split(',');
        foreach (string kind in kinds) Measure(kind, count);
    }

    private void Measure(string kind, int count)
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, ".tmp", "generator-perf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var project = new MidoraProject(480);
        try
        {
            var instrument = EventInstrumentLibrary.Create(project, "Generator");
            LogicalTrack logicalTrack = new(project) { Name = "Logical" };
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, logicalTrack, instrument.Id);
            Segment logical = new(project) { LengthTicks = count * 2L }; logicalTrack.Segments.Add(logical);
            MidiChannelRoot root = new(project) { Name = "Root" }; project.MidiChannelRoots.Add(root);
            PureMidiTrack midiTrack = new(project) { Name = "MIDI", MidiChannelRootId = root.Id }; project.PureMidiTracks.Add(midiTrack);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, midiTrack.Id));
            MidiSegment midi = new(project) { LengthTicks = count * 2L }; midiTrack.Segments.Add(midi);
            LogicalParameterLane? parameterLane = null;
            if (kind == "logical-event")
            {
                LogicalParameterDefinition parameter = new(project)
                { Name = "Generated Value", Type = LogicalParameterType.Integer, Minimum = 0, Maximum = 127, DefaultValue = 0 };
                instrument.LogicalParameters.Add(parameter);
                parameterLane = new(project) { ParameterId = parameter.Id };
                logical.ParameterLanes.Add(parameterLane);
            }
            var options = new NoteGenerationOptions
            { MaximumCandidates = count, TickExpression = "=i*2", KeyExpression = "=i%128", VelocityExpression = "=64+i%64" };
            var eventOptions = new EventGenerationOptions
            { MaximumCandidates = count, TickExpression = "=i*2", ValueExpression = "=i%128" };
            IProjectEditCommand command = kind switch
            {
                "direct" => ProjectDomainEditCommands.GenerateDirectMidiNotes(midi.Id, options),
                "logical" => ProjectDomainEditCommands.GenerateLogicalNotes(logical.Id, options),
                "template" => ProjectDomainEditCommands.GenerateTemplateNotes(instrument.Id, instrument.SubVoices[0].Id, options),
                "direct-event" => ProjectDomainEditCommands.GenerateDirectMidiEventPoints(
                    midi.Id, DirectMidiChannelEventKind.ControlChange, 11, eventOptions),
                "logical-event" => ProjectDomainEditCommands.GenerateLogicalParameterPoints(
                    logical.Id, parameterLane!.Id, eventOptions),
                "template-event" => ProjectDomainEditCommands.GenerateTemplateEventPoints(
                    instrument.Id, instrument.SubVoices[0].Id, MidiValueTarget.ControlChange(11), eventOptions),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            var resources = new BoundedEditResources(temporaryRoot: scratch);
            using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
            // Use the desktop's asynchronous compilation publication mode. The
            // debounce isolates edit/history costs from a concurrent compile;
            // compiler throughput is a separate benchmark, not this probe.
            using var compilation = new ProjectCompilationSession(project,
                executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(1));
            using var document = new ProjectDocumentSession(compilation);
            var stopwatch = Stopwatch.StartNew();
            long allocated = GC.GetTotalAllocatedBytes();
            using var prepared = document.PrepareEdit(command);
            double prepare = stopwatch.Elapsed.TotalSeconds;
            Assert.Equal(0, SourceCount());
            stopwatch.Restart(); document.ExecutePrepared(prepared);
            double publish = stopwatch.Elapsed.TotalMilliseconds;
            var selection = Assert.IsType<PreparedTimelineSelection>(prepared.PreparedSelection);
            Assert.Equal(count, selection.ResultSelectionIds.Count);
            Assert.Equal(count, SourceCount());
            stopwatch.Restart(); document.Undo();
            double undo = stopwatch.Elapsed.TotalMilliseconds;
            Assert.Equal(0, SourceCount());
            stopwatch.Restart(); document.Redo();
            double redo = stopwatch.Elapsed.TotalMilliseconds;
            Assert.Equal(count, SourceCount());
            using var process = Process.GetCurrentProcess();
            output.WriteLine($"GEN {kind} {count:N0}: prepare={prepare:F3}s; publish={publish:F3}ms; undo={undo:F3}ms; redo={redo:F3}ms; working={resources.PeakWorkingBytes / 1048576.0:F2}MiB; resident={resources.PeakResidentBytes / 1048576.0:F2}MiB; spill={resources.PeakSpillBytes / 1048576.0:F2}MiB; allocated={(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0:F2}MiB; managed={GC.GetTotalMemory(false) / 1048576.0:F2}MiB; ws={process.WorkingSet64 / 1048576.0:F2}MiB");
            Assert.True(resources.PeakWorkingBytes <= resources.Budget.MaximumWorkingBytes);
            Assert.True(resources.PeakResidentBytes <= resources.Budget.MaximumResidentBytes);
            Assert.True(resources.PeakSpillBytes <= resources.Budget.MaximumSpillBytes);

            int SourceCount() => kind switch
            {
                "direct" => project.PureMidiTracks[0].Segments[0].Notes.Count,
                "logical" => project.Tracks[0].Segments[0].Notes.Count,
                "template" or "template-event" => project.EventInstruments[0].SubVoices[0].Events.Count,
                "direct-event" => project.PureMidiTracks[0].Segments[0].ChannelEvents.Count,
                "logical-event" => project.Tracks[0].Segments[0].ParameterLanes[0].Points.Count,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
        finally
        {
            project.Dispose();
            Directory.Delete(scratch, true);
        }
    }
}
