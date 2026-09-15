using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Persistence;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectAdvancedTimelineEditIntegrationTests
{
    [Fact]
    public void HumanizeAutoSeedSelectionUndoAndRedoRestoreTheFrozenResult()
    {
        using Fixture fixture = CreateFixture();
        LogicalNote first = AddNote(fixture.Project, fixture.Segment, 10, 5, 60, 80);
        LogicalNote second = AddNote(fixture.Project, fixture.Segment, 20, 5, 60, 90);
        fixture.StartSession();
        MidoraId[] originalSelection = [first.Id, second.Id];
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.HumanizeLogicalNotes(
                fixture.Segment.Id,
                originalSelection,
                new(
                    TimelineHumanizeField.Override(100, 100),
                    TimelineHumanizeField.Disabled,
                    TimelineHumanizeField.Add(-10, 10)));

        Segment oldRoot = fixture.Segment;
        IReadOnlyList<MidoraId> frozenOriginalSelection;
        using (StagedProjectEdit staged = fixture.Document.PrepareEdit(command))
        {
            frozenOriginalSelection = command.ResultSelectionIds;
            Assert.Same(oldRoot, fixture.Segment);
            Assert.Equal((10L, 5L, 80),
                (first.StartTick, first.LengthTicks, first.Velocity));
            Assert.True(fixture.Document.ExecutePrepared(staged).Changed);
        }

        Segment replacementRoot = fixture.Segment;
        Assert.NotSame(oldRoot, replacementRoot);
        LogicalNote applied = Assert.Single(fixture.Segment.Notes);
        NoteSnapshot frozen = Snapshot(applied);
        IReadOnlyList<MidoraId> frozenResultSelection = command.ResultSelectionIds;
        Assert.Equal(first.Id, applied.Id);
        Assert.Equal([first.Id], command.ResultSelectionIds);
        AssertSingleUndoAndFullCompileParity(fixture);

        fixture.Document.Undo();

        Assert.Same(oldRoot, fixture.Segment);
        Assert.Same(frozenOriginalSelection, command.ResultSelectionIds);
        Assert.Equal(originalSelection, command.ResultSelectionIds);
        Assert.Equal(
            [new(first.Id, 10, 5, 60, 80), new(second.Id, 20, 5, 60, 90)],
            Snapshot(fixture.Segment));
        Assert.False(fixture.Document.CanUndo);
        Assert.True(fixture.Document.CanRedo);
        AssertFullCompileParity(fixture.Compilation);

        fixture.Document.Redo();

        Assert.Same(replacementRoot, fixture.Segment);
        Assert.Same(frozenResultSelection, command.ResultSelectionIds);
        Assert.Equal([first.Id], command.ResultSelectionIds);
        Assert.Equal(frozen, Snapshot(Assert.Single(fixture.Segment.Notes)));
        Assert.Single(fixture.Document.History);
        AssertFullCompileParity(fixture.Compilation);
    }

    [Fact]
    public void InvalidHumanizeRangesFailBeforeCommitAndLeaveTheDocumentUnchanged()
    {
        using Fixture fixture = CreateFixture();
        LogicalNote note = AddNote(fixture.Project, fixture.Segment, 10, 5, 60, 80);
        fixture.StartSession();
        NoteSnapshot original = Snapshot(note);
        TimelineHumanizeField[] invalidFields =
        [
            TimelineHumanizeField.Add(1, 0),
            TimelineHumanizeField.Override(1, 0),
            TimelineHumanizeField.Multiply(double.NaN, 1),
            TimelineHumanizeField.Multiply(0, double.PositiveInfinity),
            TimelineHumanizeField.Multiply(2, 1),
            TimelineHumanizeField.Multiply(-0.1, 1)
        ];

        foreach (TimelineHumanizeField invalidField in invalidFields)
        {
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.HumanizeLogicalNotes(
                    fixture.Segment.Id,
                    [note.Id],
                    new(
                        TimelineHumanizeField.Disabled,
                        invalidField,
                        TimelineHumanizeField.Disabled,
                        Seed: 1));

            Assert.ThrowsAny<ArgumentException>(() => fixture.Document.PrepareEdit(command));
            Assert.Equal(original, Snapshot(note));
            Assert.Empty(fixture.Document.History);
            Assert.False(fixture.Document.CanUndo);
            Assert.False(fixture.Document.CanRedo);
            Assert.False(fixture.Document.IsModified);
            AssertFullCompileParity(fixture.Compilation);
        }
    }

    [Fact]
    public void SplitSelectionUndoAndRedoRestoreObjectsAndFrozenFragmentIds()
    {
        using Fixture fixture = CreateFixture();
        LogicalNote source = AddNote(fixture.Project, fixture.Segment, 25, 12, 61, 91);
        fixture.StartSession();
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.SplitLogicalNotes(
                fixture.Segment.Id,
                [source.Id],
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 5
                });

        Segment oldRoot = fixture.Segment;
        long nextStableId = fixture.Project.NextStableId;
        using (StagedProjectEdit staged = fixture.Document.PrepareEdit(command))
        {
            Assert.Same(oldRoot, fixture.Segment);
            Assert.Equal(nextStableId, fixture.Project.NextStableId);
            Assert.Equal((25L, 12L), (source.StartTick, source.LengthTicks));
            Assert.True(fixture.Document.ExecutePrepared(staged).Changed);
        }

        Segment replacementRoot = fixture.Segment;
        Assert.NotSame(oldRoot, replacementRoot);
        long publishedHighWater = fixture.Project.NextStableId;
        Assert.Equal(nextStableId + 2, publishedHighWater);
        NoteSnapshot[] frozen = Snapshot(fixture.Segment);
        MidoraId[] frozenSelection = command.ResultSelectionIds.ToArray();
        Assert.Equal(3, frozen.Length);
        Assert.Equal([5L, 5L, 2L], frozen.Select(static value => value.Gate).ToArray());
        Assert.Equal(source.Id, frozen[0].Id);
        Assert.Equal(frozen.Select(static value => value.Id), frozenSelection);
        AssertSingleUndoAndFullCompileParity(fixture);

        fixture.Document.Undo();

        Assert.Same(oldRoot, fixture.Segment);
        Assert.Equal(publishedHighWater, fixture.Project.NextStableId);
        Assert.Equal([source.Id], command.ResultSelectionIds);
        Assert.Equal([new NoteSnapshot(source.Id, 25, 12, 61, 91)], Snapshot(fixture.Segment));
        Assert.False(fixture.Document.CanUndo);
        AssertFullCompileParity(fixture.Compilation);

        fixture.Document.Redo();

        Assert.Same(replacementRoot, fixture.Segment);
        Assert.Equal(publishedHighWater, fixture.Project.NextStableId);
        Assert.Equal(frozenSelection, command.ResultSelectionIds);
        Assert.Equal(frozen, Snapshot(fixture.Segment));
        Assert.Single(fixture.Document.History);
        AssertFullCompileParity(fixture.Compilation);
    }

    [Fact]
    public void JoinSelectionUndoAndRedoRestoreTheOriginalRunAndSingleHistoryEntry()
    {
        using Fixture fixture = CreateFixture();
        LogicalNote first = AddNote(fixture.Project, fixture.Segment, 40, 5, 62, 100);
        LogicalNote second = AddNote(fixture.Project, fixture.Segment, 45, 7, 62, 70);
        fixture.StartSession();
        MidoraId[] originalSelection = [first.Id, second.Id];
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.JoinLogicalNotes(
                fixture.Segment.Id,
                originalSelection,
                new());

        Segment oldRoot = fixture.Segment;
        using (StagedProjectEdit staged = fixture.Document.PrepareEdit(command))
        {
            Assert.Same(oldRoot, fixture.Segment);
            Assert.Equal((5L, 7L), (first.LengthTicks, second.LengthTicks));
            Assert.True(fixture.Document.ExecutePrepared(staged).Changed);
        }

        Segment replacementRoot = fixture.Segment;
        Assert.NotSame(oldRoot, replacementRoot);
        Assert.Equal([new NoteSnapshot(first.Id, 40, 12, 62, 100)], Snapshot(fixture.Segment));
        Assert.Equal([first.Id], command.ResultSelectionIds);
        AssertSingleUndoAndFullCompileParity(fixture);

        fixture.Document.Undo();

        Assert.Same(oldRoot, fixture.Segment);
        Assert.Equal(originalSelection, command.ResultSelectionIds);
        Assert.Equal(
            [new(first.Id, 40, 5, 62, 100), new(second.Id, 45, 7, 62, 70)],
            Snapshot(fixture.Segment));
        Assert.False(fixture.Document.CanUndo);
        AssertFullCompileParity(fixture.Compilation);

        fixture.Document.Redo();

        Assert.Same(replacementRoot, fixture.Segment);
        Assert.Equal([new NoteSnapshot(first.Id, 40, 12, 62, 100)], Snapshot(fixture.Segment));
        Assert.Equal([first.Id], command.ResultSelectionIds);
        Assert.Single(fixture.Document.History);
        AssertFullCompileParity(fixture.Compilation);
    }

    [Fact]
    public void QuantizeSelectionUndoAndRedoRestoreCollisionLoserAndFrozenWinner()
    {
        using Fixture fixture = CreateFixture();
        LogicalNote first = AddNote(fixture.Project, fixture.Segment, 51, 4, 63, 100);
        LogicalNote second = AddNote(fixture.Project, fixture.Segment, 54, 6, 63, 70);
        fixture.StartSession();
        MidoraId[] originalSelection = [first.Id, second.Id];
        ITimelineSelectionResultEditCommand command =
            ProjectDomainEditCommands.QuantizeLogicalNotes(
                fixture.Segment.Id,
                originalSelection,
                new(TimelineQuantizeGrid.FromCustomTicks(10)));

        Segment oldRoot = fixture.Segment;
        using (StagedProjectEdit staged = fixture.Document.PrepareEdit(command))
        {
            Assert.Same(oldRoot, fixture.Segment);
            Assert.Equal((51L, 54L), (first.StartTick, second.StartTick));
            Assert.True(fixture.Document.ExecutePrepared(staged).Changed);
        }

        Segment replacementRoot = fixture.Segment;
        Assert.NotSame(oldRoot, replacementRoot);
        Assert.Equal([new NoteSnapshot(first.Id, 50, 4, 63, 100)], Snapshot(fixture.Segment));
        Assert.Equal([first.Id], command.ResultSelectionIds);
        AssertSingleUndoAndFullCompileParity(fixture);

        fixture.Document.Undo();

        Assert.Same(oldRoot, fixture.Segment);
        Assert.Equal(originalSelection, command.ResultSelectionIds);
        Assert.Equal(
            [new(first.Id, 51, 4, 63, 100), new(second.Id, 54, 6, 63, 70)],
            Snapshot(fixture.Segment));
        Assert.False(fixture.Document.CanUndo);
        AssertFullCompileParity(fixture.Compilation);

        fixture.Document.Redo();

        Assert.Same(replacementRoot, fixture.Segment);
        Assert.Equal([new NoteSnapshot(first.Id, 50, 4, 63, 100)], Snapshot(fixture.Segment));
        Assert.Equal([first.Id], command.ResultSelectionIds);
        Assert.Single(fixture.Document.History);
        AssertFullCompileParity(fixture.Compilation);
    }

    [Fact]
    public async Task AdvancedEditResultsRoundTripThroughFormat3WithoutChangingCanonicalMusic()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MidoraTests",
            $"stage4-integration-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "advanced-edits.midora");
        Directory.CreateDirectory(directory);
        try
        {
            using Fixture fixture = CreateFixture();
            LogicalNote humanized = AddNote(fixture.Project, fixture.Segment, 101, 8, 60, 80);
            LogicalNote split = AddNote(fixture.Project, fixture.Segment, 200, 12, 61, 90);
            LogicalNote joinFirst = AddNote(fixture.Project, fixture.Segment, 300, 5, 62, 100);
            LogicalNote joinSecond = AddNote(fixture.Project, fixture.Segment, 305, 5, 62, 70);
            LogicalNote quantized = AddNote(fixture.Project, fixture.Segment, 411, 9, 63, 95);
            fixture.StartSession();

            ExecuteStaged(fixture.Document, ProjectDomainEditCommands.HumanizeLogicalNotes(
                fixture.Segment.Id,
                [humanized.Id],
                new(
                    TimelineHumanizeField.Add(5, 5),
                    TimelineHumanizeField.Add(2, 2),
                    TimelineHumanizeField.Override(99, 99),
                    Seed: 123)));
            ExecuteStaged(fixture.Document, ProjectDomainEditCommands.SplitLogicalNotes(
                fixture.Segment.Id,
                [split.Id],
                new()
                {
                    Mode = NoteSplitMode.FixedPieceLength,
                    FixedPieceLengthTicks = 5
                }));
            ExecuteStaged(fixture.Document, ProjectDomainEditCommands.JoinLogicalNotes(
                fixture.Segment.Id,
                [joinFirst.Id, joinSecond.Id],
                new()));
            ExecuteStaged(fixture.Document, ProjectDomainEditCommands.QuantizeLogicalNotes(
                fixture.Segment.Id,
                [quantized.Id],
                new(TimelineQuantizeGrid.FromCustomTicks(10))));

            NoteSnapshot[] expectedNotes = Snapshot(fixture.Segment);
            Assert.Equal(4, fixture.Document.History.Count);
            AssertFullCompileParity(fixture.Compilation);
            CanonicalCompiledResult expectedCanonical = fixture.Compilation.LastAttempt;
            _ = await new MidoraProjectPackageV1("1.0.0-dev")
                .SaveProjectAsync(fixture.Project, path);

            await using MidoraProjectOpenResultV1 opened =
                await new MidoraProjectPackageV1("1.0.0-dev").OpenAsync(path);
            Assert.Equal(4, opened.SourceFileFormatVersion);
            Segment reopenedSegment = Assert.Single(Assert.Single(opened.Project.Tracks).Segments);
            Assert.Equal(expectedNotes, Snapshot(reopenedSegment));
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult reopenedCanonical = compiler.CompileFull(opened.Project);
            AssertCanonicalEqual(expectedCanonical, reopenedCanonical);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DirectSplitEndpointOrderSurvivesFormat3AndMaterializedPagedCanonicalProjection()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "MidoraTests",
            $"stage4-direct-split-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "direct-split.midora");
        Directory.CreateDirectory(directory);
        try
        {
            using MidoraProject project = new(480);
            MidiChannelRoot root = new(project) { Name = "Root" };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            MidiSegment segment = new(project) { LengthTicks = 100 };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            DirectMidiNote source = new(project)
            {
                StartTick = 0,
                LengthTicks = 10,
                Key = 60,
                NoteOnVelocity = 90,
                NoteOffVelocity = 37,
                NoteOnOrder = 11,
                NoteOffOrder = 19
            };
            segment.Notes.Add(source);
            segment.OpaqueEvents.Add(new(project)
            {
                Tick = 5,
                Kind = OpaqueMidiEventKind.Meta,
                MetaType = 0x01,
                Payload = [0x41],
                Order = 40
            });
            ITimelineSelectionResultEditCommand command =
                ProjectDomainEditCommands.SplitDirectMidiNotes(
                    segment.Id,
                    [source.Id],
                    new()
                    {
                        Mode = NoteSplitMode.FixedPieceLength,
                        FixedPieceLengthTicks = 5
                    });
            using ProjectCompilationSession compilation = new(project);
            using ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
            ExecuteStaged(document, command);

            DirectMidiNote[] expectedNotes = Active(project, segment).Notes
                .OrderBy(static value => value.StartTick).ToArray();
            Assert.Equal([(11L, 41L), (42L, 19L)], expectedNotes.Select(static value =>
                (value.NoteOnOrder, value.NoteOffOrder)));
            AssertFullCompileParity(compilation);
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult expectedCanonical = compilation.LastAttempt;
            Assert.True(expectedCanonical.IsConsumable);
            CanonicalSmfTrackChannelEvent[] cutEvents = expectedCanonical
                .QuerySmfTrackChannelEventPages(track.Id)
                .SelectMany(static page => page.Items)
                .Where(static value => value.Tick == 5)
                .ToArray();
            Assert.Equal([41L, 42L], cutEvents.Select(static value => value.EventOrder));
            Assert.Equal(
                [MidiMessageType.NoteOff, MidiMessageType.NoteOn],
                cutEvents.Select(static value => value.Message.MessageType));

            MidoraId[] frozenIds = expectedNotes.Select(static value => value.Id).ToArray();
            document.Undo();
            DirectMidiNote restored = Assert.Single(Active(project, segment).Notes);
            Assert.Equal((source.Id, 11L, 19L), (
                restored.Id,
                restored.NoteOnOrder,
                restored.NoteOffOrder));
            AssertFullCompileParity(compilation);
            document.Redo();
            Assert.Equal(
                frozenIds,
                Active(project, segment).Notes.OrderBy(static value => value.StartTick)
                    .Select(static value => value.Id));
            AssertFullCompileParity(compilation);

            _ = await new MidoraProjectPackageV1("1.0.0-dev")
                .SaveProjectAsync(project, path);
            await using MidoraProjectOpenResultV1 opened =
                await new MidoraProjectPackageV1("1.0.0-dev").OpenAsync(path);
            Assert.Equal(4, opened.SourceFileFormatVersion);
            PureMidiTrack reopenedTrack = Assert.Single(opened.Project.PureMidiTracks);
            MidiSegment reopenedSegment = Assert.Single(reopenedTrack.Segments);
            Assert.Equal(
                [(11L, 41L), (42L, 19L)],
                reopenedSegment.Notes.QueryValues(0, 100)
                    .OrderBy(static value => value.StartTick)
                    .Select(static value => (value.NoteOnOrder, value.NoteOffOrder)));
            CanonicalCompiledResult reopenedCanonical = compiler.CompileFull(opened.Project);
            Assert.True(reopenedCanonical.IsConsumable);
            Assert.Equal(
                DirectCanonicalEvents(expectedCanonical),
                DirectCanonicalEvents(reopenedCanonical));
            Assert.Equal(
                expectedCanonical.QuerySmfTrackChannelEventPages(track.Id)
                    .SelectMany(static page => page.Items),
                reopenedCanonical.QuerySmfTrackChannelEventPages(reopenedTrack.Id)
                    .SelectMany(static page => page.Items));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        static CanonicalMidiEvent[] DirectCanonicalEvents(CanonicalCompiledResult result) =>
            result.QueryEventPages(result.StartTick, result.EndTick)
                .SelectMany(static page => page.Items)
                .Where(static value => value.Role == CanonicalEventRole.DirectMidi
                    && value.Source.DirectMidiObjectId != default)
                .ToArray();
    }

    private static void ExecuteStaged(
        ProjectDocumentSession document,
        ITimelineSelectionResultEditCommand command)
    {
        using StagedProjectEdit staged = document.PrepareEdit(command);
        ProjectEditExecution result = document.ExecutePrepared(staged);
        Assert.True(result.Changed);
    }

    private static void AssertSingleUndoAndFullCompileParity(Fixture fixture)
    {
        Assert.Single(fixture.Document.History);
        Assert.True(fixture.Document.CanUndo);
        Assert.False(fixture.Document.CanRedo);
        AssertFullCompileParity(fixture.Compilation);
    }

    private static void AssertFullCompileParity(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        AssertCanonicalEqual(compiler.CompileFull(compilation.Project), compilation.LastAttempt);
    }

    private static void AssertCanonicalEqual(
        CanonicalCompiledResult expected,
        CanonicalCompiledResult actual)
    {
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }

    private static Fixture CreateFixture()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 480,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project);
        voice.Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 2_000 };
        track.Segments.Add(segment);
        return new(project, segment);
    }

    private static LogicalNote AddNote(
        MidoraProject project,
        Segment segment,
        long tick,
        long gate,
        int key,
        int velocity)
    {
        LogicalNote note = new(project)
        {
            StartTick = tick,
            LengthTicks = gate,
            Note = key,
            Velocity = velocity
        };
        segment.Notes.Add(note);
        return note;
    }

    private static NoteSnapshot[] Snapshot(Segment segment) => segment.Notes
        .OrderBy(static value => value.StartTick)
        .ThenBy(static value => value.Note)
        .ThenBy(static value => value.Id.Value)
        .Select(Snapshot)
        .ToArray();

    private static NoteSnapshot Snapshot(LogicalNote note) =>
        new(note.Id, note.StartTick, note.LengthTicks, note.Note, note.Velocity);

    private static MidiSegment Active(MidoraProject project, MidiSegment identity) =>
        project.PureMidiTracks.SelectMany(static value => value.Segments)
            .Single(value => value.Id == identity.Id);

    private readonly record struct NoteSnapshot(
        MidoraId Id,
        long Tick,
        long Gate,
        int Key,
        int Velocity);

    private sealed class Fixture(
        MidoraProject project,
        Segment segment) : IDisposable
    {
        private readonly MidoraId _segmentId = segment.Id;
        private ProjectCompilationSession? _compilation;
        private ProjectDocumentSession? _document;

        public MidoraProject Project { get; } = project;
        public Segment Segment => Project.Tracks
            .SelectMany(static value => value.Segments)
            .Single(value => value.Id == _segmentId);
        public ProjectCompilationSession Compilation => _compilation
            ?? throw new InvalidOperationException("The test session has not been started.");
        public ProjectDocumentSession Document => _document
            ?? throw new InvalidOperationException("The test session has not been started.");

        public void StartSession()
        {
            if (_compilation is not null)
                throw new InvalidOperationException("The test session was already started.");
            _compilation = new(Project);
            _document = new(_compilation, ProjectDocumentOrigin.Persisted);
        }

        public void Dispose()
        {
            _document?.Dispose();
            _compilation?.Dispose();
            Project.Dispose();
        }
    }
}
