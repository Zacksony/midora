using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectCreationEditCommandsTests
{
    [Fact]
    public void LogicalTrackWithoutAnExplicitNameUsesTheNeutralDefault()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Named Instrument");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
            eventInstrumentId: instrument.Id));

        Assert.Equal("Logical Track", Assert.Single(project.Tracks).Name);
    }

    [Fact]
    public void CreatedEventInstrumentFollowsInstanceVelocityByDefault()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateEventInstrument("Instrument"));

        EventInstrument instrument = Assert.Single(project.EventInstruments);
        SubVoice voice = Assert.Single(instrument.SubVoices);
        Assert.True(SubVoiceMappingConventions.FollowsInstanceVelocity(voice));
        SubVoiceEventMapping mapping = Assert.Single(voice.EventMappings);
        ValueMappingStep step = Assert.Single(mapping.Steps);
        Assert.Equal(MappingSource.TriggerVelocity, step.Source);
        Assert.Equal(MappingOperation.Override, step.Operation);
    }

    [Fact]
    public void UndoKeepsAllocatorHighWaterRedoKeepsIdentityAndBranchUsesHigherId()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long before = project.NextStableId;

        document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Created", instrument.Id));
        LogicalTrack created = Assert.Single(project.Tracks);
        EventInstrumentUsage usage = Assert.Single(project.EventInstrumentUsages);
        long afterCreate = project.NextStableId;

        Assert.Equal(before, usage.Id.Value);
        Assert.Equal(before + 1, created.Id.Value);
        Assert.Equal(before + 2, afterCreate);
        Assert.True(document.IsModified);
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Empty(project.Tracks);
        Assert.Empty(project.EventInstrumentUsages);
        Assert.Equal(afterCreate, project.NextStableId);
        Assert.False(document.IsModified);
        Assert.True(document.CanRedo);
        AssertMatchesFull(compilation);

        document.Redo();

        Assert.Same(created, Assert.Single(project.Tracks));
        Assert.Same(usage, Assert.Single(project.EventInstrumentUsages));
        Assert.Equal(afterCreate, project.NextStableId);
        AssertMatchesFull(compilation);

        document.Undo();
        document.Execute(ProjectDomainEditCommands.CreateLogicalTrack("Branch"));
        LogicalTrack branch = Assert.Single(project.Tracks);

        Assert.False(document.CanRedo);
        Assert.True(branch.Id.Value > created.Id.Value);
        Assert.Equal(afterCreate + 1, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InstrumentAndTrackDuplicationAreDeepStableAndUndoable()
    {
        MidoraProject project = new(480);
        EventInstrument source = CreateInstrument(project, "Source");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5
        };
        source.LogicalParameters.Add(parameter);
        source.SubVoices[0].Events.Add(TemplateEvent.Note(project, 0, 120, 60, 100));
        LogicalTrack track = new(project)
        {
            Name = "Track",
            LastBoundEventInstrumentName = source.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, source.Id);
        Segment segment = new(project) { ProjectStartTick = 0, LengthTicks = 480 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 240,
            Note = 64,
            Velocity = 90
        });
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.DuplicateEventInstrument(source.Id, "Copy"));
        EventInstrument instrumentCopy = project.EventInstruments[1];
        Assert.NotEqual(source.Id, instrumentCopy.Id);
        Assert.NotEqual(source.SubVoices[0].Id, instrumentCopy.SubVoices[0].Id);
        Assert.NotEqual(source.SubVoices[0].Events[0].Id, instrumentCopy.SubVoices[0].Events[0].Id);
        Assert.NotEqual(parameter.Id, instrumentCopy.LogicalParameters[0].Id);
        Assert.Equal(source.Id, project.ResolveEventInstrumentDefinitionId(track));

        document.Execute(ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id));
        LogicalTrack trackCopy = project.Tracks.Single(value =>
            value.Id != track.Id
            && project.ResolveEventInstrumentDefinitionId(value) == source.Id);
        Assert.NotEqual(track.Id, trackCopy.Id);
        Assert.NotEqual(track.EventInstrumentUsageId, trackCopy.EventInstrumentUsageId);
        Assert.Equal(2, project.EventInstrumentUsages.Count);
        Assert.NotEqual(segment.Id, trackCopy.Segments[0].Id);
        Assert.NotEqual(segment.Notes[0].Id, trackCopy.Segments[0].Notes[0].Id);
        trackCopy.Segments[0].Notes[0].Note = 72;
        Assert.Equal(64, segment.Notes[0].Note);
        trackCopy.Segments[0].Notes[0].Note = 64;
        AssertMatchesFull(compilation);

        MidoraId copyTrackId = trackCopy.Id;
        MidoraId copyInstrumentId = instrumentCopy.Id;
        long highWater = project.NextStableId;
        document.Undo();
        document.Undo();
        Assert.Single(project.Tracks);
        Assert.Single(project.EventInstruments);
        Assert.Single(project.EventInstrumentUsages);
        Assert.Equal(highWater, project.NextStableId);
        Assert.False(document.IsModified);

        document.Redo();
        document.Redo();
        Assert.Equal(copyInstrumentId, project.EventInstruments[1].Id);
        Assert.Contains(project.Tracks, value => value.Id == copyTrackId);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void DuplicateLogicalTrackAndShareStateRetainsUsageAndUndoRestoresIdentity()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        LogicalTrack source = new(project) { Name = "Source" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, source, instrument.Id);
        MidoraId usageId = source.EventInstrumentUsageId!.Value;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.DuplicateLogicalTrackAndShareState(source.Id));

        LogicalTrack copy = Assert.Single(project.Tracks, value => value.Id != source.Id);
        Assert.Equal(usageId, copy.EventInstrumentUsageId);
        Assert.Single(project.EventInstrumentUsages);
        Assert.Equal(
            [source.Id, copy.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));

        MidoraId copyId = copy.Id;
        document.Undo();

        Assert.Same(source, Assert.Single(project.Tracks));
        Assert.Single(project.EventInstrumentUsages);

        document.Redo();

        Assert.Contains(project.Tracks, value => value.Id == copyId);
        Assert.All(project.Tracks, value => Assert.Equal(usageId, value.EventInstrumentUsageId));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void DuplicateLogicalTrackCreatesIndependentUsageAfterExistingSharedBlock()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        LogicalTrack first = new(project) { Name = "First" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, first, instrument.Id);
        LogicalTrack second = new(project)
        {
            Name = "Second",
            EventInstrumentUsageId = first.EventInstrumentUsageId,
            LastBoundEventInstrumentName = instrument.Name
        };
        project.Tracks.Add(second);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, second.Id));
        LogicalTrack tail = new(project) { Name = "Tail" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, tail, instrument.Id);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.DuplicateLogicalTrack(first.Id));

        LogicalTrack copy = Assert.Single(
            project.Tracks,
            value => value.Id != first.Id && value.Id != second.Id && value.Id != tail.Id);
        Assert.NotEqual(first.EventInstrumentUsageId, copy.EventInstrumentUsageId);
        Assert.Equal(
            [first.Id, second.Id, copy.Id, tail.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
        Assert.Equal(3, project.EventInstrumentUsages.Count);

        document.Undo();

        Assert.Equal(
            [first.Id, second.Id, tail.Id],
            project.TracksInArrangementOrder().Select(value => value.TrackId));
        Assert.Equal(2, project.EventInstrumentUsages.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentContentCreationAndSplitPreserveIdsAndExactUndo()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Value",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = new(project)
        {
            Name = "Track",
            LastBoundEventInstrumentName = instrument.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateSegment(track.Id, 0, 960));
        Segment original = Assert.Single(track.Segments);
        document.Execute(ProjectDomainEditCommands.CreateLogicalNote(
            original.Id,
            120,
            600,
            64,
            100));
        LogicalNote note = Assert.Single(original.Notes);
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            original.Id,
            parameter.Id));
        LogicalParameterLane lane = Assert.Single(original.ParameterLanes);
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            original.Id,
            lane.Id,
            0,
            0.25,
            CurveInterpolation.Step));
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterPoint(
            original.Id,
            lane.Id,
            720,
            0.75,
            CurveInterpolation.Step));
        long beforeSplit = project.NextStableId;

        document.Execute(ProjectDomainEditCommands.SplitSegment(original.Id, 480));

        Assert.Equal(2, track.Segments.Count);
        Segment left = track.Segments[0];
        Segment right = track.Segments[1];
        Assert.Equal(original.Id, left.Id);
        Assert.NotEqual(original.Id, right.Id);
        Assert.Equal(480, left.LengthTicks);
        Assert.Equal(480, right.LengthTicks);
        Assert.Equal(note.Id, Assert.Single(left.Notes).Id);
        Assert.Equal(360, left.Notes[0].LengthTicks);
        Assert.Empty(right.Notes);
        Assert.Equal(lane.Id, Assert.Single(left.ParameterLanes).Id);
        Assert.NotEqual(lane.Id, Assert.Single(right.ParameterLanes).Id);
        Assert.Contains(right.ParameterLanes[0].Points, point => point.Tick == 480);
        Assert.True(project.NextStableId > beforeSplit);
        long afterSplit = project.NextStableId;
        MidoraId rightId = right.Id;
        AssertMatchesFull(compilation);

        document.Undo();

        Assert.Same(original, Assert.Single(track.Segments));
        Assert.Same(note, Assert.Single(original.Notes));
        Assert.Same(lane, Assert.Single(original.ParameterLanes));
        Assert.Equal(afterSplit, project.NextStableId);
        AssertMatchesFull(compilation);

        document.Redo();

        Assert.Equal(rightId, track.Segments[1].Id);
        Assert.Equal(afterSplit, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MidiSegmentSplitPartitionsEveryDirectEventKindAndIsExactlyReversible()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI Track",
            MidiChannelRootId = root.Id
        };
        MidiSegment original = new(project)
        {
            ProjectStartTick = 100,
            LengthTicks = 800,
            ContentOffsetTick = 20
        };
        DirectMidiNote crossing = new(project)
        {
            StartTick = 300,
            LengthTicks = 500,
            Key = 64,
            NoteOnVelocity = 100,
            NoteOffVelocity = 32,
            NoteOnOrder = 1,
            NoteOffOrder = 4
        };
        DirectMidiNote rightNote = new(project)
        {
            StartTick = 500,
            LengthTicks = 120,
            Key = 67,
            NoteOnVelocity = 90,
            NoteOffVelocity = 12,
            NoteOnOrder = 5,
            NoteOffOrder = 8
        };
        DirectMidiChannelEvent leftEvent = new(project)
        {
            Tick = 200,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 11,
            Data2 = 72,
            Order = 2
        };
        DirectMidiChannelEvent boundaryEvent = new(project)
        {
            Tick = 420,
            Kind = DirectMidiChannelEventKind.ProgramChange,
            Data1 = 10,
            Order = 6
        };
        OpaqueMidiEvent opaque = new(project)
        {
            Tick = 700,
            Kind = OpaqueMidiEventKind.SystemExclusive,
            Payload = [0x7D, 0x01],
            Order = 7
        };
        original.Notes.Add(crossing);
        original.Notes.Add(rightNote);
        original.ChannelEvents.Add(leftEvent);
        original.ChannelEvents.Add(boundaryEvent);
        original.OpaqueEvents.Add(opaque);
        track.Segments.Add(original);
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.SplitMidiSegment(original.Id, 500));

        PureMidiTrack current = project.PureMidiTracks.Single(value => value.Id == track.Id);
        Assert.Equal(2, current.Segments.Count);
        MidiSegment left = current.Segments[0];
        MidiSegment right = current.Segments[1];
        Assert.Equal((100L, 400L, 20L),
            (left.ProjectStartTick, left.LengthTicks, left.ContentOffsetTick));
        Assert.Equal((500L, 400L, 420L),
            (right.ProjectStartTick, right.LengthTicks, right.ContentOffsetTick));
        Assert.Equal(original.Id, left.Id);
        Assert.NotEqual(original.Id, right.Id);
        DirectMidiNote splitCrossing = Assert.Single(left.Notes);
        Assert.Equal(crossing.Id, splitCrossing.Id);
        Assert.Equal(120, splitCrossing.LengthTicks);
        Assert.Equal(rightNote.Id, Assert.Single(right.Notes).Id);
        Assert.Equal(leftEvent.Id, Assert.Single(left.ChannelEvents).Id);
        Assert.Equal(boundaryEvent.Id, Assert.Single(right.ChannelEvents).Id);
        Assert.Equal(opaque.Id, Assert.Single(right.OpaqueEvents).Id);
        Assert.Equal([0x7D, 0x01], right.OpaqueEvents[0].Payload);
        MidiSegment rightAfterFirstApply = right;

        document.Undo();

        Assert.Same(original, Assert.Single(project.PureMidiTracks.Single(value => value.Id == track.Id).Segments));
        Assert.Same(crossing, original.Notes[0]);
        Assert.Same(opaque, Assert.Single(original.OpaqueEvents));

        document.Redo();

        current = project.PureMidiTracks.Single(value => value.Id == track.Id);
        Assert.Same(rightAfterFirstApply, current.Segments[1]);
        Assert.Equal(opaque.Id, Assert.Single(current.Segments[1].OpaqueEvents).Id);
    }

    [Fact]
    public void ConductorCreationCommandsValidateConflictsAndRedoOriginalIds()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateTempo(480, 90m));
        document.Execute(ProjectDomainEditCommands.CreateTimeSignature(960, 3, 4));
        document.Execute(ProjectDomainEditCommands.CreateKeySignature(0, -2, true));
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(0, string.Empty));
        document.Execute(ProjectDomainEditCommands.CreateProjectMarker(0, "Intro"));
        document.Execute(ProjectDomainEditCommands.CreateProjectEndMarker(1_920));

        Assert.Equal(2, project.Conductor.Tempos.Count);
        Assert.Equal(2, project.Conductor.TimeSignatures.Count);
        Assert.Single(project.Conductor.KeySignatures);
        Assert.Equal(2, project.Conductor.Markers.Count);
        ProjectEndMarker endMarker = Assert.IsType<ProjectEndMarker>(project.Conductor.EndMarker);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Execute(ProjectDomainEditCommands.CreateTempo(480, 100m));
        Assert.Equal(100m, project.Conductor.Tempos.Single(value => value.Tick == 480).BeatsPerMinute);
        document.Undo();
        Assert.Equal(90m, project.Conductor.Tempos.Single(value => value.Tick == 480).BeatsPerMinute);
        highWater = project.NextStableId;
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.CreateProjectEndMarker(2_400)));
        Assert.Equal(highWater, project.NextStableId);

        document.Undo();
        Assert.Null(project.Conductor.EndMarker);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(endMarker, project.Conductor.EndMarker);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InvalidCreationIsRejectedBeforeAllocatingStableId()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        LogicalTrack track = new(project)
        {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long highWater = project.NextStableId;

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.CreateSegment(track.Id, -1, 480)));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Execute(
            ProjectDomainEditCommands.CreateLogicalTrack(
                "Track",
                instrument.Id,
                insertionIndex: 2)));

        Assert.Equal(highWater, project.NextStableId);
        Assert.Empty(document.History);
        Assert.False(document.IsModified);
    }

    [Fact]
    public void SubVoiceTemplateAndCurveCreationAreDeepUndoableAndDeterministic()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        instrument.RequiresChannelIsolation = true;
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateSubVoice(
            instrument.Id,
            "Layer",
            rootNoteOverride: 64));
        SubVoice source = instrument.SubVoices[1];
        document.Execute(ProjectDomainEditCommands.CreateTemplateNote(
            instrument.Id,
            source.Id,
            0,
            120,
            60,
            100));
        TemplateEvent note = Assert.Single(source.Events);
        ValueMappingStep followVelocity = Assert.Single(note.ValueMappings);
        Assert.Equal(MappingSource.TriggerVelocity, followVelocity.Source);
        Assert.Equal(MappingOperation.Override, followVelocity.Operation);
        document.Execute(ProjectDomainEditCommands.CreateMappingStep(
            instrument.Id,
            note.ValueMappings.Id,
            constant: 0.5,
            targetMaximum: 127));
        document.Execute(ProjectDomainEditCommands.CreateValueCurve(
            instrument.Id,
            source.Id,
            MidiValueTarget.ControlChange(1)));
        ValueCurve curve = Assert.Single(source.Curves);
        document.Execute(ProjectDomainEditCommands.CreateValueCurvePoint(
            instrument.Id,
            source.Id,
            curve.Id,
            0,
            64));
        source.InitialState.Program = 12;

        document.Execute(ProjectDomainEditCommands.DuplicateSubVoice(
            instrument.Id,
            source.Id,
            "Layer Copy"));

        SubVoice copy = instrument.SubVoices[2];
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(12, copy.InitialState.Program);
        Assert.NotEqual(note.Id, Assert.Single(copy.Events).Id);
        Assert.NotEqual(note.ValueMappings.Id, copy.Events[0].ValueMappings.Id);
        Assert.Equal(2, note.ValueMappings.Count);
        Assert.Equal(2, copy.Events[0].ValueMappings.Count);
        Assert.All(
            note.ValueMappings.Zip(copy.Events[0].ValueMappings),
            pair => Assert.NotEqual(pair.First.Id, pair.Second.Id));
        Assert.NotEqual(curve.Id, Assert.Single(copy.Curves).Id);
        Assert.NotEqual(
            Assert.Single(curve.Points).Id,
            Assert.Single(copy.Curves[0].Points).Id);
        long highWater = project.NextStableId;
        MidoraId copyId = copy.Id;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Equal(2, instrument.SubVoices.Count);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Equal(copyId, instrument.SubVoices[2].Id);
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void TemplateCreationAtOccupiedTargetReplacesExistingEvent()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent existing = TemplateEvent.ControlChange(project, 0, 7, 20);
        voice.Events.Add(existing);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateTemplateControlChange(
            instrument.Id,
            voice.Id,
            0,
            7,
            100));

        TemplateEvent created = Assert.Single(voice.Events);
        Assert.NotSame(existing, created);
        Assert.Equal(100, created.Value);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Same(existing, Assert.Single(voice.Events));
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(created, Assert.Single(voice.Events));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalParameterCreationCanCommitCompleteEnumDefinitionAtomically()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Mode",
            LogicalParameterType.Enum,
            minimum: 0,
            maximum: 2,
            displayMinimum: 0,
            displayMaximum: 2,
            defaultValue: 1,
            usesExplicitEnumValues: true,
            enumItems:
            [
                new(null, "Low", 0),
                new(null, "High", 1)
            ]));

        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        Assert.Collection(
            parameter.EnumItems,
            item => Assert.Equal(("Low", 0), (item.Name, item.Value)),
            item => Assert.Equal(("High", 1), (item.Name, item.Value)));
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(instrument.LogicalParameters);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(parameter, Assert.Single(instrument.LogicalParameters));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void ParameterMappingEnvelopeAndFunctionCreationUseOneHistoryProtocol()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        instrument.RequiresChannelIsolation = true;
        SubVoice voice = instrument.SubVoices[0];
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameter(
            instrument.Id,
            "Mode",
            LogicalParameterType.Enum,
            0,
            10,
            0,
            10,
            0));
        LogicalParameterDefinition parameter = Assert.Single(instrument.LogicalParameters);
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterEnumItem(
            instrument.Id,
            parameter.Id,
            "Off"));
        document.Execute(ProjectDomainEditCommands.CreateInstrumentEnvelope(
            instrument.Id,
            "Shape",
            attackTicks: 10,
            releaseTicks: 20));
        document.Execute(ProjectDomainEditCommands.CreateMappingFunction(
            instrument.Id,
            "Identity",
            "value",
            []));
        document.Execute(ProjectDomainEditCommands.DuplicateMappingFunction(
            instrument.Id,
            instrument.MappingFunctions[0].Id));
        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterMapping(
            instrument.Id,
            parameter.Id,
            voice.Id,
            MidiValueTarget.ControlChange(7)));
        LogicalParameterMapping mapping = Assert.Single(instrument.ParameterMappings);
        document.Execute(ProjectDomainEditCommands.CreateMappingStep(
            instrument.Id,
            mapping.Steps.Id,
            MappingSource.LogicalParameter,
            MappingOperation.Override,
            logicalParameterId: parameter.Id,
            targetMinimum: 0,
            targetMaximum: 127));

        Assert.Equal("Off", Assert.Single(parameter.EnumItems).Name);
        Assert.Single(instrument.Envelopes);
        Assert.Equal(2, instrument.MappingFunctions.Count);
        Assert.Equal("Identity Copy", instrument.MappingFunctions[1].Name);
        ValueMappingStep step = Assert.Single(mapping.Steps);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(mapping.Steps);
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(step, Assert.Single(mapping.Steps));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void RepeatedLaneCreationIsNoOpAndDoesNotAllocateIdentity()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Value",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0
        };
        instrument.LogicalParameters.Add(parameter);
        LogicalTrack track = new(project)
        {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { ProjectStartTick = 0, LengthTicks = 480 };
        track.Segments.Add(segment);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            segment.Id,
            parameter.Id));
        long highWater = project.NextStableId;
        int historyCount = document.History.Count;

        document.Execute(ProjectDomainEditCommands.CreateLogicalParameterLane(
            segment.Id,
            parameter.Id));

        Assert.Single(segment.ParameterLanes);
        Assert.Equal(highWater, project.NextStableId);
        Assert.Equal(historyCount, document.History.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MappingChainPasteAllocatesNewChainAndStepsAndUndoRestoresTarget()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = CreateInstrument(project, "Instrument");
        SubVoice voice = instrument.SubVoices[0];
        TemplateEvent sourceEvent = TemplateEvent.ControlChange(project, 0, 1, 100);
        TemplateEvent targetEvent = TemplateEvent.ControlChange(project, 240, 11, 100);
        ValueMappingStep sourceStep = new(project)
        {
            Source = MappingSource.Constant,
            Operation = MappingOperation.Add,
            Constant = 5
        };
        ValueMappingStep targetStep = new(project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.Multiply,
            Constant = 2
        };
        sourceEvent.ValueMappings.Add(sourceStep);
        targetEvent.ValueMappings.Add(targetStep);
        voice.Events.Add(sourceEvent);
        voice.Events.Add(targetEvent);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        MappingChain originalTarget = targetEvent.ValueMappings;
        long before = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectDomainEditCommands.PasteMappingChain(
                instrument.Id,
                sourceEvent.ValueMappings.Id,
                originalTarget.Id,
                nonEmptyReplacementConfirmed: false)));
        Assert.Equal(before, project.NextStableId);

        document.Execute(ProjectDomainEditCommands.PasteMappingChain(
            instrument.Id,
            sourceEvent.ValueMappings.Id,
            originalTarget.Id,
            nonEmptyReplacementConfirmed: true));

        MappingChain pasted = targetEvent.ValueMappings;
        ValueMappingStep pastedStep = Assert.Single(pasted);
        Assert.NotSame(originalTarget, pasted);
        Assert.NotEqual(originalTarget.Id, pasted.Id);
        Assert.NotEqual(sourceEvent.ValueMappings.Id, pasted.Id);
        Assert.NotEqual(sourceStep.Id, pastedStep.Id);
        Assert.Equal(sourceStep.Constant, pastedStep.Constant);
        long highWater = project.NextStableId;
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Same(originalTarget, targetEvent.ValueMappings);
        Assert.Same(targetStep, Assert.Single(originalTarget));
        Assert.Equal(highWater, project.NextStableId);
        document.Redo();
        Assert.Same(pasted, targetEvent.ValueMappings);
        Assert.Same(pastedStep, Assert.Single(pasted));
        Assert.Equal(highWater, project.NextStableId);
        AssertMatchesFull(compilation);
    }

    private static EventInstrument CreateInstrument(MidoraProject project, string name)
    {
        EventInstrument instrument = EventInstrumentLibrary.Create(project, name);
        return instrument;
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        CanonicalCompiledResult full = new MidoraCompiler().CompileFull(
            compilation.Project,
            compilation.LastAttempt.Context.RequestedEndTick.HasValue
                ? new CompilationRequest
                {
                    Purpose = compilation.LastAttempt.Context.Purpose,
                    StartTick = compilation.LastAttempt.Context.StartTick,
                    EndTick = compilation.LastAttempt.Context.RequestedEndTick,
                    TreatWarningsAsErrors = compilation.LastAttempt.Context.TreatWarningsAsErrors
                }
                : new CompilationRequest
                {
                    Purpose = compilation.LastAttempt.Context.Purpose,
                    StartTick = compilation.LastAttempt.Context.StartTick,
                    TreatWarningsAsErrors = compilation.LastAttempt.Context.TreatWarningsAsErrors
                });
        Assert.Equal(full.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(full.IsConsumable, compilation.LastAttempt.IsConsumable);
        Assert.Equal(full.Events, compilation.LastAttempt.Events);
        Assert.Equal(full.Diagnostics, compilation.LastAttempt.Diagnostics);
    }
}
