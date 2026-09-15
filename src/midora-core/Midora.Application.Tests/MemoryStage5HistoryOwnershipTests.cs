using System.Collections;
using System.Runtime.CompilerServices;
using Midora.Domain;
using Midora.Playback;
using Xunit.Abstractions;

namespace Midora.Application.Tests;

[CollectionDefinition("MemoryStage5HistoryOwnership", DisableParallelization = true)]
public sealed class MemoryStage5HistoryOwnershipCollection;

[Collection("MemoryStage5HistoryOwnership")]
public sealed class MemoryStage5HistoryOwnershipTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void LogicalHistorySharesUnchangedBranchesAcrossSixtyFourRevisions(int count)
    {
        using MidoraProject project = new(480);
        Segment current = new(project);
        current.Notes.AdoptSource(project, new GeneratedSource<LogicalNoteSnapshotValue>(count,
            i => new(new MidoraId(i + 100), i * 4L, 3, i % 128, 100)));
        var first = current.Notes.CreateQuerySnapshot();
        List<LogicalNoteQuerySnapshot> history = [first];
        HashSet<object> unique = new(ReferenceEqualityComparer.Instance);
        first.Values.CollectSequenceStorageNodes(unique);
        int initialNodes = unique.Count;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int revision = 1; revision <= 64; revision++)
        {
            var before = history[^1];
            int ordinal = revision * 997 % count;
            Segment next = new(project);
            next.Notes.AdoptEditedSnapshot(project, before,
                new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>([
                    new(ordinal, false, before.GetByOrdinal(ordinal) with { Velocity = revision })]));
            var after = next.Notes.CreateQuerySnapshot();
            int beforeNodes = unique.Count;
            after.Values.CollectSequenceStorageNodes(unique);
            Assert.InRange(unique.Count - beforeNodes, 1, 24);
            Assert.Equal(100, before.GetByOrdinal(ordinal).Velocity);
            Assert.Equal(revision, after.GetByOrdinal(ordinal).Velocity);
            Assert.True(after.TryFindOrdinalById(after.GetByOrdinal(ordinal).Id, out int found));
            Assert.Equal(ordinal, found);
            history.Add(after);
            current = next;
        }
        output.WriteLine($"Logical count={count}; revisions={history.Count}; initialSequenceNodes={initialNodes}; uniqueSequenceNodes={unique.Count}; exclusiveAddedNodes={unique.Count - initialNodes}; allocatedBytes={GC.GetAllocatedBytesForCurrentThread() - allocated}");
        Assert.Equal(100, first.GetByOrdinal(997).Velocity);
        Assert.Equal(first.GetByOrdinal(count - 1), history[^1].GetByOrdinal(count - 1));
        GC.KeepAlive(history);
        GC.KeepAlive(current);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void TemplateHistorySharesUnchangedBranchesWithoutRecreatingMappings(int count)
    {
        using MidoraProject project = new(480);
        SubVoice current = new(project);
        current.Events.AdoptSource(project, new GeneratedSource<TemplateEventSnapshotValue>(count,
            i => new(new MidoraId(i + 100), TemplateEventKind.Note, i * 4L, 3, i % 128, 100, 0, true, true, true)));
        var first = current.Events.CreateQuerySnapshot();
        HashSet<object> unique = new(ReferenceEqualityComparer.Instance);
        first.Values.CollectSequenceStorageNodes(unique);
        int initialNodes = unique.Count;
        var previous = first;
        for (int revision = 1; revision <= 64; revision++)
        {
            int ordinal = revision * 997 % count;
            SubVoice next = new(project);
            next.Events.AdoptEditedSnapshot(project, previous,
                new ArraySource<TimelineValueEdit<TemplateEventSnapshotValue>>([
                    new(ordinal, false, previous.GetByOrdinal(ordinal) with { Value = revision })]));
            var after = next.Events.CreateQuerySnapshot();
            int beforeNodes = unique.Count;
            after.Values.CollectSequenceStorageNodes(unique);
            Assert.InRange(unique.Count - beforeNodes, 1, 24);
            Assert.Equal(100, previous.GetByOrdinal(ordinal).Value);
            Assert.Equal(revision, after.GetByOrdinal(ordinal).Value);
            Assert.Empty(next.EventMappings);
            previous = after;
            current = next;
        }
        output.WriteLine($"Template count={count}; revisions=65; initialSequenceNodes={initialNodes}; uniqueSequenceNodes={unique.Count}; exclusiveAddedNodes={unique.Count - initialNodes}");
        Assert.Equal(100, first.GetByOrdinal(997).Value);
        Assert.Equal(first.GetByOrdinal(count - 1), previous.GetByOrdinal(count - 1));
        GC.KeepAlive(current);
    }

    [Fact]
    public void RepeatedSameLeafEditsReleaseSupersededSourcesAndPreserveScalarOverlays()
    {
        using MidoraProject project = new(480);
        var (current, obsolete, last) = ReplaceSameLeaf(project);
        Collect();
        Assert.All(obsolete, weak => Assert.False(weak.IsAlive));
        Assert.True(last.IsAlive);
        var values = current.Notes.CreateQuerySnapshot();
        Assert.Equal(511 % 128, values.GetByOrdinal(17).Velocity);
        Assert.Equal(23, values.GetByOrdinal(18).Velocity);
        Assert.Equal(100, values.GetByOrdinal(19).Velocity);
        GC.KeepAlive(current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Segment Current, List<WeakReference> Obsolete, WeakReference Last) ReplaceSameLeaf(MidoraProject project)
    {
        Segment current = new(project);
        current.Notes.AdoptSource(project, new GeneratedSource<LogicalNoteSnapshotValue>(8192,
            i => new(new MidoraId(i + 100), i * 4L, 3, i % 128, 100)));
        current.Notes[18].Velocity = 23;
        List<WeakReference> obsolete = [];
        WeakReference last = null!;
        for (int revision = 0; revision < 512; revision++)
        {
            var before = current.Notes.CreateQuerySnapshot();
            var changes = new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>([
                new(17, false, before.GetByOrdinal(17) with { Velocity = revision % 128 })]);
            last = new(changes);
            if (revision != 511) obsolete.Add(last);
            Segment next = new(project);
            next.Notes.AdoptEditedSnapshot(project, before, changes);
            current = next;
        }
        return (current, obsolete, last);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PreparedNotePasteReleasesSourceClipboardAndKeepsUndoRedo(bool directSource, bool directTarget)
    {
        using var project = new MidoraProject(480);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, EventInstrumentLibrary.Create(project, "Instrument").Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        Segment unrelated = new(project) { LengthTicks = 100, ProjectStartTick = 100_000 };
        track.Segments.Add(segment);
        track.Segments.Add(unrelated);
        MidiSegment midi = new(project) { LengthTicks = 100_000 };
        if (directTarget)
        {
            MidiChannelRoot root = new(project) { Name = "Root" };
            project.MidiChannelRoots.Add(root);
            PureMidiTrack direct = new(project) { Name = "Direct", MidiChannelRootId = root.Id };
            direct.Segments.Add(midi);
            project.PureMidiTracks.Add(direct);
        }
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(10));
        using var document = new ProjectDocumentSession(compilation);
        var target = directTarget ? midi.Id : segment.Id;
        using var fixture = new StorageFixture();
        ProjectObjectClipboardPayload payload = CreatePayload(document, fixture.Resources, directSource, 8192);
        long sourceSpill = fixture.Resources.SpillBytes;
        Assert.Equal(8192L * (directSource ? Unsafe.SizeOf<DirectMidiNoteClipboardSnapshot>()
            : Unsafe.SizeOf<LogicalNoteClipboardSnapshot>()), sourceSpill);
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteNotesCommand(document, payload, target, 10, directTarget);
        using StagedProjectEdit prepared = document.PrepareEdit(command);
        Assert.Equal(sourceSpill, fixture.Resources.SpillBytes);
        payload.Dispose();
        Assert.Equal(sourceSpill, fixture.Resources.SpillBytes); // The reusable command still owns its lease.
        ((IDisposable)command).Dispose();
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        document.ExecutePrepared(prepared);
        Assert.Same(track, project.Tracks[0]);
        Assert.Same(unrelated, project.Tracks[0].Segments[1]);
        Assert.Equal(8192, Count());
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal(0, Count());
        document.Redo();
        Assert.Equal(8192, Count());
        if (directTarget)
        {
            var value = project.PureMidiTracks[0].Segments[0].Notes.CreateObjectSource().GetByOrdinal(8191);
            Assert.Equal(directSource ? 37 : 0, value.NoteOffVelocity);
            Assert.Equal(16_392, value.StartTick);
        }
        else
        {
            var value = project.Tracks[0].Segments[0].Notes.CreateQuerySnapshot().GetByOrdinal(8191);
            Assert.Equal(16_392, value.StartTick);
            Assert.Equal(100, value.Velocity);
        }
        output.WriteLine($"source={(directSource ? "Direct" : "Logical")}; target={(directTarget ? "Direct" : "Logical")}; records=8192; sourceSpillBytesBefore={sourceSpill}; sourceSpillBytesAfter={fixture.Resources.SpillBytes}; undoRedo=exact");
        int Count() => directTarget ? project.PureMidiTracks[0].Segments[0].Notes.Count : project.Tracks[0].Segments[0].Notes.Count;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CancelledNotePasteKeepsSourceReusableWithoutPublishing(bool directSource, bool afterStorageReady)
    {
        using var project = new MidoraProject(480);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, EventInstrumentLibrary.Create(project, "Instrument").Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        track.Segments.Add(segment);
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(10));
        using var document = new ProjectDocumentSession(compilation);
        using var fixture = new StorageFixture();
        using var payload = CreatePayload(document, fixture.Resources, directSource, 8192);
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteNotesCommand(document, payload, segment.Id, 0, false);
        using var ownedCommand = (IDisposable)command;
        long nextId = project.NextStableId;
        long sourceSpill = fixture.Resources.SpillBytes;
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => document.PrepareEdit(command, cancellation.Token,
            new CancellingProgress(cancellation, afterStorageReady)));
        Assert.Empty(segment.Notes);
        Assert.Empty(document.History);
        Assert.Equal(nextId, project.NextStableId);
        Assert.Equal(sourceSpill, fixture.Resources.SpillBytes);
        using StagedProjectEdit prepared = document.PrepareEdit(command);
        document.ExecutePrepared(prepared);
        Assert.Equal(8192, project.Tracks[0].Segments[0].Notes.Count);
    }

    [Fact]
    public void DeepHistoryRetainsEveryExactRootAndNewBranchDropsOnlyRedo()
    {
        using var project = new MidoraProject(480);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, EventInstrumentLibrary.Create(project, "Instrument").Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        track.Segments.Add(segment);
        segment.Notes.AddRange(Enumerable.Range(0, 8192).Select(i => new LogicalNote(project)
            { StartTick = i * 4, LengthTicks = 3, Note = i % 128, Velocity = 100 }));
        MidoraId editedId = segment.Notes[17].Id;
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(10));
        using var document = new ProjectDocumentSession(compilation);
        List<Segment> roots = [segment];
        for (int revision = 1; revision <= 64; revision++)
        {
            document.Execute(ProjectDomainEditCommands.SetLogicalNoteValues(segment.Id, [editedId], velocity: revision));
            roots.Add(track.Segments[0]);
        }
        Assert.Equal(64, document.History.Count);
        for (int revision = 63; revision >= 0; revision--)
        {
            document.Undo();
            Assert.Same(roots[revision], track.Segments[0]);
            Assert.Equal(revision == 0 ? 100 : revision, track.Segments[0].Notes.CreateQuerySnapshot().GetByOrdinal(17).Velocity);
        }
        for (int revision = 1; revision <= 64; revision++)
        {
            document.Redo();
            Assert.Same(roots[revision], track.Segments[0]);
            Assert.Equal(revision, track.Segments[0].Notes.CreateQuerySnapshot().GetByOrdinal(17).Velocity);
        }
        document.Undo();
        document.Undo();
        document.Execute(ProjectDomainEditCommands.SetLogicalNoteValues(segment.Id, [editedId], velocity: 7));
        Assert.Equal(63, document.History.Count);
        Assert.False(document.CanRedo);
        document.Undo();
        Assert.Same(roots[62], track.Segments[0]);
        document.Redo();
        Assert.Equal(7, track.Segments[0].Notes.CreateQuerySnapshot().GetByOrdinal(17).Velocity);
        Assert.Equal(64, roots[64].Notes.CreateQuerySnapshot().GetByOrdinal(17).Velocity);
    }

    private static ProjectObjectClipboardPayload CreatePayload(ProjectDocumentSession document,
        BoundedEditResources resources, bool direct, int count)
    {
        using var context = BulkEditPreparationContext.Enter(resources: resources);
        using var capture = ClipboardCaptureScope.Enter();
        return direct
            ? new(document.ClipboardSessionIdentity, ProjectObjectClipboardKind.DirectMidiNotes, count, "notes",
                new DirectMidiNoteClipboardData(ClipboardCaptureScope.Capture(Enumerable.Range(0, count).Select(i =>
                    new DirectMidiNoteClipboardSnapshot(i * 2L, 1, i % 128, 100, 37, i * 2L, i * 2L + 1, true)))))
            : new(document.ClipboardSessionIdentity, ProjectObjectClipboardKind.LogicalNotes, count, "notes",
                new LogicalNoteClipboardData(ClipboardCaptureScope.Capture(Enumerable.Range(0, count).Select(i =>
                    new LogicalNoteClipboardSnapshot(i * 2L, 1, i % 128, 100)))));
    }

    [Fact]
    public void SplicedHistorySharesBranchesAndPreservesOriginalOrdinalOrder()
    {
        using MidoraProject project = new(480);
        Segment current = new(project);
        current.Notes.AdoptSource(project, NoteSource(100_000));
        var first = current.Notes.CreateQuerySnapshot();
        HashSet<object> nodes = new(ReferenceEqualityComparer.Instance);
        first.Values.CollectSequenceStorageNodes(nodes);
        int initial = nodes.Count;
        List<LogicalNoteQuerySnapshot> history = [first];
        for (int revision = 1; revision <= 32; revision++)
        {
            var before = history[^1];
            int ordinal = revision * 997;
            var replacement = before.GetByOrdinal(ordinal) with { Velocity = revision };
            Segment next = new(project);
            next.Notes.AdoptSplicedSnapshot(project, before,
                new ArraySource<TimelineValueSplice>([new(ordinal, 0, 1)]), new IndexedNoteSource([replacement]));
            var after = next.Notes.CreateQuerySnapshot();
            int prior = nodes.Count;
            after.Values.CollectSequenceStorageNodes(nodes);
            Assert.InRange(nodes.Count - prior, 1, 32);
            Assert.Equal(100, before.GetByOrdinal(ordinal).Velocity);
            Assert.Equal(replacement, after.GetByOrdinal(ordinal));
            Assert.True(after.TryFindOrdinalById(replacement.Id, out int found));
            Assert.Equal(ordinal, found);
            history.Add(after);
            current = next;
        }
        output.WriteLine($"Splice count=100000; revisions=33; initialSequenceNodes={initial}; uniqueSequenceNodes={nodes.Count}");
        Assert.Equal(100, first.GetByOrdinal(997).Velocity);
        GC.KeepAlive(history);
        GC.KeepAlive(current);
    }

    [Fact]
    public void SpliceFragmentsReleaseProvidersOutsideTheirRetainedRanges()
    {
        using MidoraProject project = new(480);
        var (current, obsolete) = ReplaceAcrossSpliceFragments(project);
        Collect();
        Assert.All(obsolete, weak => Assert.False(weak.IsAlive));
        var after = current.Notes.CreateQuerySnapshot();
        Assert.Equal(8193, after.Count);
        Assert.Equal(79, after.GetByOrdinal(0).Velocity);
        Assert.Equal(23, after.GetByOrdinal(1).Velocity);
        Assert.Equal(31, after.GetByOrdinal(2).Velocity);
        Assert.Equal(100, after.GetByOrdinal(3).Velocity);
        GC.KeepAlive(current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Segment Current, List<WeakReference> Obsolete) ReplaceAcrossSpliceFragments(MidoraProject project)
    {
        Segment current = new(project);
        current.Notes.AdoptSource(project, NoteSource(8192));
        List<WeakReference> obsolete = [];
        for (int revision = 0; revision < 80; revision++)
        {
            var before = current.Notes.CreateQuerySnapshot();
            var change = new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>([
                new(0, false, before.GetByOrdinal(0) with { Velocity = revision })]);
            if (revision != 79) obsolete.Add(new(change));
            Segment edited = new(project);
            edited.Notes.AdoptEditedSnapshot(project, before, change);
            var old = edited.Notes.CreateQuerySnapshot();
            Segment spliced = new(project);
            // The first split separates the changed slot from a fragment that
            // used to retain the whole old leaf; later replacements alternate
            // across that source boundary while preserving prior snapshots.
            LogicalNoteSnapshotValue[] values = revision == 0
                ? [old.GetByOrdinal(1) with { Velocity = 23 }, new(new MidoraId(200_000), 5, 1, 60, 31)]
                : [old.GetByOrdinal(1)];
            spliced.Notes.AdoptSplicedSnapshot(project, old,
                new ArraySource<TimelineValueSplice>([new(1, 0, values.Length)]), new IndexedNoteSource(values));
            Assert.Equal(revision, old.GetByOrdinal(0).Velocity);
            Assert.Equal(revision, spliced.Notes.CreateQuerySnapshot().GetByOrdinal(0).Velocity);
            current = spliced;
        }
        return (current, obsolete);
    }

    [Fact]
    public void DeletingEverySpliceLeafAndAppendingToEmptyKeepsOldRootAndRejectsMidBuildCancellation()
    {
        using MidoraProject project = new(480);
        Segment original = new(project);
        original.Notes.AdoptSource(project, NoteSource(384));
        var before = original.Notes.CreateQuerySnapshot();
        Segment empty = new(project);
        empty.Notes.AdoptSplicedSnapshot(project, before,
            new GeneratedSource<TimelineValueSplice>(384, i => new(i, 0, 0)), new IndexedNoteSource([]));
        Assert.Empty(empty.Notes);
        Segment restored = new(project);
        var replacement = new LogicalNoteSnapshotValue(new MidoraId(500_000), 3, 1, 60, 90);
        restored.Notes.AdoptEditedSnapshot(project, empty.Notes.CreateQuerySnapshot(),
            new ArraySource<TimelineValueEdit<LogicalNoteSnapshotValue>>([]), new IndexedNoteSource([replacement]));
        Assert.Equal(replacement, Assert.Single(restored.Notes.CreateQuerySnapshot().EnumerateAll()));
        Assert.Equal(384, before.Count);
        using CancellationTokenSource cancellation = new();
        int reads = 0;
        Segment cancelled = new(project);
        var changes = new GeneratedSource<TimelineValueSplice>(384, i =>
        {
            if (++reads == 160) cancellation.Cancel();
            return new(i, 0, 0);
        });
        Assert.Throws<OperationCanceledException>(() => cancelled.Notes.AdoptSplicedSnapshot(project,
            before, changes, new IndexedNoteSource([]), cancellation.Token));
        Assert.Empty(cancelled.Notes);
        Assert.Equal(384, original.Notes.Count);
    }

    [Fact]
    public void FlattenedFragmentCacheOnlyReadNeverCallsUncachedSource()
    {
        using MidoraProject project = new(480);
        int reads = 0;
        Segment original = new(project);
        original.Notes.AdoptSource(project, new GeneratedSource<LogicalNoteSnapshotValue>(256, i =>
        { reads++; return new(new MidoraId(i + 100), i * 4L, 3, i % 128, 100); }));
        Segment spliced = new(project);
        spliced.Notes.AdoptSplicedSnapshot(project, original.Notes.CreateQuerySnapshot(),
            new ArraySource<TimelineValueSplice>([new(1, 0, 0)]), new IndexedNoteSource([]));
        var frozen = spliced.Notes.CreateQuerySnapshot();
        int previousReads = reads;
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.Throws<TimelineValueReadPendingException>(() => frozen.GetByOrdinal(0));
        Assert.Equal(previousReads, reads);
        Assert.Equal(new MidoraId(100), frozen.GetByOrdinal(0).Id);
    }

    [Fact]
    public void SharedReadCacheDoesNotRootAnUnreferencedProvider()
    {
        using var fixture = new StorageFixture();
        using var project = new MidoraProject(480);
        var (provider, page) = CreateCachedProvider(project, fixture.Resources);
        Collect();
        Assert.False(provider.IsAlive);
        Assert.False(page.IsAlive);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void SpliceAndScalarHistoryUndoRedoRetainsEverySharedOwnerRoot()
    {
        using var project = new MidoraProject(480);
        LogicalTrack track = new(project) { Name = "Logical" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, EventInstrumentLibrary.Create(project, "Instrument").Id);
        Segment original = new(project) { LengthTicks = 100_000 };
        track.Segments.Add(original);
        original.Notes.AddRange(Enumerable.Range(0, 8192).Select(i => new LogicalNote(project)
            { StartTick = i * 4, LengthTicks = 3, Note = i % 128, Velocity = 100 }));
        MidoraId edited = original.Notes[0].Id, split = original.Notes[1].Id;
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(10));
        using var document = new ProjectDocumentSession(compilation);
        List<Segment> roots = [original];
        document.Execute(ProjectDomainEditCommands.SetLogicalNoteValues(original.Id, [edited], velocity: 11));
        roots.Add(track.Segments[0]);
        using (var prepared = document.PrepareEdit(ProjectDomainEditCommands.SplitLogicalNotes(original.Id, [split],
            new() { Mode = NoteSplitMode.MaximumPieceCount, MaximumPieceCount = 2 })))
        {
            Assert.Same(roots[1], track.Segments[0]);
            document.ExecutePrepared(prepared);
        }
        roots.Add(track.Segments[0]);
        Assert.Equal(8193, roots[2].Notes.Count);
        document.Execute(ProjectDomainEditCommands.SetLogicalNoteValues(original.Id, [edited], velocity: 77));
        roots.Add(track.Segments[0]);
        for (int index = 2; index >= 0; index--)
        {
            document.Undo();
            Assert.Same(roots[index], track.Segments[0]);
        }
        for (int index = 1; index <= 3; index++)
        {
            document.Redo();
            Assert.Same(roots[index], track.Segments[0]);
        }
        Assert.Equal(11, roots[1].Notes.CreateQuerySnapshot().GetByOrdinal(0).Velocity);
        Assert.Equal(77, roots[3].Notes.CreateQuerySnapshot().GetByOrdinal(0).Velocity);
        Assert.Equal(100, original.Notes.CreateQuerySnapshot().GetByOrdinal(0).Velocity);
    }

    [Fact]
    public void TemplatePreparedRootKeepsLengthMappingsAndRejectsStalePublication()
    {
        using var project = new MidoraProject(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        SubVoice original = instrument.SubVoices[0];
        original.Events.AddRange(Enumerable.Range(0, 8192).Select(i => TemplateEvent.Note(project, i * 4L, 3, i % 128, 100)));
        instrument.TemplateLengthTicks = 40_000;
        MidoraId edited = original.Events[17].Id;
        int mappings = original.EventMappings.Count;
        using var compilation = new ProjectCompilationSession(project,
            executionMode: ProjectCompilationExecutionMode.Background, backgroundDebounce: TimeSpan.FromMinutes(10));
        using var document = new ProjectDocumentSession(compilation);
        using (var prepared = document.PrepareEdit(ProjectDomainEditCommands.MoveTemplateNotes(instrument.Id, original.Id, [edited], 100_000, 1)))
        {
            Assert.Same(original, instrument.SubVoices[0]);
            Assert.Equal(40_000, instrument.TemplateLengthTicks);
            document.ExecutePrepared(prepared);
        }
        SubVoice changed = instrument.SubVoices[0];
        Assert.NotSame(original, changed);
        Assert.Equal(mappings, changed.EventMappings.Count);
        Assert.Equal(100_071, instrument.TemplateLengthTicks);
        Assert.Equal(100_068, changed.Events.CreateQuerySnapshot().GetByOrdinal(17).Tick);
        document.Undo();
        Assert.Same(original, instrument.SubVoices[0]);
        Assert.Equal(40_000, instrument.TemplateLengthTicks);
        document.Redo();
        Assert.Same(changed, instrument.SubVoices[0]);
        using var stale = document.PrepareEdit(ProjectDomainEditCommands.MoveTemplateNotes(instrument.Id, original.Id, [edited], 1, 0));
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(1, "revision"));
        Assert.Throws<InvalidOperationException>(() => document.ExecutePrepared(stale));
        Assert.Same(changed, instrument.SubVoices[0]);
        Assert.Equal(100_071, instrument.TemplateLengthTicks);
        Assert.Equal(2, document.History.Count);
        Assert.Equal(68, original.Events.CreateQuerySnapshot().GetByOrdinal(17).Tick);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Provider, WeakReference Page) CreateCachedProvider(MidoraProject project, BoundedEditResources resources)
    {
        using var context = BulkEditPreparationContext.Enter(project: project, resources: resources);
        var store = new BoundedEditRecordStore<long>(resources);
        store.AddRange(Enumerable.Range(0, 128).Select(static i => (long)i));
        store.Seal(); store.SpillResidentPages();
        var provider = new BoundedImmutableValueSource<long>(store);
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(provider.ReadPage(0), out ArraySegment<long> page));
        return (new(provider), new(page.Array!));
    }

    private static GeneratedSource<LogicalNoteSnapshotValue> NoteSource(int count) => new(count,
        i => new(new MidoraId(i + 100), i * 4L, 3, i % 128, 100));

    private sealed class IndexedNoteSource(LogicalNoteSnapshotValue[] values) : IIndexedImmutableTimelineValueSource<LogicalNoteSnapshotValue>
    {
        private readonly NoteIndex _index = new(values.Select((value, ordinal) => (value.Id, ordinal))
            .ToDictionary(static pair => pair.Id, static pair => pair.ordinal));
        public int Count => values.Length;
        public int PageCapacity => 128;
        public LogicalNoteSnapshotValue this[int index] => values[index];
        public IImmutableTimelineIdIndex DetachedIdIndex => _index;
        public bool TryFindOrdinalById(MidoraId id, out int ordinal) => _index.TryFindOrdinalById(id, out ordinal);
        public ReadOnlyMemory<LogicalNoteSnapshotValue> ReadPage(int pageIndex)
        { int first = pageIndex * PageCapacity; return values.AsMemory(first, Math.Min(PageCapacity, values.Length - first)); }
        public IEnumerator<LogicalNoteSnapshotValue> GetEnumerator() => ((IEnumerable<LogicalNoteSnapshotValue>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private sealed class NoteIndex(Dictionary<MidoraId, int> values) : IImmutableTimelineIdIndex
        {
            public bool TryFindOrdinalById(MidoraId id, out int ordinal) => values.TryGetValue(id, out ordinal);
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation, bool afterStorageReady)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value)
        {
            if (!afterStorageReady || value.Phase == TimelineEditPreparationPhase.Ready) cancellation.Cancel();
        }
    }

    private sealed class StorageFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "midora-stage5-ownership-" + Guid.NewGuid().ToString("N"));
        public BoundedEditResources Resources { get; }
        public StorageFixture()
        {
            Directory.CreateDirectory(_path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 1024,
                maximumWorkingBytes: 256 * 1024, pageRecordCount: 64), _path);
        }
        public void Dispose() => Directory.Delete(_path, recursive: true);
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class GeneratedSource<T>(int count, Func<int, T> value) : IImmutableTimelineValueSource<T>
    {
        public int Count => count;
        public int PageCapacity => 4096;
        public T this[int index] => value(index);
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            int first = pageIndex * PageCapacity;
            return Enumerable.Range(first, Math.Min(PageCapacity, Count - first)).Select(value).ToArray();
        }
        public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return value(i); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ArraySource<T>(T[] values) : IImmutableTimelineValueSource<T>
    {
        public int Count => values.Length;
        public int PageCapacity => 128;
        public T this[int index] => values[index];
        public ReadOnlyMemory<T> ReadPage(int pageIndex) => pageIndex == 0 ? values : throw new ArgumentOutOfRangeException(nameof(pageIndex));
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
