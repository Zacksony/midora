using Midora.Compiler;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

/// <summary>
/// F10 crosses the independent small-object and paged command adapters. This is
/// a bounded data-chain oracle, not WPF, device-audio or an application RAM cap.
/// </summary>
[Collection("MemoryStage5HistoryOwnership")]
public sealed class MemoryStage6CrossOperationTests(ITestOutputHelper output)
{
    private const uint SequenceSeed = 0x6D_69_64_36;

    [Theory]
    [InlineData(31)]
    [InlineData(4_101)]
    public async Task SeededSixOwnerEditsSurviveCancelHistoryBranchClipboardAndEditedPackageRoundTrip(int count)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, ".tmp", "memory-stage6-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var project = CreateProject(count);
            var resources = new BoundedEditResources(new PagedEditResourceBudget(
                maximumResidentBytes: 64 * 1024, maximumWorkingBytes: 8 * 1024 * 1024), directory);
            using var context = BulkEditPreparationContext.Enter(resources: resources, project: project);
            // Keep the product's normal 75 ms scheduling. A test-only ten-minute
            // debounce can already be awaiting its delay before Ensure requests
            // an immediate compile; that fixture delay is not product latency.
            using var compilation = new ProjectCompilationSession(project,
                executionMode: ProjectCompilationExecutionMode.Background);
            using var document = new ProjectDocumentSession(compilation, ProjectDocumentOrigin.Persisted);
            List<SourceState> states = [Capture(project)];
            List<int> operationOrder = Enumerable.Range(0, 18).ToList();
            uint random = SequenceSeed;
            for (int index = operationOrder.Count - 1; index > 0; index--)
            {
                random ^= random << 13;
                random ^= random >> 17;
                random ^= random << 5;
                int other = (int)(random % (uint)(index + 1));
                (operationOrder[index], operationOrder[other]) = (operationOrder[other], operationOrder[index]);
            }
            foreach (int operation in operationOrder)
            {
                var before = Capture(project);
                Assert.True(document.Execute(CreateOperation(project, operation)).Changed);
                var after = Capture(project);
                AssertOperationEffect(operation, before, after);
                states.Add(after);
                Assert.Equal(states.Count - 1, document.History.Count);
                AssertOriginalOpaqueAndUneditedDuplicates(project);
            }

            // Cancel after the complete transformed storage exists, not just at
            // method entry. No history, allocator, root or source value changes.
            long nextId = project.NextStableId;
            using (var cancellation = new CancellationTokenSource())
            {
                Assert.Throws<OperationCanceledException>(() => document.PrepareEdit(
                    CreateOperation(project, 6), cancellation.Token,
                    new CallbackProgress(value =>
                    {
                        if (value.Phase == TimelineEditPreparationPhase.Ready) cancellation.Cancel();
                    })));
            }
            Assert.Equal(nextId, project.NextStableId);
            Assert.Equal(18, document.History.Count);
            AssertState(states[^1], Capture(project));
            Assert.Equal(0, resources.ResidentBytes);
            Assert.Equal(0, resources.WorkingBytes);

            for (int index = states.Count - 2; index >= 0; index--)
            {
                document.Undo();
                AssertState(states[index], Capture(project));
            }
            Assert.False(document.IsModified);
            for (int index = 1; index < states.Count; index++)
            {
                document.Redo();
                AssertState(states[index], Capture(project));
            }

            // New history must discard only the Redo branch; save while edits
            // and the new branch are applied, never merely after undo to source.
            document.Undo();
            document.Undo();
            var branchBase = Capture(project);
            Assert.True(document.CanRedo);
            using (StagedProjectEdit stale = document.PrepareEdit(CreateOperation(project, 0)))
            {
                Assert.True(document.Execute(ProjectDomainEditCommands.CreateProjectMarker(321, "阶段 6 / seed")).Changed);
                var afterBranchCommit = Capture(project);
                Assert.Throws<InvalidOperationException>(() => document.ExecutePrepared(stale));
                AssertState(afterBranchCommit, Capture(project));
            }
            Assert.False(document.CanRedo);
            Assert.Equal(17, document.History.Count);
            var branch = Capture(project);
            document.Undo();
            AssertState(branchBase, Capture(project));
            document.Redo();
            AssertState(branch, Capture(project));

            // Clipboard snapshots are replaced while old history remains live.
            // Logical -> Direct must set NoteOff velocity to zero; Direct ->
            // Logical keeps just the shared fields, and both receive fresh IDs.
            using (var logicalPayload = ProjectObjectClipboard.CopyLogicalNotes(document,
                project.Tracks[0].Segments[0].Id, branch.Logical.Take(7).Select(value => value.Id).ToArray()))
            {
                IProjectEditCommand paste = ProjectObjectClipboard.CreatePasteNotesCommand(document,
                    logicalPayload, project.PureMidiTracks[0].Segments[0].Id, 200_000, true);
                try { Assert.True(document.Execute(paste).Changed); }
                finally { (paste as IDisposable)?.Dispose(); }
            }
            var pastedDirect = project.PureMidiTracks[0].Segments[0].Notes.EnumerateValues()
                .Where(value => value.StartTick >= 200_000).ToArray();
            Assert.Equal(7, pastedDirect.Length);
            Assert.All(pastedDirect, value => Assert.Equal(0, value.NoteOffVelocity));
            Assert.DoesNotContain(pastedDirect, value => branch.Logical.Any(original => original.Id == value.Id));
            using (var directPayload = ProjectObjectClipboard.CopyDirectMidiNotes(document,
                project.PureMidiTracks[0].Segments[0].Id, branch.Direct.Take(7).Select(value => value.Id).ToArray()))
            {
                IProjectEditCommand paste = ProjectObjectClipboard.CreatePasteNotesCommand(document,
                    directPayload, project.Tracks[0].Segments[0].Id, 200_000, false);
                try { Assert.True(document.Execute(paste).Changed); }
                finally { (paste as IDisposable)?.Dispose(); }
            }
            var edited = Capture(project);
            Assert.Equal(count + 7, edited.Logical.Length);
            Assert.Equal(count + 9, edited.Direct.Length);
            for (int index = 0; index < 7; index++)
            {
                var copied = edited.Logical[count + index];
                var source = branch.Direct[index];
                Assert.Equal((source.LengthTicks, source.Key, source.NoteOnVelocity),
                    (copied.LengthTicks, copied.Note, copied.Velocity));
                Assert.NotEqual(source.Id, copied.Id);
            }
            document.Undo();
            document.Undo();
            AssertState(branch, Capture(project));
            document.Redo();
            document.Redo();
            AssertState(edited, Capture(project));

            CanonicalCompiledResult incremental = await compilation.EnsureCurrentCompilationAsync()
                .WaitAsync(TimeSpan.FromSeconds(60));
            using var oracleCompiler = new MidoraCompiler();
            CanonicalCompiledResult full = oracleCompiler.CompileFull(project);
            Assert.True(full.IsConsumable, string.Join("\n", full.Diagnostics));
            Assert.Equal(full.Fingerprint, incremental.Fingerprint);
            AssertCanonical(full, incremental);

            var packages = new MidoraProjectPackageV1("1.0.0-test");
            string savedPath = Path.Combine(directory, "edited.midora");
            string copyPath = Path.Combine(directory, "copy.midora");
            await packages.SaveProjectAsync(project, savedPath);
            await packages.SaveCopyAsync(project, copyPath);
            foreach (string path in new[] { savedPath, copyPath })
            {
                var opened = await packages.OpenAsync(path);
                using var restored = opened.Project;
                using var restoredCompiler = new MidoraCompiler();
                Assert.Empty(opened.Diagnostics);
                AssertState(edited, Capture(restored));
                // Pure MIDI pack flattening changes the source-aware cache
                // fingerprint. Formal events/diagnostics are the cross-file
                // oracle; same-revision Full/Incremental above is stricter.
                AssertCanonical(full, restoredCompiler.CompileFull(restored));
            }
            byte[] savedBytes = await File.ReadAllBytesAsync(savedPath);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => packages.SaveCopyAsync(
                    project, savedPath, overwriteAuthorized: true, cancellationToken: cancellation.Token));
            }
            Assert.Equal(savedBytes, await File.ReadAllBytesAsync(savedPath));
            AssertState(edited, Capture(project));
            document.Undo(); // Saving must not consume or invalidate the history.
            document.Redo();
            AssertState(edited, Capture(project));
            AssertOriginalOpaqueAndUneditedDuplicates(project);
            output.WriteLine($"seed={SequenceSeed}; countPerOwner={count}; order={string.Join(',', operationOrder)}; "
                + $"history={document.History.Count}; peakWorking={resources.PeakWorkingBytes}; peakResident={resources.PeakResidentBytes}; "
                + $"spillWhileHistoryAlive={resources.SpillBytes}; canonicalEvents={full.TotalEventCount}; editedSaveAndCopy=exact");
            Assert.InRange(resources.PeakWorkingBytes, 0, resources.Budget.MaximumWorkingBytes);
            Assert.InRange(resources.PeakResidentBytes, 0, resources.Budget.MaximumResidentBytes);
            document.Dispose();
            compilation.Dispose();
            oracleCompiler.Dispose();
            project.Dispose();
            Assert.Equal(0, resources.WorkingBytes);
            Assert.Equal(0, resources.ResidentBytes);
            Assert.Equal(0, resources.SpillBytes);
            Assert.Empty(Directory.EnumerateDirectories(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MidoraProject CreateProject(int count)
    {
        MidoraProject project = new(480);
        project.Metadata.ProjectName = "Stage 6 mixed 编辑";
        var instrument = EventInstrumentLibrary.Create(project, "Logical Instrument");
        instrument.TemplateLengthTicks = 8;
        instrument.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 2, 60, 80));
        LogicalTrack logical = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, logical, instrument.Id);
        Segment segment = new(project) { LengthTicks = 300_000 };
        logical.Segments.Add(segment);
        LogicalParameterDefinition parameter = new(project)
        { Name = "Parameter", Type = LogicalParameterType.Double, Minimum = 0, Maximum = 1 };
        instrument.LogicalParameters.Add(parameter);
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        segment.ParameterLanes.Add(lane);
        var templateInstrument = EventInstrumentLibrary.Create(project, "Unbound template editor");
        templateInstrument.TemplateLengthTicks = 300_000;
        var voice = templateInstrument.SubVoices[0];
        MidiChannelRoot root = new(project) { Name = "Root" };
        project.MidiChannelRoots.Add(root);
        PureMidiTrack direct = new(project) { Name = "Direct", MidiChannelRootId = root.Id };
        project.PureMidiTracks.Add(direct);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, direct.Id));
        MidiSegment midi = new(project) { LengthTicks = 300_000 };
        direct.Segments.Add(midi);
        for (int index = 0; index < count; index++)
        {
            long tick = 32 + index * 16L;
            segment.Notes.Add(new(project) { StartTick = tick, LengthTicks = 4, Note = 60, Velocity = 80 });
            lane.Points.Add(new(project, tick, 0.25, CurveInterpolation.Step));
            voice.Events.Add(TemplateEvent.Note(project, tick, 4, 60, 80));
            voice.Events.Add(TemplateEvent.ControlChange(project, tick, 11, 40));
            midi.Notes.Add(new(project) { StartTick = tick, LengthTicks = 4, Key = 60,
                NoteOnVelocity = 80, NoteOffVelocity = 31, NoteOnOrder = index * 3L, NoteOffOrder = index * 3L + 1 });
            midi.ChannelEvents.Add(new(project) { Tick = tick, Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 11, Data2 = 40, Order = index * 3L + 2 });
        }
        // Imported duplicates outside every edited key must survive all edits.
        for (int index = 0; index < 2; index++)
        {
            midi.Notes.Add(new(project) { StartTick = 100_000, LengthTicks = 4 + index, Key = 12,
                NoteOnVelocity = 90 + index, NoteOffVelocity = 51 + index,
                NoteOnOrder = count * 3L + index * 3, NoteOffOrder = count * 3L + index * 3 + 1 });
            midi.ChannelEvents.Add(new(project) { Tick = 100_000, Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = 12, Data2 = 50 + index, Order = count * 3L + index * 3 + 2 });
        }
        midi.OpaqueEvents.Add(new(project) { Tick = 100_010, Kind = OpaqueMidiEventKind.Meta,
            MetaType = 0x7f, Payload = [0, 1, 0x7f, 0xff], Order = count * 3L + 6 });
        return project;
    }

    private static IProjectEditCommand CreateOperation(MidoraProject project, int operation)
    {
        Segment logical = project.Tracks[0].Segments[0];
        MidiSegment midi = project.PureMidiTracks[0].Segments[0];
        EventInstrument instrument = project.EventInstruments[1];
        SubVoice voice = instrument.SubVoices[0];
        var logicalIds = logical.Notes.CreateQuerySnapshot().EnumerateAll().Select(value => value.Id).ToArray();
        var directIds = midi.Notes.EnumerateValues().Where(value => value.StartTick < 100_000).Select(value => value.Id).ToArray();
        var templateIds = voice.Events.CreateQuerySnapshot().EnumerateAll()
            .Where(value => value.Kind == TemplateEventKind.Note).Select(value => value.Id).ToArray();
        var humanize = new TimelineHumanizeOptions(TimelineHumanizeField.Disabled,
            TimelineHumanizeField.Disabled, TimelineHumanizeField.Add(1, 7), SequenceSeed);
        return operation switch
        {
            0 => ProjectDomainEditCommands.MoveLogicalNotes(logical.Id, logicalIds, 1, 1),
            1 => ProjectDomainEditCommands.MoveDirectMidiNotes(midi.Id, directIds, 1, 1),
            2 => ProjectDomainEditCommands.MoveTemplateNotes(instrument.Id, voice.Id, templateIds, 1, 1),
            3 => ProjectDomainEditCommands.AdjustLogicalNoteEdges(logical.Id, logicalIds, 1, 2),
            4 => ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(midi.Id, directIds, 1, 2),
            5 => ProjectDomainEditCommands.AdjustTemplateNoteEdges(instrument.Id, voice.Id, templateIds, 1, 2),
            6 => ProjectDomainEditCommands.HumanizeLogicalNotes(logical.Id, logicalIds, humanize),
            7 => ProjectDomainEditCommands.HumanizeDirectMidiNotes(midi.Id, directIds, humanize),
            8 => ProjectDomainEditCommands.HumanizeTemplateNotes(instrument.Id, voice.Id, templateIds, humanize),
            _ when operation % 3 == 0 => ProjectDomainEditCommands.AdjustLogicalParameterPoints(logical.Id,
                logical.ParameterLanes[0].Id, logical.ParameterLanes[0].Points.CreateQuerySnapshot().EnumerateAll()
                    .Select(value => value.Id).ToArray(), 1, 0.125),
            _ when operation % 3 == 1 => ProjectDomainEditCommands.AdjustDirectMidiEventPoints(midi.Id,
                midi.ChannelEvents.EnumerateValues().Where(value => value.Tick < 100_000).Select(value => value.Id).ToArray(), 1, 0, 3, false),
            _ => ProjectDomainEditCommands.AdjustSubVoiceEventPoints(instrument.Id, voice.Id,
                voice.Events.CreateQuerySnapshot().EnumerateAll().Where(value => value.Kind == TemplateEventKind.ControlChange)
                    .Select(value => value.Id).ToArray(), MidiValueTarget.ControlChange(11), 1, 3, false)
        };
    }

    private static void AssertOperationEffect(int operation, SourceState before, SourceState after)
    {
        if (operation < 9)
        {
            int owner = operation % 3;
            Assert.Equal(before.Points, after.Points);
            Assert.Equal(before.DirectEvents, after.DirectEvents);
            Assert.Equal(before.Templates.Where(value => value.Kind != TemplateEventKind.Note),
                after.Templates.Where(value => value.Kind != TemplateEventKind.Note));
            if (owner != 0) Assert.Equal(before.Logical, after.Logical);
            if (owner != 1) Assert.Equal(before.Direct, after.Direct);
            if (owner != 2) Assert.Equal(before.Templates, after.Templates);
            NoteValue[] old = Notes(before, owner);
            NoteValue[] current = Notes(after, owner);
            Assert.Equal(old.Length, current.Length);
            for (int index = 0; index < old.Length; index++)
            {
                NoteValue expected = operation < 3 ? old[index] with { Tick = old[index].Tick + 1, Key = old[index].Key + 1 }
                    : operation < 6 ? old[index] with { Tick = old[index].Tick + 1, Gate = old[index].Gate + 1 }
                    : old[index] with { Velocity = current[index].Velocity };
                Assert.Equal(expected, current[index]);
                if (operation >= 6) Assert.InRange(current[index].Velocity - old[index].Velocity, 1, 7);
            }
        }
        else
        {
            Assert.Equal(before.Logical, after.Logical);
            Assert.Equal(before.Direct, after.Direct);
            Assert.Equal(before.Templates.Where(value => value.Kind == TemplateEventKind.Note),
                after.Templates.Where(value => value.Kind == TemplateEventKind.Note));
            if (operation % 3 == 0)
            {
                Assert.Equal(before.Points.Select(value => value with { Tick = value.Tick + 1, Value = value.Value + 0.125 }), after.Points);
                Assert.Equal(before.DirectEvents, after.DirectEvents);
                Assert.Equal(before.Templates, after.Templates);
            }
            else if (operation % 3 == 1)
            {
                Assert.Equal(before.DirectEvents.Select(value => value.Tick < 100_000
                    ? value with { Tick = value.Tick + 1, Data2 = value.Data2 + 3 } : value), after.DirectEvents);
                Assert.Equal(before.Points, after.Points);
                Assert.Equal(before.Templates, after.Templates);
            }
            else
            {
                Assert.Equal(before.Templates.Select(value => value.Kind == TemplateEventKind.ControlChange
                    ? value with { Tick = value.Tick + 1, Value = value.Value + 3 } : value), after.Templates);
                Assert.Equal(before.Points, after.Points);
                Assert.Equal(before.DirectEvents, after.DirectEvents);
            }
        }
        Assert.Equal(before.Opaque, after.Opaque);
        Assert.Equal(before.Markers, after.Markers);
    }

    private static NoteValue[] Notes(SourceState source, int owner) => owner switch
    {
        0 => source.Logical.Select(value => new NoteValue(value.Id, value.StartTick, value.LengthTicks, value.Note, value.Velocity, 0, 0, 0)).ToArray(),
        1 => source.Direct.Where(value => value.StartTick < 100_000).Select(value => new NoteValue(value.Id, value.StartTick,
            value.LengthTicks, value.Key, value.NoteOnVelocity, value.NoteOffVelocity, value.NoteOnOrder, value.NoteOffOrder)).ToArray(),
        _ => source.Templates.Where(value => value.Kind == TemplateEventKind.Note).Select(value =>
            new NoteValue(value.Id, value.Tick, value.LengthTicks, value.Number, value.Value, 0, 0, 0)).ToArray()
    };

    private static SourceState Capture(MidoraProject project)
    {
        var logical = project.Tracks[0].Segments[0];
        var midi = project.PureMidiTracks[0].Segments[0];
        return new(logical.Notes.CreateQuerySnapshot().EnumerateAll().ToArray(),
            logical.ParameterLanes[0].Points.CreateQuerySnapshot().EnumerateAll().ToArray(),
            project.EventInstruments[1].SubVoices[0].Events.CreateQuerySnapshot().EnumerateAll().ToArray(),
            midi.Notes.EnumerateValues().ToArray(), midi.ChannelEvents.EnumerateValues().ToArray(),
            midi.OpaqueEvents.EnumerateValues().Select(value =>
                $"{value.Id}:{value.Tick}:{value.Kind}:{value.MetaType}:{value.Order}:{Convert.ToHexString(value.Payload.Span)}").ToArray(),
            project.Conductor.Markers.Select(value => $"{value.Id}:{value.Tick}:{value.Name}").ToArray());
    }

    private static void AssertState(SourceState expected, SourceState actual)
    {
        Assert.Equal(expected.Logical, actual.Logical);
        Assert.Equal(expected.Points, actual.Points);
        Assert.Equal(expected.Templates, actual.Templates);
        Assert.Equal(expected.Direct, actual.Direct);
        Assert.Equal(expected.DirectEvents, actual.DirectEvents);
        Assert.Equal(expected.Opaque, actual.Opaque);
        Assert.Equal(expected.Markers, actual.Markers);
    }

    private static void AssertOriginalOpaqueAndUneditedDuplicates(MidoraProject project)
    {
        var midi = project.PureMidiTracks[0].Segments[0];
        Assert.Equal(new[] { (90, 51), (91, 52) }, midi.Notes.EnumerateValues()
            .Where(value => value.StartTick == 100_000).Select(value => (value.NoteOnVelocity, value.NoteOffVelocity)));
        Assert.Equal(new[] { 50, 51 }, midi.ChannelEvents.EnumerateValues()
            .Where(value => value.Tick == 100_000).Select(value => value.Data2));
        Assert.Equal(new byte[] { 0, 1, 0x7f, 0xff }, Assert.Single(midi.OpaqueEvents.EnumerateValues()).Payload.ToArray());
    }

    private static void AssertCanonical(CanonicalCompiledResult expected, CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.TotalEventCount, actual.TotalEventCount);
        Assert.Equal(expected.TotalNoteOnEventCount, actual.TotalNoteOnEventCount);
        Assert.Equal(expected.QueryEventPages(expected.StartTick, expected.EndTick).SelectMany(page => page.Items),
            actual.QueryEventPages(actual.StartTick, actual.EndTick).SelectMany(page => page.Items));
        Assert.Equal(expected.Diagnostics.ToArray(), actual.Diagnostics.ToArray());
    }

    private sealed record SourceState(LogicalNoteSnapshotValue[] Logical, CurvePointSnapshotValue[] Points,
        TemplateEventSnapshotValue[] Templates, DirectMidiNoteValue[] Direct,
        DirectMidiChannelEventValue[] DirectEvents, string[] Opaque, string[] Markers);
    private readonly record struct NoteValue(MidoraId Id, long Tick, long Gate, int Key, int Velocity,
        int NoteOffVelocity, long NoteOnOrder, long NoteOffOrder);
    private sealed class CallbackProgress(Action<TimelineEditPreparationProgress> callback) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => callback(value); }
}
