using System.Diagnostics;
using System.Reflection;
using Midora.Application;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

/// <summary>Opt-in read-only probe. This file also compiles against the pre-B1 baseline.</summary>
[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class WorkspaceStatePerformanceProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptInRealMidiOpenCloseAndHotViewUpdates()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_B1_SAMPLE");
        if (string.IsNullOrEmpty(path)) return;
        var imported = MidiProjectImportService.ImportFile(path, "B1 state probe");
        var project = imported.Project;
        await using var session = new DesktopSessionController();
        var compilation = new ProjectCompilationSession(project, executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.FromHours(1));
        var document = new ProjectDocumentSession(compilation);
        var persistence = new ProjectPersistenceCoordinator(document, new MidoraProjectPackageV1("1.0.0-dev-test"));
        Type contextType = typeof(DesktopSessionController).GetNestedType("ProjectContext", BindingFlags.NonPublic)!;
        object context = Activator.CreateInstance(contextType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [new ProjectOwner(project), compilation, document, persistence, null, null, "No audio in the B1 probe"], null)!;
        await (Task)typeof(DesktopSessionController).GetMethod("ActivateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [context])!;
        var track = project.PureMidiTracks.Single(x => x.Name == "MIDI Out #23");
        var segment = Assert.Single(track.Segments);
        output.WriteLine($"notes={segment.Notes.Count:N0}");
        List<double> reopen = [], hot = [];
        var watch = Stopwatch.StartNew();
        var workspace = session.OpenSegment(segment.Id);
        output.WriteLine($"initial-open={watch.Elapsed.TotalMilliseconds:F3} ms");
        var originalSnapshot = workspace.Snapshot;
        long musicRevision = compilation.SourceRevision;
        for (int i = 0; i < 1000; i++) { workspace.TickSpan = 3000 + (i % 32); workspace.StartTick = 168816 + i; }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var timings = new long[10_000];
        for (int i = 0; i < timings.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            workspace.TickSpan = 3000 + (i % 32); workspace.StartTick = 168816 + i % 24000;
            workspace.LaneHeight = 6 + (i % 3);
            timings[i] = Stopwatch.GetTimestamp() - start;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Same(originalSnapshot, workspace.Snapshot);
        Assert.Equal(musicRevision, compilation.SourceRevision);
        hot.AddRange(timings.Select(x => x * 1000d / Stopwatch.Frequency));
        for (int i = 0; i < 20; i++)
        {
            CheckMemory();
            session.CloseWorkspace(workspace); watch.Restart(); workspace = session.OpenSegment(segment.Id);
            reopen.Add(watch.Elapsed.TotalMilliseconds);
        }
        Report("hot-pan-zoom", hot); output.WriteLine($"hot-allocated={allocated:N0} bytes (10000 iterations)");
        Report("close-reopen", reopen);
        session.CloseWorkspace(workspace); CheckMemory();
        output.WriteLine($"managed={GC.GetTotalMemory(true) / 1048576d:F2} MiB; private={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d:F2} MiB");

        void Report(string name, List<double> values)
        {
            values.Sort(); output.WriteLine($"{name}: p50={values[values.Count / 2]:F4} ms; p95={values[(int)(values.Count * .95)]:F4} ms; p99={values[Math.Min(values.Count - 1, (int)(values.Count * .99))]:F4} ms");
        }
        static void CheckMemory() => Assert.True(Process.GetCurrentProcess().PrivateMemorySize64 < 7L * 1024 * 1024 * 1024,
            "The B1 probe stopped before the 9 GiB process safety boundary.");
    }
    private sealed class ProjectOwner(MidoraProject project) : IAsyncDisposable
    { public ValueTask DisposeAsync() { project.Dispose(); return ValueTask.CompletedTask; } }
}
