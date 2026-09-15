using System.Diagnostics;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

// Explicit local probe: the ordinary suite never opens external user files.
// Set MIDORA_REAL_SELECTION_READ_MIDI to the authorized 9KX2 sample and run
// this test's fully qualified filter to collect real, not estimated timings.
[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class RealMidiSelectionReadPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasureFourHundredThousandActualMidiNoteProperties()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_REAL_SELECTION_READ_MIDI");
        if (string.IsNullOrWhiteSpace(path)) return;
        Stopwatch clock = Stopwatch.StartNew();
        var imported = MidiProjectImportService.ImportFile(path, "Selection read probe");
        using MidoraProject project = imported.Project;
        output.WriteLine($"Import: {clock.Elapsed.TotalSeconds:F3}s");
        PureMidiTrack track = Assert.Single(project.PureMidiTracks, t => t.Name == "MIDI Out #23");
        MidiSegment segment = Assert.Single(track.Segments);
        var source = segment.Notes.CreateObjectSource();
        var ids = CompressedMidoraIdSet.Create(source.QueryTickRange(new(168816, 193536, 0, 127))
            .Take(400_000).Select(static note => note.Id));
        Assert.Equal(400_000, ids.Count);
        var selection = new ObjectPropertiesSelectionContext(TimelineWorkspaceMode.Segment, false, segment.Id, ids,
            new(WorkspaceTimelineSelectionKind.DirectMidiNote, segment.Id));
        output.WriteLine($"Owner notes: {source.Count:N0}; selected: {ids.Count:N0}; source revision: {source.SourceRevision}");
        Run("Properties first", ReadProperties);
        Run("Properties repeat", ReadProperties);
        Run("Selection metrics", () =>
        {
            var metrics = MainWindow.ReadSelectionInputMetrics(project, source, ids,
                static value => (value.StartTick, checked(value.StartTick + value.LengthTicks), value.Key, (double)value.NoteOnVelocity), default);
            Assert.Same(ids, metrics.Ids);
            Assert.True(metrics.MaximumTick > metrics.MinimumTick);
        });

        void ReadProperties()
        {
            var properties = ObjectPropertiesProjection.ReadMultiSelection(project, selection, default, null);
            Assert.Equal("400000 Direct MIDI Notes", properties.Title);
            Assert.Equal(5, properties.Fields.Count);
            Assert.Single(properties.Fields, field => field.Key == "batch.midiNote.offVelocity");
        }
        void Run(string label, Action read)
        {
            long allocated = GC.GetTotalAllocatedBytes();
            clock.Restart();
            read();
            output.WriteLine($"{label}: {clock.Elapsed.TotalSeconds:F3}s; allocated={(GC.GetTotalAllocatedBytes() - allocated) / 1048576d:F2} MiB; managed={GC.GetTotalMemory(false) / 1048576d:F2} MiB");
        }
    }
}
