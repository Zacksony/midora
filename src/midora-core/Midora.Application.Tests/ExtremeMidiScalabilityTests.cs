using System.Diagnostics;
using System.Security.Cryptography;
using Midora.Audio;
using Midora.Compiler;
using Midora.Playback;
using Midora.Domain;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

public sealed class ExtremeMidiScalabilityTests(ITestOutputHelper output)
{
    [Fact]
    public void OptInSampleLargeDuplicateUndoUsesBatchOverlayCompaction()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_SCALE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_SCALE_MIDI_PATH to run the opt-in large-MIDI Undo gate.");
            return;
        }

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            int requestedCount = int.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_UNDO_COUNT"),
                    out int configuredCount)
                ? Math.Max(1, configuredCount)
                : 100_000;
            DirectMidiNoteValue[] selected = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .Take(requestedCount)
                .ToArray();
            Assert.NotEmpty(selected);
            int originalCount = segment.Notes.Count;
            long destinationStart = checked(segment.Notes.CreateQuerySnapshot().MaximumEndTick + 1_024);
            long tickDelta = checked(destinationStart - selected.Min(static value => value.StartTick));

            Stopwatch prepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit source = ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                    segment.Id,
                    selected.Select(static value => value.Id).ToArray(),
                    tickDelta,
                    keyDelta: 0)
                .Prepare(imported.Project);
            IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(imported.Project, source);
            prepareTimer.Stop();

            Stopwatch firstApply = Stopwatch.StartNew();
            edit.Apply(imported.Project);
            firstApply.Stop();
            int createdCount = segment.Notes.Count - originalCount;
            Assert.InRange(createdCount, 1, selected.Length);

            Stopwatch firstUndo = Stopwatch.StartNew();
            edit.Undo(imported.Project);
            firstUndo.Stop();
            Assert.Equal(originalCount, segment.Notes.Count);

            Stopwatch warmApply = Stopwatch.StartNew();
            edit.Apply(imported.Project);
            warmApply.Stop();
            Assert.Equal(checked(originalCount + createdCount), segment.Notes.Count);

            Stopwatch warmUndo = Stopwatch.StartNew();
            edit.Undo(imported.Project);
            warmUndo.Stop();
            Assert.Equal(originalCount, segment.Notes.Count);

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            output.WriteLine(
                $"notes={originalCount}; requested={selected.Length}; created={createdCount}; "
                + $"prepare={prepareTimer.Elapsed}; firstApply={firstApply.Elapsed}; "
                + $"firstUndo={firstUndo.Elapsed}; warmApply={warmApply.Elapsed}; "
                + $"warmUndo={warmUndo.Elapsed}; managedMiB={GC.GetTotalMemory(false) / 1048576d:F1}");
        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    [Fact]
    public void OptInSamplePagedSelectionEditUsesBoundedTargetedWork()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_SCALE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_SCALE_MIDI_PATH to run the opt-in large-MIDI edit gate.");
            return;
        }

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            MidiSegment segment = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            int requestedEditCount = int.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_EDIT_COUNT"),
                    out int configuredEditCount)
                ? Math.Max(1, configuredEditCount)
                : 4_096;
            DirectMidiNoteValue[] selected = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .Take(requestedEditCount)
                .ToArray();
            Assert.NotEmpty(selected);
            long originalFirstLength = selected[0].LengthTicks;
            IProjectEditCommand command = ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                segment.Id,
                selected.Select(static value => value.Id).ToArray(),
                startDelta: 0,
                endDelta: 1);
            Stopwatch prepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit sourceEdit = command.Prepare(imported.Project);
            prepareTimer.Stop();
            Stopwatch collisionBaselineTimer = Stopwatch.StartNew();
            IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(
                imported.Project,
                sourceEdit);
            collisionBaselineTimer.Stop();

            Stopwatch timer = Stopwatch.StartNew();
            edit.Apply(imported.Project);
            timer.Stop();
            Assert.True(segment.Notes.TryGetById(selected[0].Id, out DirectMidiNote? edited));
            Assert.NotNull(edited);
            Assert.Equal(originalFirstLength + 1, edited!.LengthTicks);

            Stopwatch undoTimer = Stopwatch.StartNew();
            edit.Undo(imported.Project);
            undoTimer.Stop();
            Assert.Equal(originalFirstLength, edited.LengthTicks);

            Stopwatch movePrepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit moveSource = ProjectDomainEditCommands.MoveDirectMidiNotes(
                segment.Id,
                selected.Select(static value => value.Id).ToArray(),
                tickDelta: 1,
                keyDelta: 0).Prepare(imported.Project);
            movePrepareTimer.Stop();
            Stopwatch moveBaselineTimer = Stopwatch.StartNew();
            IPreparedProjectEdit move = ExactTimelineCollisionPolicy.Wrap(imported.Project, moveSource);
            moveBaselineTimer.Stop();
            Stopwatch moveTimer = Stopwatch.StartNew();
            move.Apply(imported.Project);
            moveTimer.Stop();
            Stopwatch moveUndoTimer = Stopwatch.StartNew();
            move.Undo(imported.Project);
            moveUndoTimer.Stop();
            Assert.True(segment.Notes.TryGetById(selected[0].Id, out _));
            long createTick = checked(segment.ContentEndTick + 1);
            Stopwatch createPrepareTimer = Stopwatch.StartNew();
            IPreparedProjectEdit createSource = ProjectDomainEditCommands.CreateDirectMidiNote(
                segment.Id,
                createTick,
                lengthTicks: 1,
                key: 0,
                noteOnVelocity: 100).Prepare(imported.Project);
            createPrepareTimer.Stop();
            Stopwatch createBaselineTimer = Stopwatch.StartNew();
            IPreparedProjectEdit create = ExactTimelineCollisionPolicy.Wrap(imported.Project, createSource);
            createBaselineTimer.Stop();
            Stopwatch createApplyTimer = Stopwatch.StartNew();
            create.Apply(imported.Project);
            createApplyTimer.Stop();
            output.WriteLine(
                $"notes={segment.Notes.Count}; selected={selected.Length}; "
                + $"prepare={prepareTimer.Elapsed}; collisionBaseline={collisionBaselineTimer.Elapsed}; "
                + $"edit={timer.Elapsed}; undo={undoTimer.Elapsed}; "
                + $"movePrepare={movePrepareTimer.Elapsed}; moveBaseline={moveBaselineTimer.Elapsed}; "
                + $"move={moveTimer.Elapsed}; moveUndo={moveUndoTimer.Elapsed}; "
                + $"createPrepare={createPrepareTimer.Elapsed}; createBaseline={createBaselineTimer.Elapsed}; "
                + $"createApply={createApplyTimer.Elapsed}");
        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    [Fact]
    public void OptInSampleEditedCompilationRevisionRemainsPaged()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_SCALE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine(
                "Set MIDORA_SCALE_MIDI_PATH to run the opt-in edited-compilation gate.");
            return;
        }

        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            Path.GetFullPath(path),
            Path.GetFileNameWithoutExtension(path));
        try
        {
            PureMidiTrack track = imported.Project.PureMidiTracks
                .OrderByDescending(static value => value.Segments.Sum(segment => segment.Notes.Count))
                .First(static value => value.Segments.Any(segment => segment.Notes.Count != 0));
            MidiSegment segment = track.Segments
                .OrderByDescending(static value => value.Notes.Count)
                .First(static value => value.Notes.Count != 0);
            int requestedCount = int.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_EDIT_COUNT"),
                    out int configuredCount)
                ? Math.Max(1, configuredCount)
                : 60_000;
            MidoraId[] ids = segment.Notes.QueryValues(
                    segment.ContentOffsetTick,
                    segment.ContentEndTick)
                .Take(requestedCount)
                .Select(static value => value.Id)
                .ToArray();
            Assert.NotEmpty(ids);

            using MidoraProject mirror = ProjectCompilationSnapshot.Create(imported.Project);
            IPreparedProjectEdit sourceEdit = ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                segment.Id,
                ids,
                startDelta: 0,
                endDelta: 1).Prepare(imported.Project);
            IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(
                imported.Project,
                sourceEdit);
            edit.Apply(imported.Project);

            ProjectChangeSet changes = new();
            changes.PureMidiTrackIds.Add(track.Id);
            Stopwatch captureTimer = Stopwatch.StartNew();
            ProjectCompilationSnapshot.RevisionCapture capture =
                ProjectCompilationSnapshot.CaptureRevision(mirror, imported.Project, changes);
            captureTimer.Stop();
            Stopwatch materializeTimer = Stopwatch.StartNew();
            MidoraProject materialized = ProjectCompilationSnapshot.MaterializeRevision(capture);
            materializeTimer.Stop();
            Assert.Same(mirror, materialized);

            Stopwatch compileTimer = Stopwatch.StartNew();
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(materialized);
            compileTimer.Stop();
            Assert.True(compiled.EndTick > compiled.StartTick);
            output.WriteLine(
                $"editedNotes={ids.Length}; capture={captureTimer.Elapsed}; "
                + $"materialize={materializeTimer.Elapsed}; compile={compileTimer.Elapsed}; "
                + $"compiledRange=[{compiled.StartTick}, {compiled.EndTick})");
            Assert.True(
                captureTimer.Elapsed < TimeSpan.FromSeconds(3),
                $"Edited compilation capture took {captureTimer.Elapsed}.");
            Assert.True(
                materializeTimer.Elapsed < TimeSpan.FromSeconds(5),
                $"Edited compilation mirror materialization took {materializeTimer.Elapsed}.");
            Assert.True(
                compileTimer.Elapsed < TimeSpan.FromSeconds(10),
                $"Edited full compile took {compileTimer.Elapsed}.");
        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    [Fact]
    public void OptInSampleImportAndCompileRemainPaged()
    {
        string? path = Environment.GetEnvironmentVariable("MIDORA_SCALE_MIDI_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            output.WriteLine("Set MIDORA_SCALE_MIDI_PATH to run the opt-in large-MIDI gate.");
            return;
        }

        path = Path.GetFullPath(path);
        Assert.True(File.Exists(path), $"Scale sample does not exist: {path}");
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        Process process = Process.GetCurrentProcess();
        using ManagedHeapSampler managedHeapSampler = new();
        Stopwatch importTimer = Stopwatch.StartNew();
        MidiProjectImportResult imported = MidiProjectImportService.ImportFile(
            path,
            Path.GetFileNameWithoutExtension(path));
        importTimer.Stop();
        managedHeapSampler.Stop();
        try
        {
            PureMidiContentPack[] contentPacks = imported.Project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .Select(static segment => segment.TryGetPristineContentPack())
                .OfType<PureMidiContentPack>()
                .DistinctBy(static pack => pack.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static pack => pack.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.NotEmpty(contentPacks);
            Stopwatch compileTimer = Stopwatch.StartNew();
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(imported.Project);
            compileTimer.Stop();

            Stopwatch planTimer = Stopwatch.StartNew();
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);
            planTimer.Stop();
            Stopwatch windowTimer = Stopwatch.StartNew();
            long startupEventCount = plan.EventPageProvider?.Query(
                    0,
                    Math.Min(plan.TotalFrameCount, 96_000))
                .LongCount() ?? plan.Ports.ToArray().Sum(port => (long)port.Events.Length);
            windowTimer.Stop();

            if (string.Equals(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_QUERY_MIDPOINT"),
                    "1",
                    StringComparison.Ordinal))
            {
                long midpointTick = compiled.StartTick
                    + ((compiled.EndTick - compiled.StartTick) / 2);
                long midpointEndTick = Math.Min(
                    compiled.EndTick,
                    checked(midpointTick + imported.Project.TicksPerQuarterNote * 2L));
                if (midpointEndTick > midpointTick)
                {
                    Stopwatch midpointTimer = Stopwatch.StartNew();
                    long midpointEventCount = compiled.QueryMidiRenderEventPages(
                            midpointTick,
                            midpointEndTick,
                            includeStateAtStart: true)
                        .Sum(page => (long)page.Items.Count);
                    midpointTimer.Stop();
                    output.WriteLine(
                        $"midpointTick={midpointTick}; midpointWindow={midpointTimer.Elapsed}; midpointEvents={midpointEventCount}");
                }
            }

            if (long.TryParse(
                    Environment.GetEnvironmentVariable("MIDORA_SCALE_DIAGNOSTIC_FRAME"),
                    out long diagnosticFrame)
                && plan.EventPageProvider is not null)
            {
                long diagnosticStart = Math.Max(0, diagnosticFrame - 48_000);
                ScheduledPortMidiMessage[] diagnosticEvents = plan.EventPageProvider.Query(
                        diagnosticStart,
                        Math.Min(plan.TotalFrameCount, diagnosticFrame + 48_001))
                    .ToArray();
                output.WriteLine($"diagnosticFrame={diagnosticFrame}; events={diagnosticEvents.Length}");
                IGrouping<(long Frame, int Unit), ScheduledPortMidiMessage>[] groups = diagnosticEvents
                    .GroupBy(value => (
                        Frame: value.Scheduled.SampleFrame,
                        Unit: value.ZeroBasedPortNumber * 16 + value.Scheduled.Message.ChannelNumber))
                    .OrderByDescending(value => value.Count())
                    .ThenBy(value => Math.Abs(value.Key.Frame - diagnosticFrame))
                    .Take(20)
                    .ToArray();
                foreach (IGrouping<(long Frame, int Unit), ScheduledPortMidiMessage> group in groups)
                {
                    string kinds = string.Join(",", group
                        .GroupBy(value => value.Scheduled.Message.MessageType)
                        .OrderBy(value => value.Key)
                        .Select(value => $"{value.Key}:{value.Count()}"));
                    output.WriteLine(
                        $"frame={group.Key.Frame}; unit={group.Key.Unit}; count={group.Count()}; kinds={kinds}");
                }
            }

            Assert.True(compiled.IsConsumable);
            Assert.True(compiled.HasPagedEvents);
            Assert.Empty(compiled.Events.ToArray());
            Assert.True(compiled.TotalNoteOnEventCount > 0);
            Assert.InRange(
                imported.Project.PureMidiTracks
                    .SelectMany(track => track.Segments)
                    .Sum(segment => segment.Notes.Count),
                1,
                long.MaxValue);

            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            long retainedHeap = GC.GetTotalMemory(forceFullCollection: true) - baselineHeap;
            MidiProjectImportMetrics metrics = Assert.IsType<MidiProjectImportMetrics>(imported.Metrics);
            output.WriteLine($"sample={path}");
            output.WriteLine($"fileBytes={metrics.SourceFileBytes}");
            output.WriteLine($"notes={metrics.ImportedNoteCount}; otherEvents={metrics.ImportedDirectEventCount}");
            output.WriteLine($"pages={metrics.ContentPageCount}; packBytes={metrics.ContentPackBytes}");
            output.WriteLine($"pass1={metrics.FirstPassElapsed}; pass2={metrics.SecondPassElapsed}; import={importTimer.Elapsed}; compile={compileTimer.Elapsed}");
            output.WriteLine($"plan={planTimer.Elapsed}; startupWindow={windowTimer.Elapsed}; startupEvents={startupEventCount}");
            output.WriteLine(
                $"retainedManagedBytes={retainedHeap}; peakManagedBytes={managedHeapSampler.PeakByteCount}; "
                + $"peakManagedDeltaBytes={Math.Max(0, managedHeapSampler.PeakByteCount - baselineHeap)}; "
                + $"peakWorkingSetBytes={process.PeakWorkingSet64}");
            byte[][] packHashes = contentPacks.Select(static pack =>
            {
                using FileStream stream = new(
                    pack.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                return SHA256.HashData(stream);
            }).ToArray();
            byte[] combinedHashInput = new byte[packHashes.Length * SHA256.HashSizeInBytes];
            for (int index = 0; index < packHashes.Length; index++)
            {
                packHashes[index].CopyTo(
                    combinedHashInput,
                    index * SHA256.HashSizeInBytes);
            }
            output.WriteLine(
                $"packCount={packHashes.Length}; packCombinedSha256="
                + Convert.ToHexStringLower(SHA256.HashData(combinedHashInput)));

        }
        finally
        {
            imported.Project.Dispose();
        }
    }

    private sealed class ManagedHeapSampler : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new(initialState: false);
        private readonly Thread _thread;
        private long _peakByteCount = GC.GetTotalMemory(forceFullCollection: false);
        private bool _stopped;

        public ManagedHeapSampler()
        {
            _thread = new(Sample)
            {
                IsBackground = true,
                Name = "Midora scale-test managed-heap sampler"
            };
            _thread.Start();
        }

        public long PeakByteCount => Volatile.Read(ref _peakByteCount);

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            _stop.Set();
            _thread.Join();
            Observe(GC.GetTotalMemory(forceFullCollection: false));
        }

        public void Dispose()
        {
            Stop();
            _stop.Dispose();
        }

        private void Sample()
        {
            do
            {
                Observe(GC.GetTotalMemory(forceFullCollection: false));
            }
            while (!_stop.Wait(TimeSpan.FromMilliseconds(25)));
        }

        private void Observe(long byteCount)
        {
            long previous = Volatile.Read(ref _peakByteCount);
            while (byteCount > previous)
            {
                long observed = Interlocked.CompareExchange(
                    ref _peakByteCount,
                    byteCount,
                    previous);
                if (observed == previous) return;
                previous = observed;
            }
        }
    }

}
