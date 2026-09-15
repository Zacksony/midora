using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ExactTimelineCollisionPolicyTests
{
    [Fact]
    public void MovingLogicalNoteOntoExistingExactKeyDiscardsMoverAndUndoRestoresIt()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote incumbent = new(project)
        {
            StartTick = 100,
            LengthTicks = 40,
            Note = 64,
            Velocity = 90
        };
        LogicalNote mover = new(project)
        {
            StartTick = 20,
            LengthTicks = 80,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([incumbent, mover]);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            segment.Id,
            [mover.Id],
            tickDelta: 80,
            pitchDelta: 0));

        Assert.Same(incumbent, Assert.Single(segment.Notes));
        document.Undo();
        Assert.Equal([incumbent, mover], segment.Notes.ToArray());
        Assert.Equal(20, mover.StartTick);
        document.Redo();
        Assert.Same(incumbent, Assert.Single(segment.Notes));
    }

    [Fact]
    public void LogicalParameterCreationAndMoveReplaceExistingPointAtExactTick()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        instrument.LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint incumbent = new(project, 100, 0.25);
        CurvePoint mover = new(project, 40, 0.75);
        lane.Points.AddRange([incumbent, mover]);
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            segment.Id,
            lane.Id,
            100,
            0.5,
            CurveInterpolation.Step));
        CurvePoint created = Assert.Single(lane.Points, point => point.Id != mover.Id);
        Assert.Equal((100L, 0.5), (created.Tick, created.Value));
        Assert.DoesNotContain(incumbent, lane.Points);

        document.Execute(ProjectDomainEditCommands.MoveLogicalParameterPoints(
            segment.Id,
            lane.Id,
            [mover.Id],
            tickDelta: 60));
        CurvePoint moved = Assert.Single(lane.Points);
        Assert.Equal(mover.Id, moved.Id);
        Assert.Equal((100L, 0.75), (moved.Tick, moved.Value));

        document.Undo();
        Assert.Contains(created, lane.Points);
        Assert.Contains(mover, lane.Points);
        Assert.Equal(40, mover.Tick);
        document.Undo();
        Assert.Equal([incumbent, mover], lane.Points);
        document.Redo();
        Assert.Contains(created, lane.Points);
        Assert.DoesNotContain(incumbent, lane.Points);
    }

    [Fact]
    public void TemplateNoteCreationAndMoveKeepExistingExactNote()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent incumbent = TemplateEvent.Note(project, 100, 40, 64, 90);
        TemplateEvent mover = TemplateEvent.Note(project, 20, 80, 64, 100);
        voice.Events.AddRange([incumbent, mover]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            voice.Id,
            100,
            20,
            64,
            80));
        Assert.Equal([incumbent, mover], voice.Events);

        document.Execute(ProjectDomainEditCommands.UpdateTemplateNote(
            instrument.Id,
            voice.Id,
            mover.Id,
            100,
            mover.LengthTicks,
            64,
            mover.Value,
            mover.FollowPitchDelta));
        Assert.Same(incumbent, Assert.Single(voice.Events));

        document.Undo();
        Assert.Equal([incumbent, mover], voice.Events);
        Assert.Equal(20, mover.Tick);
        document.Redo();
        Assert.Same(incumbent, Assert.Single(voice.Events));
    }

    [Fact]
    public void ScopedCollisionResolutionDoesNotCleanUnrelatedPreexistingCollisions()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment unrelated = new(project)
        {
            ProjectStartTick = 0,
            LengthTicks = 240
        };
        LogicalNote unrelatedFirst = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 90
        };
        LogicalNote unrelatedSecond = new(project)
        {
            StartTick = 10,
            LengthTicks = 40,
            Note = 60,
            Velocity = 100
        };
        unrelated.Notes.AddRange([unrelatedFirst, unrelatedSecond]);

        Segment edited = new(project)
        {
            ProjectStartTick = 240,
            LengthTicks = 240
        };
        LogicalNote incumbent = new(project)
        {
            StartTick = 100,
            LengthTicks = 20,
            Note = 64,
            Velocity = 90
        };
        LogicalNote mover = new(project)
        {
            StartTick = 20,
            LengthTicks = 20,
            Note = 64,
            Velocity = 100
        };
        edited.Notes.AddRange([incumbent, mover]);
        track.Segments.AddRange([unrelated, edited]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveLogicalNotes(
            edited.Id,
            [mover.Id],
            tickDelta: 80,
            pitchDelta: 0));

        Assert.Equal([unrelatedFirst, unrelatedSecond], unrelated.Notes);
        Assert.Same(incumbent, Assert.Single(edited.Notes));
        document.Undo();
        Assert.Equal([unrelatedFirst, unrelatedSecond], unrelated.Notes);
        Assert.Equal([incumbent, mover], edited.Notes);
    }

    [Fact]
    public void BankComponentsUseIndependentExactTargetsAndLaterSameTargetReplacesIncumbent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.CreateTemplateBank(
            instrument.Id,
            voice.Id,
            tick: 100,
            bankMsb: 12,
            bankLsb: null));
        document.Execute(ProjectDomainEditCommands.CreateTemplateBank(
            instrument.Id,
            voice.Id,
            tick: 100,
            bankMsb: null,
            bankLsb: 34));
        TemplateEvent retainedLsb = Assert.Single(voice.Events, value => value.HasBankLsb);
        TemplateEvent replacedMsb = Assert.Single(voice.Events, value => value.HasBankMsb);

        document.Execute(ProjectDomainEditCommands.CreateTemplateBank(
            instrument.Id,
            voice.Id,
            tick: 100,
            bankMsb: 56,
            bankLsb: null));

        TemplateEvent replacementMsb = Assert.Single(voice.Events, value => value.HasBankMsb);
        Assert.NotSame(replacedMsb, replacementMsb);
        Assert.Equal(56, replacementMsb.Value);
        Assert.Contains(retainedLsb, voice.Events);
        Assert.DoesNotContain(replacedMsb, voice.Events);
        document.Undo();
        Assert.Contains(replacedMsb, voice.Events);
        Assert.Contains(retainedLsb, voice.Events);
        Assert.DoesNotContain(replacementMsb, voice.Events);
        document.Redo();
        Assert.Contains(replacementMsb, voice.Events);
        Assert.DoesNotContain(replacedMsb, voice.Events);
    }

    [Fact]
    public void MovingTemplateEventPointOntoExistingTargetKeepsMoverAndUndoRestoresBoth()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent incumbent = TemplateEvent.ControlChange(project, 100, 11, 32);
        TemplateEvent mover = TemplateEvent.ControlChange(project, 40, 11, 96);
        voice.Events.AddRange([incumbent, mover]);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
            instrument.Id,
            voice.Id,
            [mover.Id],
            MidiValueTarget.ControlChange(11),
            tickDelta: 60,
            valueDelta: 0,
            duplicate: false));

        Assert.Same(mover, Assert.Single(voice.Events));
        Assert.Equal(100, mover.Tick);
        document.Undo();
        Assert.Equal([incumbent, mover], voice.Events);
        Assert.Equal(40, mover.Tick);
        document.Redo();
        Assert.Same(mover, Assert.Single(voice.Events));
    }

    [Fact]
    public void WholeSubVoiceDuplicateDropsLaterExactCollisionsInTheNewOwner()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument", TemplateLengthTicks = 480 };
        SubVoice source = new(project) { Name = "Voice" };
        TemplateEvent first = TemplateEvent.Note(project, 20, 40, 60, 80);
        TemplateEvent later = TemplateEvent.Note(project, 20, 90, 60, 120);
        source.Events.AddRange([first, later]);
        instrument.SubVoices.Add(source);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.DuplicateSubVoice(
            instrument.Id,
            source.Id));

        SubVoice copy = instrument.SubVoices[1];
        TemplateEvent copied = Assert.Single(copy.Events);
        Assert.Equal((40L, 80), (copied.LengthTicks, copied.Value));
        Assert.Equal([first, later], source.Events);
    }

    [Fact]
    public void UnrelatedDirectMidiEditDoesNotCleanImportedExactDuplicates()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        DirectMidiNote firstNote = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 80
        };
        DirectMidiNote secondNote = new(project)
        {
            StartTick = 10,
            LengthTicks = 40,
            Key = 60,
            NoteOnVelocity = 90
        };
        DirectMidiNote editedNote = new(project)
        {
            StartTick = 30,
            LengthTicks = 20,
            Key = 64,
            NoteOnVelocity = 70
        };
        DirectMidiChannelEvent firstEvent = new(project)
        {
            Tick = 15,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 40
        };
        DirectMidiChannelEvent secondEvent = new(project)
        {
            Tick = 15,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 80
        };
        segment.Notes.AddRange([firstNote, secondNote, editedNote]);
        segment.ChannelEvents.AddRange([firstEvent, secondEvent]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.SetDirectMidiNoteValues(
            segment.Id,
            [editedNote.Id],
            noteOnVelocity: 100));

        Assert.Equal([firstNote, secondNote, editedNote], segment.Notes);
        Assert.Equal([firstEvent, secondEvent], segment.ChannelEvents);
        Assert.Equal(100, editedNote.NoteOnVelocity);
        document.Undo();
        Assert.Equal([firstNote, secondNote, editedNote], segment.Notes);
        Assert.Equal([firstEvent, secondEvent], segment.ChannelEvents);
        Assert.Equal(70, editedNote.NoteOnVelocity);
    }

    [Fact]
    public void DirectMidiEventMoveReplacesIncumbentAndUndoRestoresBoth()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        DirectMidiChannelEvent mover = new(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 96
        };
        DirectMidiChannelEvent incumbent = new(project)
        {
            Tick = 20,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 7,
            Data2 = 32
        };
        segment.ChannelEvents.AddRange([mover, incumbent]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.AdjustDirectMidiEventPoints(
            segment.Id,
            [mover.Id],
            tickDelta: 10,
            data1Delta: 0,
            data2Delta: 0,
            duplicate: false));

        Assert.Same(mover, Assert.Single(segment.ChannelEvents));
        Assert.Equal(20, mover.Tick);
        document.Undo();
        Assert.Equal([mover, incumbent], segment.ChannelEvents);
        Assert.Equal(10, mover.Tick);
        document.Redo();
        Assert.Same(mover, Assert.Single(segment.ChannelEvents));
    }

    [Fact]
    public void DirectMidiEventLineUpsertCreatesPointsInOneBatchAndSupportsUndoRedo()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpsertDirectMidiEventPoints(
            segment.Id,
            DirectMidiChannelEventKind.ControlChange,
            laneData1: 11,
            [new(10, 11, 40), new(20, 11, 80)]));

        DirectMidiChannelEvent[] created = segment.ChannelEvents
            .OrderBy(value => value.Tick)
            .ToArray();
        Assert.Equal(2, created.Length);
        Assert.Equal((10L, 11, 40), (created[0].Tick, created[0].Data1, created[0].Data2));
        Assert.Equal((20L, 11, 80), (created[1].Tick, created[1].Data1, created[1].Data2));

        document.Undo();
        Assert.Empty(segment.ChannelEvents);

        document.Redo();
        Assert.Equal(created, segment.ChannelEvents.OrderBy(value => value.Tick));
    }

    [Fact]
    public void DirectMidiEventLineUpsertUpdatesExistingAndCreatesMissingAtomically()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        DirectMidiChannelEvent existing = new(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 24
        };
        segment.ChannelEvents.Add(existing);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.UpsertDirectMidiEventPoints(
            segment.Id,
            DirectMidiChannelEventKind.ControlChange,
            laneData1: 11,
            [new(10, 11, 64), new(20, 11, 96)]));

        DirectMidiChannelEvent[] edited = segment.ChannelEvents
            .OrderBy(value => value.Tick)
            .ToArray();
        Assert.Equal(2, edited.Length);
        Assert.Same(existing, edited[0]);
        Assert.Equal(64, existing.Data2);
        Assert.Equal((20L, 11, 96), (edited[1].Tick, edited[1].Data1, edited[1].Data2));

        document.Undo();
        Assert.Same(existing, Assert.Single(segment.ChannelEvents));
        Assert.Equal(24, existing.Data2);

        document.Redo();
        edited = segment.ChannelEvents.OrderBy(value => value.Tick).ToArray();
        Assert.Equal(2, edited.Length);
        Assert.Same(existing, edited[0]);
        Assert.Equal(64, existing.Data2);
    }

    [Fact]
    public void DirectMidiNoteMoveKeepsIncumbentAndUndoRestoresMover()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        DirectMidiNote incumbent = new(project)
        {
            StartTick = 20,
            LengthTicks = 30,
            Key = 64,
            NoteOnVelocity = 80
        };
        DirectMidiNote mover = new(project)
        {
            StartTick = 10,
            LengthTicks = 50,
            Key = 64,
            NoteOnVelocity = 100
        };
        segment.Notes.AddRange([incumbent, mover]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        document.Execute(ProjectDomainEditCommands.MoveDirectMidiNotes(
            segment.Id,
            [mover.Id],
            tickDelta: 10,
            keyDelta: 0));

        Assert.Same(incumbent, Assert.Single(segment.Notes));
        document.Undo();
        Assert.Equal([incumbent, mover], segment.Notes);
        Assert.Equal(10, mover.StartTick);
        document.Redo();
        Assert.Same(incumbent, Assert.Single(segment.Notes));
    }

    [Fact]
    public void DirectMidiCollisionScopeQueriesOnlyTheEditedPagedKey()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        RecordingPagedContentSource source = new();
        segment.AttachPagedContent(source);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.CreateDirectMidiNote(
            segment.Id,
            startTick: 400,
            lengthTicks: 20,
            key: 72,
            noteOnVelocity: 100).Prepare(project);
        IPreparedProjectEdit wrapped = ExactTimelineCollisionPolicy.Wrap(project, prepared);

        wrapped.Apply(project);

        MidiSegment current = project.PureMidiTracks.SelectMany(static value => value.Segments).Single(value => value.Id == segment.Id);
        Assert.Equal(source.NoteCount + 1, current.Notes.Count);
        DirectMidiNoteValue created = current.Notes.CreateObjectSource().GetByOrdinal(source.NoteCount);
        Assert.Equal((400L, 20L, 72, 100), (created.StartTick, created.LengthTicks, created.Key, created.NoteOnVelocity));
        Assert.Single(source.NoteQueries);
        Assert.All(source.NoteQueries, query => Assert.Equal((400L, 401L, 72, 72), query));
        var querySnapshot = current.Notes.CreateQuerySnapshot();
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.Throws<TimelineValueReadPendingException>(() => querySnapshot.MaximumEndTick);
        Assert.Equal(420, querySnapshot.MaximumEndTick);
        using (TimelineValueReadScope.EnterCacheOnly())
            Assert.Equal(420, querySnapshot.MaximumEndTick);

        wrapped.Undo(project);
        Assert.Same(segment, project.PureMidiTracks.SelectMany(static value => value.Segments).Single(value => value.Id == segment.Id));
        Assert.Equal(source.NoteCount, segment.Notes.Count);
    }

    [Fact]
    public void DirectMidiMoveResolvesPagedSelectionInBatchesWithoutFullEnumeration()
    {
        (MidoraProject project, MidiSegment segment) = CreateDirectMidiFixture();
        BatchResolvedPagedContentSource source = new();
        segment.AttachPagedContent(source);
        IPreparedProjectEdit prepared = ProjectDomainEditCommands.MoveDirectMidiNotes(
            segment.Id,
            [source.Note.Id],
            tickDelta: 20,
            keyDelta: 2).Prepare(project);

        prepared.Apply(project);

        MidiSegment current = project.PureMidiTracks[0].Segments[0];
        Assert.True(current.Notes.TryGetById(source.Note.Id, out DirectMidiNote? edited));
        Assert.NotNull(edited);
        Assert.Equal(30, edited!.StartTick);
        Assert.Equal(62, edited.Key);
        Assert.Equal(1, source.BatchIdQueryCount);
        Assert.Equal(0, source.GetNoteCallCount);
        Assert.Equal(0, source.FindNoteIndexCallCount);

        prepared.Undo(project);

        Assert.Same(segment, project.PureMidiTracks[0].Segments[0]);
        var restored = Assert.Single(segment.Notes.ResolveValuesByIds(new HashSet<MidoraId> { source.Note.Id }));
        Assert.Equal(10, restored.StartTick);
        Assert.Equal(60, restored.Key);
        Assert.Equal(source.Note.Id, restored.Id);
        Assert.Equal(30, edited.StartTick); // Published roots remain immutable across Undo.
        Assert.Equal(1, source.BatchIdQueryCount);
        Assert.Equal(0, source.GetNoteCallCount);
        Assert.Equal(0, source.FindNoteIndexCallCount);
    }

    private static (MidoraProject Project, MidiSegment Segment) CreateDirectMidiFixture()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project) { Name = "Root" };
        PureMidiTrack track = new(project)
        {
            Name = "Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment segment = new(project) { LengthTicks = 480 };
        track.Segments.Add(segment);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        return (project, segment);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }

    private sealed class RecordingPagedContentSource : IPureMidiSegmentContentSource
    {
        public int NoteCount => 2_000_000;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => "recording";
        public List<(long Start, long End, int MinimumKey, int MaximumKey)> NoteQueries { get; } = [];

        public DirectMidiNoteValue GetNote(int index) =>
            throw new InvalidOperationException("A targeted collision edit must not enumerate the paged source.");

        public DirectMidiChannelEventValue GetChannelEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public OpaqueMidiEventValue GetOpaqueEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public int FindNoteIndex(MidoraId id) => -1;
        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;

        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127)
        {
            NoteQueries.Add((startTick, endTick, minimumKey, maximumKey));
            return [];
        }

        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(
            long startTick,
            long endTick) => [];

        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(
            long startTick,
            long endTick) => [];
    }

    private sealed class BatchResolvedPagedContentSource : IPureMidiSegmentContentSource
    {
        public DirectMidiNoteValue Note { get; } = new(
            new MidoraId(9_000_001),
            10,
            4,
            60,
            100,
            0,
            0,
            1);

        public int NoteCount => 6_700_000;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => "batch-resolved";
        public int BatchIdQueryCount { get; private set; }
        public int GetNoteCallCount { get; private set; }
        public int FindNoteIndexCallCount { get; private set; }

        public DirectMidiNoteValue GetNote(int index)
        {
            GetNoteCallCount++;
            throw new InvalidOperationException("A selected paged Note must be resolved in one batch.");
        }

        public DirectMidiChannelEventValue GetChannelEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public OpaqueMidiEventValue GetOpaqueEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public int FindNoteIndex(MidoraId id)
        {
            FindNoteIndexCallCount++;
            throw new InvalidOperationException("A selected paged Note must not use per-ID lookup.");
        }

        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;

        public IEnumerable<DirectMidiNoteSourceMatch> QueryNotesByIds(
            IReadOnlySet<MidoraId> ids)
        {
            BatchIdQueryCount++;
            return ids.Contains(Note.Id) ? [new(5_000_000, Note)] : [];
        }

        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127) =>
            Note.StartTick < endTick
            && Note.StartTick + Note.LengthTicks > startTick
            && Note.Key >= minimumKey
            && Note.Key <= maximumKey
                ? [Note]
                : [];

        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(
            long startTick,
            long endTick) => [];

        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(
            long startTick,
            long endTick) => [];
    }
}
