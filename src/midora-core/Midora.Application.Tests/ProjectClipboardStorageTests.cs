using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectClipboardStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrangementDirectoryBudgetRejectsBeforePreparationAndLeavesNoReservation(bool delete)
    {
        using var fixture = new StorageFixture();
        using var context = BulkEditPreparationContext.Enter(resources: fixture.Resources);
        using var project = new MidoraProject(192);
        LogicalTrack track = new(project) { Name = "Many Segments" };
        project.Tracks.Add(track);
        for (int index = 0; index < 3000; index++)
            track.Segments.Add(new(project) { ProjectStartTick = index * 4, LengthTicks = 2 });
        MidoraId id = track.Segments[0].Id;
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        IProjectEditCommand command = delete
            ? ProjectDomainEditCommands.DeleteArrangementSegments([id])
            : ProjectDomainEditCommands.SetArrangementSegmentValues([id], lengthTicks: 3);
        Assert.Throws<InvalidOperationException>(() => document.PrepareEdit(command));
        Assert.Same(track, Assert.Single(project.Tracks));
        Assert.Equal(3000, track.Segments.Count);
        Assert.Equal(2, track.Segments[0].LengthTicks);
        Assert.Empty(document.History);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void CatalogMetadataBudgetFailsBeforeAllocatingAndLeaseRetainsAccounting()
    {
        using var fixture = new StorageFixture();
        using var context = BulkEditPreparationContext.Enter(resources: fixture.Resources);
        ProjectObjectClipboardPayload payload;
        using (ClipboardCaptureScope scope = ClipboardCaptureScope.Enter())
        {
            ClipboardCaptureScope.ReserveMetadata(4, 512);
            payload = new(new object(), ProjectObjectClipboardKind.LogicalNotes, 0,
                "empty", new LogicalNoteClipboardData([]));
        }
        Assert.Equal(2048, fixture.Resources.WorkingBytes);
        using (ClipboardCaptureScope scope = ClipboardCaptureScope.Enter())
            Assert.Throws<InvalidOperationException>(() => ClipboardCaptureScope.ReserveMetadata(
                fixture.Resources.Budget.MaximumWorkingBytes, 2));
        Assert.Equal(2048, fixture.Resources.WorkingBytes);
        IDisposable lease = payload.AcquireStorageLease();
        payload.Dispose();
        Assert.Equal(2048, fixture.Resources.WorkingBytes);
        lease.Dispose();
        Assert.Equal(0, fixture.Resources.WorkingBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelAfterSecondaryProviderAllocationReleasesAllSpillImmediately(bool synchronous)
    {
        using var fixture = new StorageFixture();
        using var cancellation = new CancellationTokenSource();
        using var context = BulkEditPreparationContext.Enter(cancellation.Token, resources: fixture.Resources);
        using var project = new MidoraProject(192);
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        var command = new BoundedProjectEditCommand("Cancelled provider", (_, token, _) =>
        {
            var values = new BoundedEditRecordStore<long>(fixture.Resources);
            values.AddRange(Enumerable.Range(0, 10_000).Select(static value => (long)value), token);
            values.Seal();
            _ = new BoundedImmutableValueSource<long>(values);
            Assert.True(fixture.Resources.SpillBytes > 0);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        });
        if (synchronous)
            Assert.Throws<OperationCanceledException>(() => document.Execute(command));
        else
            Assert.Throws<OperationCanceledException>(() => document.PrepareEdit(command, cancellation.Token));
        Assert.Empty(document.History);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
    }

    [Fact]
    public void CopyUsesSpillPagesAndClipboardLeasePreservesThemUntilLastRelease()
    {
        using var fixture = new StorageFixture();
        using BulkEditPreparationContext context = BulkEditPreparationContext.Enter(resources: fixture.Resources);
        ProjectObjectClipboardPayload payload;
        using (ClipboardCaptureScope scope = ClipboardCaptureScope.Enter())
        {
            var values = ClipboardCaptureScope.Capture(Enumerable.Range(0, 100_000)
                .Select(i => new LogicalNoteClipboardSnapshot(i, 1, i % 128, 100)), 100_000);
            payload = new(new object(), ProjectObjectClipboardKind.LogicalNotes, values.Count,
                "notes", new LogicalNoteClipboardData(values));
        }
        Assert.True(fixture.Resources.SpillBytes > 0);
        Assert.True(fixture.Resources.PeakResidentBytes <= 1024);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        IDisposable lease = payload.AcquireStorageLease();
        payload.Dispose();
        Assert.Equal(99_999, ((LogicalNoteClipboardData)payload.Data).Notes[99_999].StartOffset);
        lease.Dispose();
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Path));
    }

    [Fact]
    public void CancelDuringCaptureRemovesPartialPagesWithoutPublishingPayload()
    {
        using var fixture = new StorageFixture();
        using var cancellation = new CancellationTokenSource();
        using BulkEditPreparationContext context = BulkEditPreparationContext.Enter(cancellation.Token,
            resources: fixture.Resources);
        Assert.Throws<OperationCanceledException>(() =>
        {
            using ClipboardCaptureScope scope = ClipboardCaptureScope.Enter();
            ClipboardCaptureScope.Capture(Values());
        });
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Path));
        IEnumerable<long> Values()
        {
            for (int index = 0; index < 20_000; index++)
            {
                if (index == 10_000) cancellation.Cancel();
                yield return index;
            }
        }
    }

    [Fact]
    public void TransferCancellationAfterCopyDisposesDetachedPayloadAndLeavesHistoryUnchanged()
    {
        using var fixture = new StorageFixture();
        using var cancellation = new CancellationTokenSource();
        using BulkEditPreparationContext context = BulkEditPreparationContext.Enter(resources: fixture.Resources);
        var project = new MidoraProject(192);
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        long beforeId = project.NextStableId;
        Assert.Throws<OperationCanceledException>(() => ProjectObjectClipboard.PrepareTransfer(document, () =>
        {
            using ClipboardCaptureScope scope = ClipboardCaptureScope.Enter();
            var values = ClipboardCaptureScope.Capture(Enumerable.Range(0, 5000)
                .Select(i => new LogicalNoteClipboardSnapshot(i, 1, i % 128, 100)));
            ProjectObjectClipboardPayload payload = new(document.ClipboardSessionIdentity,
                ProjectObjectClipboardKind.LogicalNotes, values.Count, "notes", new LogicalNoteClipboardData(values));
            cancellation.Cancel();
            return payload;
        }, cancellationToken: cancellation.Token));
        Assert.Equal(beforeId, project.NextStableId);
        Assert.Empty(document.History);
        Assert.Equal(0, fixture.Resources.ResidentBytes);
        Assert.Equal(0, fixture.Resources.SpillBytes);
    }

    [Fact]
    public void HistoryPreparationCanBeCancelledAndCannotPublishAgainstChangedRevision()
    {
        var project = new MidoraProject(192);
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(0, "one"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => document.PrepareHistoryTransition(false, cancelled.Token));
        Assert.Single(project.Conductor.Markers);
        PreparedProjectHistoryTransition transition = document.PrepareHistoryTransition(false);
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(10, "two"));
        Assert.Throws<InvalidOperationException>(() => document.PublishHistoryTransition(transition));
        Assert.Equal(2, project.Conductor.Markers.Count);
        document.PublishHistoryTransition(document.PrepareHistoryTransition(false));
        Assert.Single(project.Conductor.Markers);
        document.PublishHistoryTransition(document.PrepareHistoryTransition(true));
        Assert.Equal(2, project.Conductor.Markers.Count);
    }

    [Fact]
    public void LargePastePublishesOneRootAndUndoRedoReuseExactRootsWithoutMutatingOldSnapshot()
    {
        var project = new MidoraProject(192);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        track.Segments.Add(segment);
        segment.Notes.AddRange(Enumerable.Range(0, 5000).Select(i => new LogicalNote(project)
            { StartTick = i * 2, LengthTicks = 1, Note = i % 128, Velocity = 100 }));
        LogicalNoteQuerySnapshot oldSnapshot = segment.Notes.CreateQuerySnapshot();
        using var compilation = new ProjectCompilationSession(project);
        using var document = new ProjectDocumentSession(compilation);
        using var payload = ProjectObjectClipboard.CopyLogicalNotes(document, segment.Id,
            oldSnapshot.EnumerateAll().Select(value => value.Id).ToArray());
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteLogicalNotesCommand(document, payload, segment.Id, 20_000);
        using StagedProjectEdit prepared = document.PrepareEdit(command);
        Assert.Same(segment, project.Tracks[0].Segments[0]);
        Assert.Equal(5000, segment.Notes.Count);
        document.ExecutePrepared(prepared);
        Segment published = project.Tracks[0].Segments[0];
        Assert.NotSame(segment, published);
        Assert.Equal(10_000, published.Notes.Count);
        Assert.Equal(5000, oldSnapshot.Count);
        Assert.Equal(9998, oldSnapshot.EnumerateAll().Max(value => value.StartTick));
        document.Undo();
        Assert.Same(segment, project.Tracks[0].Segments[0]);
        Assert.Equal(5000, segment.Notes.Count);
        document.Redo();
        Assert.Same(published, project.Tracks[0].Segments[0]);
        Assert.Equal(10_000, published.Notes.Count);
        Assert.Equal(5000, oldSnapshot.Count);
    }

    [Fact]
    public void ConductorClipboardSpillsMarkerTextAndPreservesStableEqualTickOrder()
    {
        using var fixture = new StorageFixture();
        using BulkEditPreparationContext context = BulkEditPreparationContext.Enter(resources: fixture.Resources);
        ProjectObjectClipboardPayload payload;
        using (ClipboardCaptureScope scope = ClipboardCaptureScope.Enter())
        {
            ConductorClipboardList values = ConductorClipboardList.Capture(Enumerable.Range(0, 4000)
                .Select(i => new ConductorEventClipboardSnapshot(ConductorClipboardEventKind.Marker,
                    1234, 0, 0, 0, false, "Marker " + i + " — 音楽")));
            payload = new(new object(), ProjectObjectClipboardKind.ConductorEvents, values.Count,
                "markers", new ConductorEventsClipboardData(values));
        }
        var rows = ((ConductorEventsClipboardData)payload.Data).Events;
        Assert.Equal("Marker 0 — 音楽", rows[0].Text);
        Assert.Equal("Marker 3999 — 音楽", rows[3999].Text);
        Assert.All(rows, value => Assert.Equal(0, value.TickOffset));
        Assert.True(fixture.Resources.PeakResidentBytes <= 1024);
        Assert.True(fixture.Resources.SpillBytes > 0);
        payload.Dispose();
        Assert.Equal(0, fixture.Resources.SpillBytes);
        Assert.Equal(0, fixture.Resources.WorkingBytes);
    }

    private sealed class StorageFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "midora-clipboard-pages-" + Guid.NewGuid().ToString("N"));
        public BoundedEditResources Resources { get; }
        public StorageFixture()
        {
            Directory.CreateDirectory(Path);
            Resources = new(new PagedEditResourceBudget(maximumResidentBytes: 1024,
                maximumWorkingBytes: 256 * 1024, pageRecordCount: 64), Path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
