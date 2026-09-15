using System.Diagnostics;
using Midora.Application;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class TimelineGenerationIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EarlyCompilationNoticeMustNotConsumePendingUndoSelection()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        var session = fixture.Session;
        var source = fixture.Source(WorkspaceTimelineSelectionKind.TemplateNote);
        var workspace = fixture.Open(WorkspaceTimelineSelectionKind.TemplateNote);
        Publish(session, workspace, Create(source, 2));
        var ids = fixture.ReadIds(WorkspaceTimelineSelectionKind.TemplateNote);
        workspace.Selection.Replace(ids[0], source);
        session.RefreshWorkspaceSelection(workspace);
        bool injectNotice = false;
        var notify = typeof(Midora.Playback.ProjectCompilationSession).GetMethod("NotifyCompilationChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var command = new ProjectPropertyEditCommand<string>("Rename for notification test", p => p.Metadata.ProjectName,
            (p, value) => { p.Metadata.ProjectName = value; if (injectNotice) notify.Invoke(session.Document!.Compilation, null); },
            "Changed", new() { AffectsEverything = true });
        session.ExecutePreservingWorkspaceSelection(command, workspace);
        workspace.Selection.Replace(ids[1], source);
        session.RefreshWorkspaceSelection(workspace);
        injectNotice = true;
        session.Undo();
        AssertSelection(workspace, [ids[0]], source);
    }
    [Theory]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiNote)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalNote)]
    [InlineData(WorkspaceTimelineSelectionKind.TemplateNote)]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiEventPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalParameterPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.SubVoiceEventPoint)]
    public async Task CreationPublishesExactTypedSelectionAndRestoresBothHistorySelections(
        WorkspaceTimelineSelectionKind kind)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        DesktopSessionController session = fixture.Session;
        WorkspaceTimelineSelectionSource source = fixture.Source(kind);
        WorkspaceViewModel workspace = fixture.Open(kind);
        Assert.True(MainWindow.IsTimelineGenerationSource(source));
        bool notes = MainWindow.IsTimelineNoteGenerationSource(source);
        Publish(session, workspace, Create(source, 1));
        MidoraId original = Assert.Single(fixture.ReadIds(kind));
        AssertSelection(workspace, [original], source);
        long originalAllocator = session.Project!.NextStableId;
        long originalState = session.Document!.CurrentStateId;
        List<TimelineEditPreparationProgress> reports = [];

        using var command = Create(source, 4);
        using StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace,
            progress: new ImmediateProgress(reports.Add));

        // Preparing includes detached source roots, history resources and the actual
        // compressed Workspace selection projection, not just a Core plan.
        Assert.Equal(originalAllocator, session.Project.NextStableId);
        Assert.Equal(originalState, session.Document.CurrentStateId);
        Assert.Equal([original], fixture.ReadIds(kind));
        AssertSelection(workspace, [original], source);
        Assert.NotNull(staged.PreparedSelection);
        MidoraId[] created = staged.PreparedSelection.ResultSelectionIds.ToArray();
        Assert.Equal(notes ? 3 : 4, created.Length);
        Assert.DoesNotContain(original, created);
        Assert.True(session.TryGetPreparedWorkspaceSelectionProjection(staged, out var projection));
        Assert.NotNull(projection);
        Assert.Contains(reports, value => value.Detail == $"Candidates: 4; retained: {created.Length}");

        Assert.True(session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace).Changed);
        Assert.Equal(4, fixture.ReadIds(kind).Length);
        AssertSelection(workspace, created, source);
        if (notes) Assert.Contains(original, fixture.ReadIds(kind));
        else Assert.DoesNotContain(original, fixture.ReadIds(kind));

        session.Undo();
        Assert.Equal([original], fixture.ReadIds(kind));
        AssertSelection(workspace, [original], source);
        session.Redo();
        Assert.Equal(4, fixture.ReadIds(kind).Length);
        AssertSelection(workspace, created, source);
    }

    [Theory]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiNote)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalNote)]
    [InlineData(WorkspaceTimelineSelectionKind.TemplateNote)]
    public async Task FullyCollidingCreationClearsSelectionWithoutProjectOrHistoryMutation(
        WorkspaceTimelineSelectionKind kind)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkspaceTimelineSelectionSource source = fixture.Source(kind);
        WorkspaceViewModel workspace = fixture.Open(kind);
        Publish(fixture.Session, workspace, Create(source, 1));
        MidoraId original = Assert.Single(fixture.ReadIds(kind));
        long state = fixture.Session.Document!.CurrentStateId;
        long allocator = fixture.Session.Project!.NextStableId;
        using var command = Create(source, 1);
        using StagedProjectEdit staged = fixture.Session.PrepareProjectEdit(command, workspace);

        Assert.NotNull(staged.PreparedSelection);
        Assert.Empty(staged.PreparedSelection.ResultSelectionIds);
        Assert.False(fixture.Session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace).Changed);
        Assert.Equal(state, fixture.Session.Document.CurrentStateId);
        Assert.Equal(allocator, fixture.Session.Project.NextStableId);
        Assert.Equal([original], fixture.ReadIds(kind));
        Assert.Empty(workspace.Selection.Ids);
        Assert.Null(workspace.Selection.HomogeneousTimelineSource);
        Assert.Null(workspace.Selection.HomogeneousTimelineQuantizeScope);
    }

    [Theory]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiNote)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalNote)]
    [InlineData(WorkspaceTimelineSelectionKind.TemplateNote)]
    [InlineData(WorkspaceTimelineSelectionKind.DirectMidiEventPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.LogicalParameterPoint)]
    [InlineData(WorkspaceTimelineSelectionKind.SubVoiceEventPoint)]
    public async Task CancelledCreationKeepsWorkspaceSelectionAndLiveSourceUntouched(
        WorkspaceTimelineSelectionKind kind)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        WorkspaceTimelineSelectionSource source = fixture.Source(kind);
        WorkspaceViewModel workspace = fixture.Open(kind);
        Publish(fixture.Session, workspace, Create(source, 1));
        MidoraId original = Assert.Single(fixture.ReadIds(kind));
        long state = fixture.Session.Document!.CurrentStateId;
        long allocator = fixture.Session.Project!.NextStableId;
        using var cancellation = new CancellationTokenSource();
        using var command = Create(source, 10_000);
        var progress = new ImmediateProgress(value =>
        {
            if (value.Phase == TimelineEditPreparationPhase.Planning && value.Completed >= 256)
                cancellation.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Session.PrepareProjectEdit(
            command, workspace, cancellation.Token, progress));
        Assert.Equal(state, fixture.Session.Document.CurrentStateId);
        Assert.Equal(allocator, fixture.Session.Project.NextStableId);
        Assert.Equal([original], fixture.ReadIds(kind));
        AssertSelection(workspace, [original], source);
    }

    [Fact]
    public async Task OptInLargeGenerationIncludesDesktopProjectionPublicationAndHistory()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MIDORA_GENERATOR_DESKTOP_BENCHMARK_COUNT"), out int count)) return;
        Assert.InRange(count, 1, TimelineGenerationLimits.MaximumCandidates);
        await using Fixture fixture = await Fixture.CreateAsync();
        var source = fixture.Source(WorkspaceTimelineSelectionKind.DirectMidiNote);
        WorkspaceViewModel workspace = fixture.Open(source.Kind);
        using var command = new TimelineCreationEditCommand(MainWindow.CreateTimelineGenerationCommand(source,
            new NoteGenerationOptions { MaximumCandidates = count, TickExpression = "=i*2", KeyExpression = "=i%128" }, null), source);
        Stopwatch timer = Stopwatch.StartNew();
        using StagedProjectEdit staged = fixture.Session.PrepareProjectEdit(command, workspace);
        double preparationSeconds = timer.Elapsed.TotalSeconds;
        Assert.NotNull(staged.PreparedSelection);
        Assert.Equal(count, staged.PreparedSelection.ResultSelectionIds.Count);
        Assert.True(fixture.Session.TryGetPreparedWorkspaceSelectionProjection(staged, out _));
        timer.Restart();
        fixture.Session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
        double publicationMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(count, workspace.Selection.Ids.Count);
        Assert.Equal(source, workspace.Selection.HomogeneousTimelineSource);
        timer.Restart(); fixture.Session.Undo(); double undoMs = timer.Elapsed.TotalMilliseconds;
        Assert.Empty(workspace.Selection.Ids);
        timer.Restart(); fixture.Session.Redo(); double redoMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(count, workspace.Selection.Ids.Count);
        using Process process = Process.GetCurrentProcess();
        output.WriteLine($"Desktop generation {count:N0}: prepare+projection={preparationSeconds:F3}s; " +
            $"publish={publicationMs:F2}ms; undo={undoMs:F2}ms; redo={redoMs:F2}ms; " +
            $"managed={GC.GetTotalMemory(false) / 1048576d:F1}MiB; workingSet={process.WorkingSet64 / 1048576d:F1}MiB; " +
            $"peakWorkingSet={process.PeakWorkingSet64 / 1048576d:F1}MiB");
        Assert.False(fixture.Session.IsPlaybackActive);
        Assert.False(fixture.Session.HasEnabledSoundFonts);
    }

    private static TimelineCreationEditCommand Create(WorkspaceTimelineSelectionSource source, int count) =>
        new(MainWindow.CreateTimelineGenerationCommand(source,
            new NoteGenerationOptions { MaximumCandidates = count, InitialKey = 60, InitialGate = 6,
                TickExpression = "=24+i*12", VelocityExpression = "=60+i" },
            new EventGenerationOptions { MaximumCandidates = count, TickExpression = "=24+i*12", ValueExpression = "=32+i" }), source);

    private static void Publish(DesktopSessionController session, WorkspaceViewModel workspace, TimelineCreationEditCommand command)
    {
        using (command)
        using (StagedProjectEdit staged = session.PrepareProjectEdit(command, workspace))
            session.ExecutePreparedPreservingWorkspaceSelection(staged, workspace);
    }

    private static void AssertSelection(WorkspaceViewModel workspace, IReadOnlyCollection<MidoraId> ids,
        WorkspaceTimelineSelectionSource source)
    {
        Assert.True(workspace.Selection.IdSet.SetEquals(ids),
            $"Expected selection [{string.Join(',', ids)}], actual [{string.Join(',', workspace.Selection.IdSet)}]; kind={workspace.Selection.HomogeneousTimelineSource?.Kind}");
        Assert.Equal(source, workspace.Selection.HomogeneousTimelineSource);
        Assert.Equal(source.QuantizeScope, workspace.Selection.HomogeneousTimelineQuantizeScope);
    }

    private sealed class ImmediateProgress(Action<TimelineEditPreparationProgress> callback) : IProgress<TimelineEditPreparationProgress>
    { public void Report(TimelineEditPreparationProgress value) => callback(value); }

    private sealed class Fixture(DesktopSessionController session, MidoraId instrumentId, MidoraId voiceId,
        MidoraId logicalSegmentId, MidoraId midiSegmentId, MidoraId laneId) : IAsyncDisposable
    {
        public DesktopSessionController Session { get; } = session;
        private Segment Logical => Session.Project!.Tracks.SelectMany(value => value.Segments).Single(value => value.Id == logicalSegmentId);
        private MidiSegment Midi => Session.Project!.PureMidiTracks.SelectMany(value => value.Segments).Single(value => value.Id == midiSegmentId);
        private SubVoice Voice => Session.Project!.EventInstruments.Single(value => value.Id == instrumentId)
            .SubVoices.Single(value => value.Id == voiceId);

        public WorkspaceTimelineSelectionSource Source(WorkspaceTimelineSelectionKind kind) => kind switch
        {
            WorkspaceTimelineSelectionKind.DirectMidiNote => new(kind, midiSegmentId),
            WorkspaceTimelineSelectionKind.LogicalNote => new(kind, logicalSegmentId),
            WorkspaceTimelineSelectionKind.TemplateNote => new(kind, instrumentId, voiceId),
            WorkspaceTimelineSelectionKind.DirectMidiEventPoint => new(kind, midiSegmentId,
                DirectMidiEventKind: DirectMidiChannelEventKind.ControlChange, DirectMidiData1: 11),
            WorkspaceTimelineSelectionKind.LogicalParameterPoint => new(kind, logicalSegmentId, laneId,
                PointMinimum: -64, PointMaximum: 64),
            WorkspaceTimelineSelectionKind.SubVoiceEventPoint => new(kind, instrumentId, voiceId,
                MidiValueTarget.ControlChange(11)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        public WorkspaceViewModel Open(WorkspaceTimelineSelectionKind kind) => kind switch
        {
            WorkspaceTimelineSelectionKind.TemplateNote or WorkspaceTimelineSelectionKind.SubVoiceEventPoint => Session.OpenInstrument(instrumentId),
            WorkspaceTimelineSelectionKind.LogicalNote or WorkspaceTimelineSelectionKind.LogicalParameterPoint => Session.OpenSegment(logicalSegmentId),
            _ => Session.OpenSegment(midiSegmentId)
        };

        public MidoraId[] ReadIds(WorkspaceTimelineSelectionKind kind) => kind switch
        {
            WorkspaceTimelineSelectionKind.DirectMidiNote => Midi.Notes.Select(value => value.Id).ToArray(),
            WorkspaceTimelineSelectionKind.LogicalNote => Logical.Notes.Select(value => value.Id).ToArray(),
            WorkspaceTimelineSelectionKind.TemplateNote => Voice.Events.Where(value => value.Kind == TemplateEventKind.Note).Select(value => value.Id).ToArray(),
            WorkspaceTimelineSelectionKind.DirectMidiEventPoint => Midi.ChannelEvents.Select(value => value.Id).ToArray(),
            WorkspaceTimelineSelectionKind.LogicalParameterPoint => Logical.ParameterLanes.Single(value => value.Id == laneId).Points.Select(value => value.Id).ToArray(),
            _ => Voice.Events.Where(value => value.Kind != TemplateEventKind.Note).Select(value => value.Id).ToArray()
        };

        public ValueTask DisposeAsync() => Session.DisposeAsync();

        public static async Task<Fixture> CreateAsync()
        {
            DesktopSessionController session = new();
            await session.CreateProjectAsync(new NewProjectCreationRequest
            { ProjectName = "Generation integration", PersistenceMode = NewProjectPersistenceMode.CreateUnsaved });
            Assert.False(session.HasEnabledSoundFonts);
            session.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));
            MidoraId instrumentId = session.Project!.EventInstruments.Single().Id;
            MidoraId voiceId = session.Project.EventInstruments.Single().SubVoices.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Logical", instrumentId));
            MidoraId logicalTrackId = session.Project.Tracks.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreateSegment(logicalTrackId, 0, 480));
            MidoraId logicalId = session.Project.Tracks.Single().Segments.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreateLogicalParameter(instrumentId,
                "Signed", LogicalParameterType.Integer, -64, 64, -64, 64, 0));
            MidoraId parameterId = session.Project.EventInstruments.Single().LogicalParameters.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(logicalId, parameterId));
            MidoraId laneId = session.Project.Tracks.Single().Segments.Single().ParameterLanes.Single().Id;
            session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("MIDI"));
            session.Execute(ProjectDomainEditCommands.CreateMidiSegment(session.Project.PureMidiTracks.Single().Id, 0, 480));
            return new(session, instrumentId, voiceId, logicalId,
                session.Project.PureMidiTracks.Single().Segments.Single().Id, laneId);
        }
    }
}
