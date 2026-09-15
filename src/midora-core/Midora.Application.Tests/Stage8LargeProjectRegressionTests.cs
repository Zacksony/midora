using System.Diagnostics;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

/// <summary>Explicit sample gate: never opens external files unless the operator supplies a path.</summary>
[Collection(Stage5BoundedEditScalabilityCollection.CollectionName)]
public sealed class Stage8LargeProjectRegressionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SampleSelectionEditCancelHistoryAndPresentationSaveRemainBounded()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_STAGE8_MIDI");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var temporary = new Stage8Directory();
        var timer = Stopwatch.StartNew();
        var imported = MidiProjectImportService.ImportFile(path, Path.GetFileNameWithoutExtension(path));
        using var project = imported.Project;
        output.WriteLine($"Import: {imported.Metrics?.ImportedNoteCount:N0} notes, {timer.Elapsed.TotalSeconds:F3}s");
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(10));
        using var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Unsaved);
        var packages = new MidoraProjectPackageV1("1.0.0-dev");
        var persistence = new ProjectPersistenceCoordinator(document, packages);
        var track = project.PureMidiTracks.OrderByDescending(t => t.Segments.Sum(s => s.Notes.Count)).First();
        var segment = track.Segments.OrderByDescending(s => s.Notes.Count).First();
        int originalCount = segment.Notes.Count;
        timer.Restart();
        var selected = segment.Notes.QueryValues(segment.ContentOffsetTick, segment.ContentEndTick).Take(60_000).ToArray();
        Assert.Equal(60_000, selected.Length);
        var ids = CompactMidoraIdList.Freeze(selected.Select(n => n.Id).ToArray());
        output.WriteLine($"Read selection: {timer.Elapsed.TotalMilliseconds:F1}ms, {ids.Count:N0} / {originalCount:N0}");
        Run("resize", ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(segment.Id, ids, 0, 1));
        Run("move", ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 1, 0));

        using var cancellation = new CancellationTokenSource();
        long state = document.CurrentStateId;
        timer.Restart();
        Assert.Throws<OperationCanceledException>(() => document.PrepareEdit(
            ProjectDomainEditCommands.MoveDirectMidiNotes(segment.Id, ids, 2, 0), cancellation.Token,
            new CancelAfterWork(cancellation)));
        Assert.Equal(state, document.CurrentStateId);
        Assert.Equal(originalCount, segment.Notes.Count);
        output.WriteLine($"Cancel active preparation: {timer.Elapsed.TotalMilliseconds:F1}ms; history/source unchanged");

        var source = project.PureMidiTracks.First(t => t.Id != track.Id);
        persistence.Presentation.Replace(new(ProjectPresentationAllTracksModeV3.Compiled,
            [new(track.Id, true, .3, [source.Id])], []));
        long noteCount = project.PureMidiTracks.Sum(t => t.Segments.Sum(s => (long)s.Notes.Count));
        string saved = temporary.PathFor("stage8.midora"), copy = temporary.PathFor("stage8-copy.midora");
        timer.Restart(); await persistence.SaveProjectAsync(saved);
        output.WriteLine($"Save Format 3: {timer.Elapsed.TotalSeconds:F3}s, {new FileInfo(saved).Length / 1048576d:F1}MiB");
        Assert.False(document.IsModified);
        timer.Restart(); await persistence.SaveCopyAsync(copy);
        output.WriteLine($"Save Copy: {timer.Elapsed.TotalSeconds:F3}s");
        timer.Restart();
        await using var opened = await packages.OpenAsync(copy);
        output.WriteLine($"Reopen: {timer.Elapsed.TotalSeconds:F3}s");
        Assert.Equal(noteCount, opened.Project.PureMidiTracks.Sum(t => t.Segments.Sum(s => (long)s.Notes.Count)));
        Assert.Equal(ProjectPresentationAllTracksModeV3.Compiled, opened.Presentation.AllTracksMode);
        var restored = Assert.Single(opened.Presentation.TrackOnionPresets);
        Assert.Equal(track.Id, restored.TargetTrackId); Assert.Equal(source.Id, Assert.Single(restored.SourceTrackIds));
        output.WriteLine($"Process: managed={GC.GetTotalMemory(false) / 1048576d:F1}MiB; peak WS={Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d:F1}MiB");

        void Run(string label, IProjectEditCommand command)
        {
            var resources = new BoundedEditResources();
            using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
            timer.Restart(); using var prepared = document.PrepareEdit(command);
            double prepare = timer.Elapsed.TotalSeconds;
            timer.Restart(); document.ExecutePrepared(prepared); double publish = timer.Elapsed.TotalMilliseconds;
            timer.Restart(); document.Undo(); double undo = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(originalCount, segment.Notes.Count);
            Assert.True(segment.Notes.TryGetById(selected[0].Id, out var first));
            Assert.Equal(selected[0].StartTick, first!.StartTick); Assert.Equal(selected[0].LengthTicks, first.LengthTicks);
            timer.Restart(); document.Redo(); double redo = timer.Elapsed.TotalMilliseconds;
            document.Undo();
            Assert.Equal(originalCount, segment.Notes.Count);
            Assert.InRange(resources.PeakResidentBytes, 0, resources.Budget.MaximumResidentBytes);
            Assert.InRange(resources.PeakWorkingBytes, 0, resources.Budget.MaximumWorkingBytes);
            output.WriteLine($"{label}: prepare={prepare:F3}s, publish={publish:F1}ms, undo={undo:F1}ms, redo={redo:F1}ms; resident={resources.PeakResidentBytes / 1048576d:F1}MiB, working={resources.PeakWorkingBytes / 1048576d:F1}MiB, spill={resources.PeakSpillBytes / 1048576d:F1}MiB");
        }
    }

    private sealed class CancelAfterWork(CancellationTokenSource cancellation) : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value)
        { if (value.Completed > 0) cancellation.Cancel(); }
    }

    private sealed class Stage8Directory : IDisposable
    {
        private readonly string _path = Path.Combine(AppContext.BaseDirectory, ".tmp", "stage8-roundtrip-" + Guid.NewGuid().ToString("N"));
        public Stage8Directory() => Directory.CreateDirectory(_path);
        public string PathFor(string name) => Path.Combine(_path, name);
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
