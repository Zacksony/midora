using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectObjectClipboardTests
{
    [Fact]
    public void SegmentPasteStableMergeKeepsExistingFormalOrderAndUndoRestoresOriginalRoot()
    {
        using MidoraProject project = new(192);
        LogicalTrack source = new(project) { Name = "Source" };
        LogicalTrack target = new(project) { Name = "Target" };
        AddIndependentLogicalTracks(project, source, target);
        Segment first = new(project) { ProjectStartTick = 0, LengthTicks = 4 };
        Segment second = new(project) { ProjectStartTick = 100, LengthTicks = 4 };
        source.Segments.AddRange([second, first]);
        Segment existingLate = new(project) { ProjectStartTick = 200, LengthTicks = 4 };
        Segment existingEarly = new(project) { ProjectStartTick = 10, LengthTicks = 4 };
        target.Segments.AddRange([existingLate, existingEarly]);
        using ProjectCompilationSession compilation = new(project);
        using ProjectDocumentSession document = PersistedDocument(compilation);
        using ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document, [second.Id, first.Id], first.Id);
        IProjectEditCommand command = ProjectObjectClipboard.CreatePasteSegmentsCommand(document,
            payload, target.Id, 20);
        using (command as IDisposable) document.Execute(command);
        LogicalTrack published = Active(project, target);
        Assert.Equal(new long[] { 20, 120, 200, 10 }, published.Segments.Select(value => value.ProjectStartTick));
        Assert.Equal(new[] { existingLate.Id, existingEarly.Id }, published.Segments.Skip(2).Select(value => value.Id));
        Assert.Equal(new long[] { 200, 10 }, target.Segments.Select(value => value.ProjectStartTick));
        document.Undo();
        Assert.Same(target, Active(project, target));
        document.Redo();
        Assert.Same(published, Active(project, target));
    }

    [Fact]
    public void LogicalTrackClipboardDeepCopiesContentAndPreservesValidBinding()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack source = new(project) {
            Name = "Source",
            LastBoundEventInstrumentName = instrument.Name,
            ColorOverride = new MidoraColor(40, 80, 120)
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, source, instrument.Id);
        Segment segment = new(project)
        {
            ProjectStartTick = 120,
            LengthTicks = 240,
            ContentOffsetTick = 20
        };
        LogicalNote note = new(project)
        {
            StartTick = 30,
            LengthTicks = 60,
            Note = 65,
            Velocity = 95
        };
        Active(project, segment).Notes.Add(note);
        Active(project, source).Segments.Add(segment);
        LogicalTrack peer = new(project) {
            Name = "Peer",
            LastBoundEventInstrumentName = instrument.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, peer, instrument.Id);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyLogicalTrack(
            document,
            source.Id);
        source.Name = "Changed after copy";
        note.Note = 12;

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalTrackCommand(
            document,
            payload,
            targetEventInstrumentId: instrument.Id,
            insertionIndex: 1));

        LogicalTrack copy = project.Tracks.Single(value => value.Id != source.Id && value.Id != peer.Id);
        Assert.Equal("Source", copy.Name);
        Assert.Equal(instrument.Id, project.ResolveEventInstrumentDefinitionId(copy));
        Assert.Equal(source.ColorOverride, copy.ColorOverride);
        Assert.NotEqual(source.Id, copy.Id);
        Segment segmentCopy = Assert.Single(Active(project, copy).Segments);
        LogicalNote noteCopy = Assert.Single(Active(project, segmentCopy).Notes);
        Assert.NotEqual(segment.Id, segmentCopy.Id);
        Assert.NotEqual(note.Id, noteCopy.Id);
        Assert.Equal((120L, 240L, 20L), (
            segmentCopy.ProjectStartTick,
            segmentCopy.LengthTicks,
            segmentCopy.ContentOffsetTick));
        Assert.Equal((30L, 60L, 65, 95), (
            noteCopy.StartTick,
            noteCopy.LengthTicks,
            noteCopy.Note,
            noteCopy.Velocity));

        document.Undo();
        Assert.Equal([source, peer], project.Tracks);
        document.Redo();
        Assert.Same(copy, project.Tracks[^1]);
        Assert.Equal(
            [source.Id, copy.Id, peer.Id],
            project.LogicalTracksInArrangementOrder().Select(value => value.Id));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void EventInstrumentClipboardIsDeepSnapshotAndPasteRemapsOwnedReferences()
    {
        MidoraProject project = new(480);
        EventInstrument source = EventInstrumentLibrary.Create(project, "Lead");
        source.Description = "Snapshot description";
        source.Color = new MidoraColor(21, 91, 173);
        source.RequiresChannelIsolation = true;
        SubVoice voice = Assert.Single(Active(project, source).SubVoices);
        voice.Name = "Main";
        voice.Events.Add(TemplateEvent.Note(project, 0, 240, 60, 100));
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0.5
        };
        InstrumentEnvelope envelope = new(project) { Name = "Shape", PeakValue = 0.75 };
        CSharpMappingFunction function = new(project)
        {
            Name = "Identity",
            Body = "value"
        };
        Active(project, source).LogicalParameters.Add(parameter);
        Active(project, source).Envelopes.Add(envelope);
        Active(project, source).MappingFunctions.Add(function);
        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.Override,
            LogicalParameterId = parameter.Id
        });
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.Envelope,
            Operation = MappingOperation.Multiply,
            EnvelopeId = envelope.Id
        });
        mapping.Steps.Add(new ValueMappingStep(project)
        {
            Source = MappingSource.CurrentValue,
            Operation = MappingOperation.CustomCSharp,
            MappingFunctionId = function.Id
        });
        Active(project, source).ParameterMappings.Add(mapping);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyEventInstrument(
            document,
            source.Id);
        parameter.Name = "Mutated after copy";
        function.Body = "0";
        long firstPastedId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteEventInstrumentCommand(
            document,
            payload));

        EventInstrument copy = project.EventInstruments.Single(value => value.Id != source.Id);
        Assert.True(copy.Id.Value >= firstPastedId);
        Assert.Equal("Lead Copy 1", copy.Name);
        Assert.Equal("Snapshot description", copy.Description);
        Assert.Equal(source.Color, copy.Color);
        LogicalParameterDefinition parameterCopy = Assert.Single(Active(project, copy).LogicalParameters);
        InstrumentEnvelope envelopeCopy = Assert.Single(Active(project, copy).Envelopes);
        CSharpMappingFunction functionCopy = Assert.Single(Active(project, copy).MappingFunctions);
        SubVoice voiceCopy = Assert.Single(Active(project, copy).SubVoices);
        LogicalParameterMapping mappingCopy = Assert.Single(Active(project, copy).ParameterMappings);
        Assert.Equal("Amount", parameterCopy.Name);
        Assert.Equal("value", functionCopy.Body);
        Assert.NotEqual(parameter.Id, parameterCopy.Id);
        Assert.NotEqual(envelope.Id, envelopeCopy.Id);
        Assert.NotEqual(function.Id, functionCopy.Id);
        Assert.NotEqual(voice.Id, voiceCopy.Id);
        Assert.Equal(parameterCopy.Id, mappingCopy.ParameterId);
        Assert.Equal(voiceCopy.Id, mappingCopy.SubVoiceId);
        Assert.Equal(parameterCopy.Id, mappingCopy.Steps[0].LogicalParameterId);
        Assert.Equal(envelopeCopy.Id, mappingCopy.Steps[1].EnvelopeId);
        Assert.Equal(functionCopy.Id, mappingCopy.Steps[2].MappingFunctionId);
        Assert.Single(document.History);

        document.Undo();
        Assert.DoesNotContain(copy, project.EventInstruments);
        document.Redo();
        Assert.Same(copy, project.EventInstruments.Single(value => value.Id == copy.Id));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentPayloadIsImmutableDeepSnapshotWithFreshOwnedIds()
    {
        MidoraProject project = new(480);
        LogicalTrack sourceTrack = new(project) { Name = "Source" };
        LogicalTrack targetTrack = new(project) { Name = "Target" };
        AddIndependentLogicalTracks(project, sourceTrack, targetTrack);
        MidoraId externalParameterId = MidoraId.FromSequence(900_000);
        Segment source = new(project)
        {
            ProjectStartTick = 10,
            LengthTicks = 100,
            ContentOffsetTick = 25
        };
        LogicalNote note = new(project)
        {
            StartTick = 4,
            LengthTicks = 20,
            Note = 61,
            Velocity = 99
        };
        LogicalParameterLane lane = new(project) { ParameterId = externalParameterId };
        CurvePoint point = new(project, 7, 0.25, CurveInterpolation.Step);
        Active(project, lane).Points.Add(point);
        Active(project, source).Notes.Add(note);
        source.ParameterLanes.Add(lane);
        Active(project, sourceTrack).Segments.Add(source);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [source.Id],
            source.Id);
        document.Execute(ProjectDomainEditCommands.DeleteSegments([source.Id]));
        long beforePasteId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(
            document,
            payload,
            targetTrack.Id,
            editCursorTick: 500));

        Segment copy = Assert.Single(Active(project, targetTrack).Segments);
        LogicalNote noteCopy = Assert.Single(Active(project, copy).Notes);
        LogicalParameterLane laneCopy = Assert.Single(copy.ParameterLanes);
        CurvePoint pointCopy = Assert.Single(Active(project, laneCopy).Points);
        Assert.Equal((500L, 100L, 25L),
            (copy.ProjectStartTick, copy.LengthTicks, copy.ContentOffsetTick));
        Assert.Equal((4L, 20L, 61, 99),
            (noteCopy.StartTick, noteCopy.LengthTicks, noteCopy.Note, noteCopy.Velocity));
        Assert.Equal((7L, 0.25, CurveInterpolation.Step),
            (pointCopy.Tick, pointCopy.Value, pointCopy.Interpolation));
        Assert.Equal(externalParameterId, laneCopy.ParameterId);
        Assert.All(
            new[] { copy.Id, noteCopy.Id, laneCopy.Id, pointCopy.Id },
            id => Assert.True(id.Value >= beforePasteId));
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(Active(project, targetTrack).Segments);
        document.Redo();
        Assert.Same(copy, Assert.Single(Active(project, targetTrack).Segments));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SegmentPastePreservesRelativeTrackAndTimeOffsets()
    {
        MidoraProject project = new(480);
        LogicalTrack targetPrimary = new(project) { Name = "Target 1" };
        LogicalTrack targetSecondary = new(project) { Name = "Target 2" };
        LogicalTrack sourcePrimary = new(project) { Name = "Source 1" };
        LogicalTrack sourceSecondary = new(project) { Name = "Source 2" };
        AddIndependentLogicalTracks(
            project,
            targetPrimary,
            targetSecondary,
            sourcePrimary,
            sourceSecondary);
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 220, LengthTicks = 60 };
        Active(project, sourcePrimary).Segments.Add(first);
        Active(project, sourceSecondary).Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [second.Id, first.Id],
            first.Id);

        document.Execute(ProjectObjectClipboard.CreatePasteSegmentsCommand(
            document,
            payload,
            targetPrimary.Id,
            editCursorTick: 500));

        Assert.Equal(500, Assert.Single(Active(project, targetPrimary).Segments).ProjectStartTick);
        Assert.Equal(620, Assert.Single(Active(project, targetSecondary).Segments).ProjectStartTick);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InvalidSegmentTargetRejectsBeforeIdsHistoryOrMutation()
    {
        MidoraProject project = new(480);
        LogicalTrack target = new(project) { Name = "Target" };
        LogicalTrack sourcePrimary = new(project) { Name = "Source 1" };
        LogicalTrack sourceSecondary = new(project) { Name = "Source 2" };
        AddIndependentLogicalTracks(project, sourcePrimary, sourceSecondary, target);
        Segment first = new(project) { ProjectStartTick = 100, LengthTicks = 60 };
        Segment second = new(project) { ProjectStartTick = 220, LengthTicks = 60 };
        Active(project, sourcePrimary).Segments.Add(first);
        Active(project, sourceSecondary).Segments.Add(second);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            document,
            [first.Id, second.Id],
            first.Id);
        long nextStableId = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteSegmentsCommand(
                document,
                payload,
                target.Id,
                editCursorTick: 500)));

        Assert.Empty(Active(project, target).Segments);
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Empty(document.History);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void LogicalNotePayloadSurvivesCutAndEachPasteAllocatesNewIds()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment source = new(project) { LengthTicks = 480 };
        Segment target = new(project) { ProjectStartTick = 480, LengthTicks = 480 };
        LogicalNote first = new(project)
        {
            StartTick = 20,
            LengthTicks = 40,
            Note = 60,
            Velocity = 80
        };
        LogicalNote second = new(project)
        {
            StartTick = 140,
            LengthTicks = 60,
            Note = 64,
            Velocity = 100
        };
        Active(project, source).Notes.AddRange([first, second]);
        Active(project, track).Segments.AddRange([source, target]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyLogicalNotes(
            document,
            source.Id,
            [second.Id, first.Id]);
        document.Execute(ProjectDomainEditCommands.DeleteLogicalNotes(
            source.Id,
            [first.Id, second.Id]));

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
            document,
            payload,
            target.Id,
            editCursorTick: 30));
        LogicalNote[] firstPaste = Active(project, target).Notes.ToArray();
        document.Execute(ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
            document,
            payload,
            target.Id,
            editCursorTick: 300));
        LogicalNote[] secondPaste = Active(project, target).Notes.Skip(2).ToArray();

        Assert.Equal([30L, 150L], firstPaste.Select(value => value.StartTick));
        Assert.Equal([300L, 420L], secondPaste.Select(value => value.StartTick));
        Assert.Equal([60, 64], firstPaste.Select(value => value.Note));
        Assert.Empty(firstPaste.Select(value => value.Id)
            .Intersect(secondPaste.Select(value => value.Id)));
        Assert.Equal(3, document.History.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void PayloadKindAndProjectSessionAreStrictlyValidated()
    {
        MidoraProject sourceProject = new(480);
        LogicalTrack sourceTrack = new(sourceProject) { Name = "Source" };
        Segment sourceSegment = new(sourceProject) { LengthTicks = 100 };
        sourceTrack.Segments.Add(sourceSegment);
        AddIndependentLogicalTracks(sourceProject, sourceTrack);
        using ProjectCompilationSession sourceCompilation = new(sourceProject);
        ProjectDocumentSession sourceDocument = PersistedDocument(sourceCompilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySegments(
            sourceDocument,
            [sourceSegment.Id],
            sourceSegment.Id);

        Assert.Throws<ArgumentException>(() =>
            ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
                sourceDocument,
                payload,
                sourceSegment.Id,
                editCursorTick: 0));

        MidoraProject otherProject = new(480);
        LogicalTrack otherTrack = new(otherProject) { Name = "Other" };
        otherProject.Tracks.Add(otherTrack);
        using ProjectCompilationSession otherCompilation = new(otherProject);
        ProjectDocumentSession otherDocument = PersistedDocument(otherCompilation);
        Assert.Throws<InvalidOperationException>(() =>
            ProjectObjectClipboard.CreatePasteSegmentsCommand(
                otherDocument,
                payload,
                otherTrack.Id,
                editCursorTick: 0));
    }

    [Fact]
    public void LogicalParameterLaneAndContentRequireExactTargetAndAlignEarliestPoint()
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
        Active(project, instrument).LogicalParameters.Add(parameter);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) {
            Name = "Track",
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment source = new(project) { LengthTicks = 480 };
        Segment target = new(project) { ProjectStartTick = 480, LengthTicks = 480 };
        LogicalParameterLane sourceLane = new(project) { ParameterId = parameter.Id };
        CurvePoint first = new(project, 10, 0.25, CurveInterpolation.Step);
        CurvePoint second = new(project, 30, 0.75);
        Active(project, sourceLane).Points.AddRange([first, second]);
        source.ParameterLanes.Add(sourceLane);
        Active(project, track).Segments.AddRange([source, target]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload lanePayload =
            ProjectObjectClipboard.CopyLogicalParameterLane(
                document,
                source.Id,
                sourceLane.Id);
        ProjectObjectClipboardPayload contentPayload =
            ProjectObjectClipboard.CopyLogicalParameterLaneContent(
                document,
                source.Id,
                sourceLane.Id,
                [second.Id, first.Id]);
        document.Execute(ProjectObjectClipboard.CreatePasteLogicalParameterLaneCommand(
            document,
            lanePayload,
            target.Id,
            editCursorTick: 100));

        LogicalParameterLane targetLane = Assert.Single(Active(project, target).ParameterLanes);
        Assert.NotEqual(sourceLane.Id, targetLane.Id);
        Assert.Equal(parameter.Id, targetLane.ParameterId);
        Assert.Equal([100L, 120L], Active(project, targetLane).Points.Select(value => value.Tick));
        Assert.Equal([0.25, 0.75], Active(project, targetLane).Points.Select(value => value.Value));

        document.Execute(
            ProjectObjectClipboard.CreatePasteLogicalParameterLaneContentCommand(
                document,
                contentPayload,
                target.Id,
                targetLane.Id,
                editCursorTick: 200));

        Assert.Equal([100L, 120L, 200L, 220L],
            Active(project, targetLane).Points.Select(value => value.Tick));
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        long nextStableId = project.NextStableId;
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteLogicalParameterLaneCommand(
                document,
                lanePayload,
                target.Id,
                editCursorTick: 300)));
        Assert.Equal(nextStableId, project.NextStableId);
        Assert.Equal(2, document.History.Count);
    }

    [Fact]
    public void ConductorPayloadCopiesOrdinaryEventsAtomicallyAndExcludesEndMarker()
    {
        MidoraProject project = new(480);
        TempoChange tempo = new(project, 100, 90m);
        TimeSignatureChange timeSignature = new(project, 200, 3, 4);
        KeySignatureChange keySignature = new(project, 220, -2, true);
        ProjectMarker firstMarker = new(project, 250, "A");
        ProjectMarker secondMarker = new(project, 250, "B");
        project.Conductor.Tempos.Add(tempo);
        project.Conductor.TimeSignatures.Add(timeSignature);
        project.Conductor.KeySignatures.Add(keySignature);
        project.Conductor.Markers.AddRange([firstMarker, secondMarker]);
        project.SetEndMarker(1_000);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopyConductorEvents(
            document,
            [secondMarker.Id, keySignature.Id, tempo.Id, timeSignature.Id, firstMarker.Id]);

        document.Execute(ProjectObjectClipboard.CreatePasteConductorEventsCommand(
            document,
            payload,
            editCursorTick: 500));

        Assert.Contains(project.Conductor.Tempos,
            value => value.Tick == 500 && value.BeatsPerMinute == 90m);
        Assert.Contains(project.Conductor.TimeSignatures,
            value => value.Tick == 600 && value.Numerator == 3 && value.Denominator == 4);
        Assert.Contains(project.Conductor.KeySignatures,
            value => value.Tick == 620 && value.SharpsFlats == -2 && value.IsMinor);
        Assert.Equal(["A", "B"], project.Conductor.Markers
            .Where(value => value.Tick == 650)
            .Select(value => value.Name)
            .Order(StringComparer.Ordinal));
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        long nextStableId = project.NextStableId;
        document.Execute(
            ProjectObjectClipboard.CreatePasteConductorEventsCommand(
                document,
                payload,
                editCursorTick: 500));
        Assert.True(project.NextStableId > nextStableId);
        Assert.Equal(2, document.History.Count);
        Assert.Single(project.Conductor.Tempos, value => value.Tick == 500);
        Assert.Equal(4, project.Conductor.Markers.Count(value => value.Tick == 650));
        document.Undo();
        Assert.Equal(2, project.Conductor.Markers.Count(value => value.Tick == 650));

        Assert.Throws<ArgumentException>(() => ProjectObjectClipboard.CopyConductorEvents(
            document,
            [project.Conductor.EndMarker!.Id]));
    }

    [Fact]
    public void SubVoiceTimelinePasteUsesTheTargetSubVoiceSharedMappingDefinition()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 100
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1
        };
        InstrumentEnvelope envelope = new(project) { Name = "Envelope" };
        CSharpMappingFunction function = new(project)
        {
            Name = "Function",
            Body = "value"
        };
        SubVoice source = new(project) { Name = "Source" };
        SubVoice target = new(project) { Name = "Target" };
        TemplateEvent templateEvent = TemplateEvent.ControlChange(project, 10, 1, 64);
        templateEvent.ValueMappings.IsEnabled = false;
        ValueMappingStep sourceStep = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.CustomCSharp,
            LogicalParameterId = parameter.Id,
            EnvelopeId = envelope.Id,
            MappingFunctionId = function.Id,
            Constant = 0.25,
            SourceMinimum = -1,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127,
            InputOverflow = MappingInputOverflow.Extrapolate,
            DivideByZero = DivideByZeroPolicy.Zero
        };
        templateEvent.ValueMappings.Add(sourceStep);
        templateEvent.ValueTargetSettings.Rounding = MappingRounding.Floor;
        templateEvent.ValueTargetSettings.Overflow = MappingOverflow.Clamp;
        source.Events.Add(templateEvent);
        Active(project, instrument).LogicalParameters.Add(parameter);
        Active(project, instrument).Envelopes.Add(envelope);
        Active(project, instrument).MappingFunctions.Add(function);
        Active(project, instrument).SubVoices.AddRange([source, target]);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySubVoiceTimelineEvents(
            document,
            instrument.Id,
            source.Id,
            [templateEvent.Id]);
        long beforePasteId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
            document,
            payload,
            instrument.Id,
            target.Id,
            editCursorTick: 200));

        TemplateEvent copy = Assert.Single(Active(project, target).Events);
        Assert.Equal(200, copy.Tick);
        Assert.NotEqual(templateEvent.Id, copy.Id);
        Assert.NotEqual(templateEvent.ValueMappings.Id, copy.ValueMappings.Id);
        Assert.True(copy.Id.Value >= beforePasteId);
        Assert.Empty(copy.ValueMappings);
        Assert.True(copy.ValueMappings.IsEnabled);
        Assert.Equal((MappingRounding.Round, MappingOverflow.Fail),
            (copy.ValueTargetSettings.Rounding, copy.ValueTargetSettings.Overflow));
        Assert.Equal(201, Active(project, instrument).TemplateLengthTicks);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Empty(Active(project, target).Events);
        Assert.Equal(100, Active(project, instrument).TemplateLengthTicks);
        document.Redo();
        Assert.Same(copy, Assert.Single(Active(project, target).Events));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void WholeSubVoicePasteAcrossInstrumentsCopiesAndRemapsMappingDependencies()
    {
        MidoraProject project = new(480);
        EventInstrument sourceInstrument = new(project) { Name = "Source", TemplateLengthTicks = 480 };
        EventInstrument targetInstrument = new(project) { Name = "Target", TemplateLengthTicks = 480 };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0.5
        };
        InstrumentEnvelope envelope = new(project) { Name = "Envelope", AttackTicks = 12 };
        CSharpMappingFunction function = new(project) { Name = "Function", Body = "value" };
        SubVoice source = new(project) { Name = "Layer", RootNoteOverride = 72 };
        source.InitialState.Controllers.Add(11, 90);
        TemplateEvent controller = TemplateEvent.ControlChange(project, 20, 11, 64);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.CustomCSharp,
            LogicalParameterId = parameter.Id,
            EnvelopeId = envelope.Id,
            MappingFunctionId = function.Id
        };
        controller.ValueMappings.Add(step);
        source.Events.Add(controller);
        Active(project, sourceInstrument).LogicalParameters.Add(parameter);
        Active(project, sourceInstrument).Envelopes.Add(envelope);
        Active(project, sourceInstrument).MappingFunctions.Add(function);
        Active(project, sourceInstrument).SubVoices.Add(source);
        Active(project, targetInstrument).SubVoices.Add(new(project) { Name = "Existing" });
        project.EventInstruments.AddRange([sourceInstrument, targetInstrument]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload payload = ProjectObjectClipboard.CopySubVoice(
            document,
            sourceInstrument.Id,
            source.Id);
        long firstCopiedId = project.NextStableId;

        document.Execute(ProjectObjectClipboard.CreatePasteSubVoiceCommand(
            document,
            payload,
            targetInstrument.Id,
            insertionIndex: 1));

        SubVoice copy = Active(project, targetInstrument).SubVoices[1];
        TemplateEvent eventCopy = Assert.Single(copy.Events);
        ValueMappingStep stepCopy = Assert.Single(eventCopy.ValueMappings);
        LogicalParameterDefinition parameterCopy = Assert.Single(Active(project, targetInstrument).LogicalParameters);
        InstrumentEnvelope envelopeCopy = Assert.Single(Active(project, targetInstrument).Envelopes);
        CSharpMappingFunction functionCopy = Assert.Single(Active(project, targetInstrument).MappingFunctions);
        Assert.Equal(("Layer", 72), (copy.Name, copy.RootNoteOverride));
        Assert.Equal(90, copy.InitialState.Controllers[11]);
        Assert.All(
            new[] { copy.Id, eventCopy.Id, stepCopy.Id, parameterCopy.Id, envelopeCopy.Id, functionCopy.Id },
            id => Assert.True(id.Value >= firstCopiedId));
        Assert.Equal(parameterCopy.Id, stepCopy.LogicalParameterId);
        Assert.Equal(envelopeCopy.Id, stepCopy.EnvelopeId);
        Assert.Equal(functionCopy.Id, stepCopy.MappingFunctionId);
        Assert.NotEqual(parameter.Id, parameterCopy.Id);
        Assert.Single(document.History);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Single(Active(project, targetInstrument).SubVoices);
        Assert.Empty(Active(project, targetInstrument).LogicalParameters);
        Assert.Empty(Active(project, targetInstrument).Envelopes);
        Assert.Empty(Active(project, targetInstrument).MappingFunctions);
        AssertMatchesFull(compilation);

        document.Redo();
        Assert.Same(copy, Active(project, targetInstrument).SubVoices[1]);
        Assert.Same(parameterCopy, Assert.Single(Active(project, targetInstrument).LogicalParameters));
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void SubVoiceAndValueCurvePasteRejectIncompatibleTargetsBeforeMutation()
    {
        MidoraProject project = new(480);
        EventInstrument sourceInstrument = new(project) { Name = "Source Instrument" };
        EventInstrument otherInstrument = new(project) { Name = "Other Instrument" };
        SubVoice sourceVoice = new(project) { Name = "Source" };
        SubVoice targetVoice = new(project) { Name = "Target" };
        SubVoice otherVoice = new(project) { Name = "Other" };
        TemplateEvent templateEvent = TemplateEvent.Program(project, 10, 5);
        sourceVoice.Events.Add(templateEvent);
        ValueCurve sourceCurve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        CurvePoint sourcePoint = new(project, 20, 32);
        Active(project, sourceCurve).Points.Add(sourcePoint);
        Active(project, sourceVoice).Curves.Add(sourceCurve);
        ValueCurve targetCurve = new(project) { Target = MidiValueTarget.ControlChange(1) };
        ValueCurve incompatibleCurve = new(project) { Target = MidiValueTarget.ControlChange(2) };
        Active(project, targetVoice).Curves.AddRange([targetCurve, incompatibleCurve]);
        Active(project, sourceInstrument).SubVoices.AddRange([sourceVoice, targetVoice]);
        Active(project, otherInstrument).SubVoices.Add(otherVoice);
        project.EventInstruments.AddRange([sourceInstrument, otherInstrument]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        ProjectObjectClipboardPayload events =
            ProjectObjectClipboard.CopySubVoiceTimelineEvents(
                document,
                sourceInstrument.Id,
                sourceVoice.Id,
                [templateEvent.Id]);
        ProjectObjectClipboardPayload curveContent =
            ProjectObjectClipboard.CopyValueCurveContent(
                document,
                sourceInstrument.Id,
                sourceVoice.Id,
                sourceCurve.Id,
                [sourcePoint.Id]);
        long beforeInvalidPasteId = project.NextStableId;

        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
                document,
                events,
                otherInstrument.Id,
                otherVoice.Id,
                editCursorTick: 100)));
        Assert.Throws<InvalidOperationException>(() => document.Execute(
            ProjectObjectClipboard.CreatePasteValueCurveContentCommand(
                document,
                curveContent,
                sourceInstrument.Id,
                targetVoice.Id,
                incompatibleCurve.Id,
                editCursorTick: 100)));
        Assert.Equal(beforeInvalidPasteId, project.NextStableId);
        Assert.Empty(document.History);

        document.Execute(ProjectObjectClipboard.CreatePasteValueCurveContentCommand(
            document,
            curveContent,
            sourceInstrument.Id,
            targetVoice.Id,
            targetCurve.Id,
            editCursorTick: 100));
        CurvePoint pasted = Assert.Single(Active(project, targetCurve).Points);
        Assert.Equal((100L, 32d), (pasted.Tick, pasted.Value));
        Assert.NotEqual(sourcePoint.Id, pasted.Id);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void CutPreparationDoesNotDeleteUntilClipboardWriteSucceeds()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment source = new(project) { LengthTicks = 480 };
        Segment target = new(project) { ProjectStartTick = 480, LengthTicks = 480 };
        LogicalNote note = new(project)
        {
            StartTick = 20,
            LengthTicks = 40,
            Note = 60,
            Velocity = 100
        };
        Active(project, source).Notes.Add(note);
        Active(project, track).Segments.AddRange([source, target]);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardCutPreparation cut =
            ProjectObjectClipboard.PrepareCutLogicalNotes(
                document,
                source.Id,
                [note.Id]);

        Assert.Same(note, Assert.Single(Active(project, source).Notes));
        Assert.Empty(document.History);

        document.Execute(cut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, source).Notes);
        Assert.Single(document.History);

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalNotesCommand(
            document,
            cut.Payload,
            target.Id,
            editCursorTick: 100));
        LogicalNote pasted = Assert.Single(Active(project, target).Notes);
        Assert.NotEqual(note.Id, pasted.Id);
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void CompatibleOrderedMappingChainUsesSnapshotAndFreshOwnedIds()
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
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent source = TemplateEvent.ControlChange(project, 10, 1, 64);
        TemplateEvent target = TemplateEvent.ControlChange(project, 20, 2, 64);
        ValueMappingStep step = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.Remap,
            LogicalParameterId = parameter.Id,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        };
        source.ValueMappings.Add(step);
        voice.Events.AddRange([source, target]);
        Active(project, instrument).LogicalParameters.Add(parameter);
        Active(project, instrument).SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        MappingChain originalTarget = target.ValueMappings;

        ProjectObjectClipboardCutPreparation cut =
            ProjectObjectClipboard.PrepareCutMappingChain(
                document,
                instrument.Id,
                source.ValueMappings.Id);
        Assert.Single(source.ValueMappings);
        document.Execute(cut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(source.ValueMappings);

        document.Execute(ProjectObjectClipboard.CreatePasteMappingChainCommand(
            document,
            cut.Payload,
            instrument.Id,
            originalTarget.Id,
            nonEmptyReplacementConfirmed: false));

        MappingChain copy = Active(project, target).ValueMappings;
        ValueMappingStep stepCopy = Assert.Single(copy);
        Assert.NotSame(originalTarget, copy);
        Assert.NotEqual(originalTarget.Id, copy.Id);
        Assert.NotEqual(source.ValueMappings.Id, copy.Id);
        Assert.NotEqual(step.Id, stepCopy.Id);
        Assert.Equal(parameter.Id, stepCopy.LogicalParameterId);
        Assert.Equal(2, document.History.Count);
        AssertMatchesFull(compilation);

        document.Undo();
        Assert.Same(originalTarget, Active(project, target).ValueMappings);
        document.Redo();
        Assert.Same(copy, Active(project, target).ValueMappings);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void EveryRemainingCutKindIsTwoPhaseAndUndoRestoresTheExactSourceObject()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DefaultValue = 0.5
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent templateEvent = TemplateEvent.ControlChange(project, 10, 1, 64);
        ValueCurve curve = new(project)
        {
            Target = MidiValueTarget.ControlChange(2)
        };
        CurvePoint curvePoint = new(project, 20, 32);
        Active(project, curve).Points.Add(curvePoint);
        voice.Events.Add(templateEvent);
        Active(project, voice).Curves.Add(curve);
        Active(project, instrument).LogicalParameters.Add(parameter);
        Active(project, instrument).SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);

        LogicalTrack track = new(project) {
            Name = "Track",
            LastBoundEventInstrumentName = instrument.Name
        };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalParameterLane lane = new(project) { ParameterId = parameter.Id };
        CurvePoint lanePoint = new(project, 30, 0.75);
        Active(project, lane).Points.Add(lanePoint);
        segment.ParameterLanes.Add(lane);
        Active(project, track).Segments.Add(segment);

        ProjectMarker marker = new(project, 120, "Marker");
        project.Conductor.Markers.Add(marker);

        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardCutPreparation laneContent =
            ProjectObjectClipboard.PrepareCutLogicalParameterLaneContent(
                document,
                segment.Id,
                lane.Id,
                [lanePoint.Id]);
        Assert.Equal(ProjectObjectClipboardKind.LogicalParameterLaneContent, laneContent.Payload.Kind);
        Assert.Same(lanePoint, Assert.Single(Active(project, lane).Points));
        document.Execute(laneContent.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, lane).Points);
        document.Undo();
        Assert.Same(lanePoint, Assert.Single(Active(project, lane).Points));

        ProjectObjectClipboardCutPreparation wholeLane =
            ProjectObjectClipboard.PrepareCutLogicalParameterLane(
                document,
                segment.Id,
                lane.Id);
        Assert.Equal(ProjectObjectClipboardKind.LogicalParameterLane, wholeLane.Payload.Kind);
        Assert.Same(lane, Assert.Single(segment.ParameterLanes));
        document.Execute(wholeLane.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(segment.ParameterLanes);
        document.Undo();
        Assert.Same(lane, Assert.Single(segment.ParameterLanes));

        ProjectObjectClipboardCutPreparation timeline =
            ProjectObjectClipboard.PrepareCutSubVoiceTimelineEvents(
                document,
                instrument.Id,
                voice.Id,
                [templateEvent.Id]);
        Assert.Equal(ProjectObjectClipboardKind.SubVoiceTimelineEvents, timeline.Payload.Kind);
        Assert.Same(templateEvent, Assert.Single(voice.Events));
        document.Execute(timeline.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(voice.Events);
        document.Undo();
        Assert.Same(templateEvent, Assert.Single(voice.Events));

        ProjectObjectClipboardCutPreparation curveContent =
            ProjectObjectClipboard.PrepareCutValueCurveContent(
                document,
                instrument.Id,
                voice.Id,
                curve.Id,
                [curvePoint.Id]);
        Assert.Equal(ProjectObjectClipboardKind.ValueCurveContent, curveContent.Payload.Kind);
        Assert.Same(curvePoint, Assert.Single(Active(project, curve).Points));
        document.Execute(curveContent.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, curve).Points);
        document.Undo();
        Assert.Same(curvePoint, Assert.Single(Active(project, curve).Points));

        ProjectObjectClipboardCutPreparation conductor =
            ProjectObjectClipboard.PrepareCutConductorEvents(document, [marker.Id]);
        Assert.Equal(ProjectObjectClipboardKind.ConductorEvents, conductor.Payload.Kind);
        Assert.Same(marker, Assert.Single(project.Conductor.Markers));
        document.Execute(conductor.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(project.Conductor.Markers);
        document.Undo();
        Assert.Same(marker, Assert.Single(project.Conductor.Markers));

        ProjectObjectClipboardCutPreparation segments =
            ProjectObjectClipboard.PrepareCutSegments(document, [segment.Id], segment.Id);
        Assert.Equal(ProjectObjectClipboardKind.Segments, segments.Payload.Kind);
        Assert.Same(segment, Assert.Single(Active(project, track).Segments));
        document.Execute(segments.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, track).Segments);
        document.Undo();
        Assert.Same(segment, Assert.Single(Active(project, track).Segments));

        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InstrumentDefinitionClipboardCreatesIndependentAtomicCopies()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RequiresChannelIsolation = true
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Mode",
            Type = LogicalParameterType.Enum,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1,
            DefaultValue = 0
        };
        parameter.EnumItems.AddRange(
        [
            new(project) { Name = "Off", Value = 0 },
            new(project) { Name = "On", Value = 1 }
        ]);
        InstrumentEnvelope envelope = new(project)
        {
            Name = "Shape",
            AttackTicks = 12,
            PeakValue = 0.8,
            SustainValue = 0.5,
            ReleaseTicks = 24
        };
        CSharpMappingFunction function = new(project)
        {
            Name = "Scale",
            Body = "return value * 2;"
        };
        function.DeclaredContextFields.Add("GateLength");
        Active(project, instrument).LogicalParameters.Add(parameter);
        Active(project, instrument).Envelopes.Add(envelope);
        Active(project, instrument).MappingFunctions.Add(function);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardPayload parameterPayload =
            ProjectObjectClipboard.CopyLogicalParameterDefinition(
                document,
                instrument.Id,
                parameter.Id);
        ProjectObjectClipboardPayload envelopePayload = ProjectObjectClipboard.CopyEnvelopePreset(
            document,
            instrument.Id,
            envelope.Id);
        ProjectObjectClipboardPayload functionPayload = ProjectObjectClipboard.CopyMappingFunction(
            document,
            instrument.Id,
            function.Id);

        document.Execute(ProjectObjectClipboard.CreatePasteLogicalParameterDefinitionCommand(
            document,
            parameterPayload,
            instrument.Id));
        document.Execute(ProjectObjectClipboard.CreatePasteEnvelopePresetCommand(
            document,
            envelopePayload,
            instrument.Id));
        document.Execute(ProjectObjectClipboard.CreatePasteMappingFunctionCommand(
            document,
            functionPayload,
            instrument.Id));

        LogicalParameterDefinition parameterCopy = Active(project, instrument).LogicalParameters[1];
        InstrumentEnvelope envelopeCopy = Active(project, instrument).Envelopes[1];
        CSharpMappingFunction functionCopy = Active(project, instrument).MappingFunctions[1];
        Assert.Equal("Mode Copy 2", parameterCopy.Name);
        Assert.Equal(["Off", "On"], parameterCopy.EnumItems.Select(value => value.Name));
        Assert.NotEqual(parameter.Id, parameterCopy.Id);
        Assert.All(parameterCopy.EnumItems, value =>
            Assert.DoesNotContain(parameter.EnumItems, source => source.Id == value.Id));
        Assert.Equal(("Shape", 12L, 0.8, 0.5, 24L),
            (envelopeCopy.Name, envelopeCopy.AttackTicks, envelopeCopy.PeakValue,
                envelopeCopy.SustainValue, envelopeCopy.ReleaseTicks));
        Assert.NotEqual(envelope.Id, envelopeCopy.Id);
        Assert.Equal("Scale Copy 2", functionCopy.Name);
        Assert.Equal(function.Body, functionCopy.Body);
        Assert.Equal(["GateLength"], functionCopy.DeclaredContextFields);
        Assert.NotEqual(function.Id, functionCopy.Id);
        Assert.Equal(3, document.History.Count);

        document.Undo();
        Assert.Single(Active(project, instrument).MappingFunctions);
        document.Redo();
        Assert.Same(functionCopy, Active(project, instrument).MappingFunctions[1]);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void MappingItemClipboardReplacesConfigurationAndInsertsOneStep()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1
        };
        SubVoice voice = new(project) { Name = "Voice" };
        LogicalParameterMapping source = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(1)
        };
        source.TargetSettings.Rounding = MappingRounding.Floor;
        source.TargetSettings.Overflow = MappingOverflow.Clamp;
        ValueMappingStep sourceStep = new(project)
        {
            Source = MappingSource.LogicalParameter,
            Operation = MappingOperation.Remap,
            LogicalParameterId = parameter.Id,
            SourceMinimum = 0,
            SourceMaximum = 1,
            TargetMinimum = 0,
            TargetMaximum = 127
        };
        Active(project, source).Steps.Add(sourceStep);
        LogicalParameterMapping target = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = MidiValueTarget.ControlChange(7)
        };
        ValueMappingStep oldTargetStep = new(project);
        Active(project, target).Steps.Add(oldTargetStep);
        Active(project, instrument).LogicalParameters.Add(parameter);
        Active(project, instrument).SubVoices.Add(voice);
        Active(project, instrument).ParameterMappings.AddRange([source, target]);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);
        MappingChain oldTargetChain = Active(project, target).Steps;

        ProjectObjectClipboardPayload mappingPayload =
            ProjectObjectClipboard.CopyLogicalParameterMapping(
                document,
                instrument.Id,
                source.Id);
        document.Execute(ProjectObjectClipboard.CreatePasteLogicalParameterMappingCommand(
            document,
            mappingPayload,
            instrument.Id,
            target.Id,
            nonEmptyReplacementConfirmed: true));

        Assert.Equal(MidiValueTarget.ControlChange(7), Active(project, target).Target);
        Assert.Equal(MappingRounding.Floor, Active(project, target).TargetSettings.Rounding);
        Assert.Equal(MappingOverflow.Clamp, Active(project, target).TargetSettings.Overflow);
        ValueMappingStep replacedStep = Assert.Single(Active(project, target).Steps);
        Assert.NotSame(sourceStep, replacedStep);
        Assert.Equal(parameter.Id, replacedStep.LogicalParameterId);
        Assert.NotSame(oldTargetChain, Active(project, target).Steps);

        ProjectObjectClipboardPayload stepPayload = ProjectObjectClipboard.CopyMappingStep(
            document,
            instrument.Id,
            Active(project, source).Steps.Id,
            sourceStep.Id);
        document.Execute(ProjectObjectClipboard.CreatePasteMappingStepCommand(
            document,
            stepPayload,
            instrument.Id,
            Active(project, target).Steps.Id,
            insertionIndex: 1));

        ValueMappingStep inserted = Active(project, target).Steps[1];
        Assert.NotEqual(sourceStep.Id, inserted.Id);
        Assert.Equal(sourceStep.Operation, inserted.Operation);
        Assert.Equal(2, Active(project, target).Steps.Count);

        ProjectObjectClipboardCutPreparation stepCut = ProjectObjectClipboard.PrepareCutMappingStep(
            document,
            instrument.Id,
            Active(project, target).Steps.Id,
            inserted.Id);
        Assert.Same(inserted, Active(project, target).Steps[1]);
        document.Execute(stepCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Single(Active(project, target).Steps);
        document.Undo();
        Assert.Same(inserted, Active(project, target).Steps[1]);
        document.Undo();
        Assert.Single(Active(project, target).Steps);
        document.Undo();
        Assert.Same(oldTargetChain, Active(project, target).Steps);
        Assert.Same(instrument, Active(project, instrument));
        document.Redo();
        Assert.NotSame(oldTargetChain, Active(project, target).Steps);
        AssertMatchesFull(compilation);
    }

    [Fact]
    public void InstrumentDefinitionCutsAreTwoPhaseAndUndoRestoreExactObjects()
    {
        MidoraProject project = new(480);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RequiresChannelIsolation = true
        };
        LogicalParameterDefinition parameter = new(project)
        {
            Name = "Amount",
            Type = LogicalParameterType.Double,
            Minimum = 0,
            Maximum = 1,
            DisplayMinimum = 0,
            DisplayMaximum = 1
        };
        InstrumentEnvelope envelope = new(project) { Name = "Shape" };
        CSharpMappingFunction function = new(project)
        {
            Name = "Identity",
            Body = "value"
        };
        Active(project, instrument).LogicalParameters.Add(parameter);
        Active(project, instrument).Envelopes.Add(envelope);
        Active(project, instrument).MappingFunctions.Add(function);
        project.EventInstruments.Add(instrument);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = PersistedDocument(compilation);

        ProjectObjectClipboardCutPreparation parameterCut =
            ProjectObjectClipboard.PrepareCutLogicalParameterDefinition(
                document,
                instrument.Id,
                parameter.Id);
        Assert.Same(parameter, Assert.Single(Active(project, instrument).LogicalParameters));
        document.Execute(parameterCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, instrument).LogicalParameters);
        document.Undo();
        Assert.Same(parameter, Assert.Single(Active(project, instrument).LogicalParameters));

        ProjectObjectClipboardCutPreparation envelopeCut =
            ProjectObjectClipboard.PrepareCutEnvelopePreset(
                document,
                instrument.Id,
                envelope.Id);
        Assert.Same(envelope, Assert.Single(Active(project, instrument).Envelopes));
        document.Execute(envelopeCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, instrument).Envelopes);
        document.Undo();
        Assert.Same(envelope, Assert.Single(Active(project, instrument).Envelopes));

        ProjectObjectClipboardCutPreparation functionCut =
            ProjectObjectClipboard.PrepareCutMappingFunction(
                document,
                instrument.Id,
                function.Id);
        Assert.Same(function, Assert.Single(Active(project, instrument).MappingFunctions));
        document.Execute(functionCut.DeleteAfterSuccessfulClipboardWrite);
        Assert.Empty(Active(project, instrument).MappingFunctions);
        document.Undo();
        Assert.Same(function, Assert.Single(Active(project, instrument).MappingFunctions));
        Assert.False(document.IsModified);
        AssertMatchesFull(compilation);
    }

    private static ProjectDocumentSession PersistedDocument(ProjectCompilationSession compilation)
    {
        ProjectDocumentSession result = new(compilation, ProjectDocumentOrigin.Persisted);
        result.MarkSaveSucceeded();
        return result;
    }

    private static void AddIndependentLogicalTracks(
        MidoraProject project,
        params LogicalTrack[] tracks)
    {
        EventInstrument instrument = EventInstrumentLibrary.Create(project, "Instrument");
        foreach (LogicalTrack track in tracks)
        {
            ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        }
    }

    private static void AssertMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult expected = compiler.CompileFull(compilation.Project);
        Assert.Equal(expected.Fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(expected.IsConsumable, compilation.LastAttempt.IsConsumable);
    }
    // Commands publish immutable owner roots. Identity remains the stable ID,
    // so every post-edit observation resolves the current formal owner.
    private static T Active<T>(MidoraProject project, T value) where T : class => (value switch
    {
        EventInstrument owner => (object?)project.EventInstruments.FirstOrDefault(item => item.Id == owner.Id),
        LogicalTrack owner => project.Tracks.FirstOrDefault(item => item.Id == owner.Id),
        PureMidiTrack owner => project.PureMidiTracks.FirstOrDefault(item => item.Id == owner.Id),
        Segment owner => project.Tracks.SelectMany(item => item.Segments).FirstOrDefault(item => item.Id == owner.Id),
        MidiSegment owner => project.PureMidiTracks.SelectMany(item => item.Segments).FirstOrDefault(item => item.Id == owner.Id),
        SubVoice owner => project.EventInstruments.SelectMany(item => item.SubVoices).FirstOrDefault(item => item.Id == owner.Id),
        LogicalParameterMapping owner => project.EventInstruments.SelectMany(item => item.ParameterMappings)
            .FirstOrDefault(item => item.Id == owner.Id),
        TemplateEvent owner => project.EventInstruments.SelectMany(item => item.SubVoices)
            .SelectMany(item => item.Events).FirstOrDefault(item => item.Id == owner.Id),
        LogicalParameterLane owner => project.Tracks.SelectMany(item => item.Segments).SelectMany(item => item.ParameterLanes)
            .FirstOrDefault(item => item.Id == owner.Id),
        ValueCurve owner => project.EventInstruments.SelectMany(item => item.SubVoices).SelectMany(item => item.Curves)
            .FirstOrDefault(item => item.Id == owner.Id),
        _ => null
    }) as T ?? value;

}
