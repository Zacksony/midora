using System.Diagnostics;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

// Explicit read-only sample opt-in; the normal test suite never reads user MIDI files.
public sealed class InstrumentChangeRealMidiTests(ITestOutputHelper output)
{
    [Fact]
    public void MeasureActualPagedMidiInstrumentInsertion()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_A2A_MIDI");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var memoryGuard = new Timer(_ =>
        {
            using var process = Process.GetCurrentProcess();
            if (process.PrivateMemorySize64 >= 8L * 1024 * 1024 * 1024) cancel.Cancel();
        }, null, 0, 500);
        var clock = Stopwatch.StartNew();
        using var project = MidiProjectImportService.ImportFile(path, "A2a probe", cancellationToken: cancel.Token).Project;
        output.WriteLine($"Import: {clock.Elapsed.TotalSeconds:F3} s; notes={project.PureMidiTracks.Sum(t => (long)t.Segments.Sum(s => s.Notes.Count))}");
        clock.Restart();
        var summaries = project.PureMidiTracks.SelectMany(t => t.Segments).Select(s => s.ChannelEvents.CreateQuerySnapshot()).ToArray();
        long channelCount = 0;
        foreach (var summary in summaries) channelCount += summary.GetTargetCounts(cancel.Token).Values.Sum();
        output.WriteLine($"All channel target summaries cold: {clock.Elapsed.TotalMilliseconds:F2} ms; events={channelCount:N0}");
        clock.Restart();
        for (int i = 0; i < 100; i++)
            foreach (var summary in summaries) Assert.Equal(summary.Count, summary.GetTargetCounts(cancel.Token).Values.Sum());
        output.WriteLine($"All summaries warm x100: {clock.Elapsed.TotalMilliseconds:F2} ms");
        var track = Assert.Single(project.PureMidiTracks, t => t.Name == "MIDI Out #23");
        var id = Assert.Single(track.Segments).Id;
        Assert.Empty(track.Segments[0].InstrumentChanges.Values);
        for (int i = 0; i < 5; i++)
        {
            long allocated = GC.GetTotalAllocatedBytes(); clock.Restart();
            using var scope = BulkEditPreparationContext.Enter(cancel.Token, project: project);
            var edit = ProjectDomainEditCommands.SetMidiInstrumentChange(id, 168960, new(0, 0, (byte)i)).Prepare(project);
            try
            {
                double prepareMs = clock.Elapsed.TotalMilliseconds; clock.Restart(); edit.Apply(project);
                double applyMs = clock.Elapsed.TotalMilliseconds;
                var owner = project.PureMidiTracks.Single(t => t.Id == track.Id).Segments.Single(s => s.Id == id);
                var group = Assert.Single(owner.InstrumentChanges.Values);
                Assert.True(InstrumentChangeResolver.TryRead(owner, group, out _));
                clock.Restart(); edit.Undo(project); double undoMs = clock.Elapsed.TotalMilliseconds;
                output.WriteLine($"#{i}: prepare={prepareMs:F2} ms; apply={applyMs:F2} ms; undo={undoMs:F2} ms; allocation={(GC.GetTotalAllocatedBytes()-allocated)/1048576d:F2} MiB; heap={GC.GetTotalMemory(false)/1048576d:F2} MiB");
            }
            finally { (edit as IDisposable)?.Dispose(); }
        }
        using var finalProcess = Process.GetCurrentProcess();
        output.WriteLine($"Peak Working Set: {finalProcess.PeakWorkingSet64/1048576d:F2} MiB");
    }
}
