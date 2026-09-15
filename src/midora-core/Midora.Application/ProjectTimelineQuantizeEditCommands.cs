using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static ITimelineSelectionResultEditCommand QuantizeLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteQuantizeOptions options) =>
        ResultCommand("Quantize logical notes", (project, publishResult, cancellationToken, progress) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            SegmentLocation location = FindSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                long frozenStep = ResolveQuantizeStep(project, options.Grid);
                return PrepareBoundedLogicalNotes(project, location, noteIds, _ => value =>
                {
                    var result = QuantizeSegmentNote(new(value.StartTick, value.LengthTicks, value.Note, value.Velocity),
                        location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick, frozenStep, options.Mode);
                    return result.Discard ? null : value with { StartTick = result.Value.StartTick, LengthTicks = result.Value.LengthTicks };
                }, publishResult: publishResult, formalCollisions: true);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedLogicalNote[] selected = SelectAdvancedLogicalNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            long step = ResolveQuantizeStep(project, options.Grid);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            AdvancedLogicalNoteEdit[] edits = new AdvancedLogicalNoteEdit[selected.Length];
            for (int index = 0; index < selected.Length; index++)
            {
                PollCancellation(cancellationToken, index);
                ReportQuantizeLoopProgress(progress, TimelineEditPreparationPhase.Planning, index, selected.Length);
                AdvancedLogicalNote value = selected[index];
                QuantizedNote quantized = QuantizeSegmentNote(
                    ToAdvanced(value.Old),
                    location.Segment.ProjectStartTick,
                    location.Segment.ContentOffsetTick,
                    step,
                    options.Mode);
                edits[index] = new(value, FromAdvancedLogical(quantized.Value), quantized.Discard);
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveLogicalNoteCollisions(location.Segment, edits, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, edits.Length);
            IPreparedProjectEdit prepared = PrepareLogicalAdvancedValueEdit(
                project,
                location.Track,
                location.Segment,
                edits,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand QuantizeDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteQuantizeOptions options) =>
        ResultCommand("Quantize Direct MIDI notes", (project, publishResult, cancellationToken, progress) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (noteIds.Count > 4096 || location.Segment.Notes.Count > 4096)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                long frozenStep = ResolveQuantizeStep(project, options.Grid);
                return PrepareBoundedDirectMidiNoteTransform(project, segmentId, noteIds, _ => value =>
                {
                    var result = QuantizeSegmentNote(new(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity),
                        location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick, frozenStep, options.Mode);
                    return result.Discard ? null : value with { StartTick = result.Value.StartTick,
                        LengthTicks = result.Value.LengthTicks };
                }, formalCollisions: true, publishResult: publishResult);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedDirectNote[] selected = SelectAdvancedDirectNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            long step = ResolveQuantizeStep(project, options.Grid);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            AdvancedDirectNoteEdit[] edits = new AdvancedDirectNoteEdit[selected.Length];
            for (int index = 0; index < selected.Length; index++)
            {
                PollCancellation(cancellationToken, index);
                ReportQuantizeLoopProgress(progress, TimelineEditPreparationPhase.Planning, index, selected.Length);
                AdvancedDirectNote value = selected[index];
                QuantizedNote quantized = QuantizeSegmentNote(
                    ToAdvanced(value.Old),
                    location.Segment.ProjectStartTick,
                    location.Segment.ContentOffsetTick,
                    step,
                    options.Mode);
                edits[index] = new(
                    value,
                    FromAdvancedDirect(quantized.Value, value.Old),
                    quantized.Discard);
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveDirectNoteCollisions(location.Segment, edits, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, edits.Length);
            IPreparedProjectEdit prepared = PrepareDirectAdvancedValueEdit(
                project,
                location.Track,
                location.Segment,
                edits,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand QuantizeTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteQuantizeOptions options) =>
        ResultCommand("Quantize SubVoice notes", (project, publishResult, cancellationToken, progress) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                long frozenStep = ResolveQuantizeStep(project, options.Grid);
                return PrepareBoundedTemplateNotes(project, instrument, voice, noteIds, _ => value =>
                {
                    var result = QuantizeTemplateNote(new(value.Tick, value.LengthTicks, value.Number, value.Value),
                        instrument.TemplateLengthTicks, frozenStep, options.Mode);
                    return result.Discard ? null : value with { Tick = result.Value.StartTick, LengthTicks = result.Value.LengthTicks };
                }, publishResult: publishResult, formalCollisions: true);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            AdvancedTemplateNote[] selected = SelectAdvancedTemplateNotes(voice, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            long step = ResolveQuantizeStep(project, options.Grid);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            AdvancedTemplateNoteEdit[] edits = new AdvancedTemplateNoteEdit[selected.Length];
            for (int index = 0; index < selected.Length; index++)
            {
                PollCancellation(cancellationToken, index);
                ReportQuantizeLoopProgress(progress, TimelineEditPreparationPhase.Planning, index, selected.Length);
                AdvancedTemplateNote value = selected[index];
                QuantizedNote quantized = QuantizeTemplateNote(
                    ToAdvanced(value.Old),
                    instrument.TemplateLengthTicks,
                    step,
                    options.Mode);
                edits[index] = new(
                    value,
                    FromAdvancedTemplate(quantized.Value, value.Old),
                    quantized.Discard);
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveTemplateNoteCollisions(voice, edits, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, edits.Length);
            IPreparedProjectEdit prepared = PrepareTemplateAdvancedValueEdit(
                project,
                instrument,
                voice,
                edits,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand QuantizeLogicalParameterPoints(
        MidoraId segmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        TimelineQuantizeGrid grid) => QuantizeLogicalParameterPointsCore(
            segmentId,
            laneId,
            pointIds,
            grid);

    /// <summary>
    /// Quantizes Logical Parameter points selected from any number of lanes in
    /// one Segment. Collisions are resolved independently in each lane and the
    /// complete cross-lane result is published as one prepared Project edit.
    /// </summary>
    public static ITimelineSelectionResultEditCommand QuantizeLogicalParameterPoints(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> pointIds,
        TimelineQuantizeGrid grid) => QuantizeLogicalParameterPointsCore(
            segmentId,
            laneId: null,
            pointIds,
            grid);

    private static ITimelineSelectionResultEditCommand QuantizeLogicalParameterPointsCore(
        MidoraId segmentId,
        MidoraId? laneId,
        IReadOnlyCollection<MidoraId> pointIds,
        TimelineQuantizeGrid grid) =>
        pointIds.Count >= BoundedPointThreshold
        ? BoundedQuantizeLogicalPoints(segmentId, laneId, pointIds, grid)
        :
        ResultCommand("Quantize logical parameter points", (project, publishResult, cancellationToken, progress) =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            if (location.Segment.ParameterLanes.Any(lane => (laneId is null || lane.Id == laneId) && lane.Points.Count >= BoundedPointThreshold))
                return ForwardBoundedSelection(BoundedQuantizeLogicalPoints(segmentId, laneId, pointIds, grid), project, publishResult, cancellationToken, progress);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            IReadOnlySet<MidoraId> requested = ValidateTimelineSelectionIds(
                pointIds,
                nameof(pointIds),
                "Logical Parameter point");
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, requested.Count);
            IReadOnlyList<LogicalParameterLane> candidateLanes = laneId is MidoraId requestedLaneId
                ? [FindLogicalParameterLane(location.Segment, requestedLaneId)]
                : location.Segment.ParameterLanes;
            List<QuantizedCurvePointLaneEdit> laneEdits = [];
            HashSet<MidoraId> resolvedIds = [];
            int resolutionIndex = 0;
            foreach (LogicalParameterLane lane in candidateLanes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<(int Index, CurvePoint Value)> resolved =
                    lane.Points.ResolveByIdsWithIndicesInCollectionOrder(requested);
                if (resolved.Count == 0)
                    continue;
                QuantizedCurvePointEdit[] edits = new QuantizedCurvePointEdit[resolved.Count];
                for (int index = 0; index < resolved.Count; index++)
                {
                    PollCancellation(cancellationToken, resolutionIndex++);
                    (int ordinal, CurvePoint point) = resolved[index];
                    if (!resolvedIds.Add(point.Id))
                    {
                        throw new InvalidOperationException(
                            "A Logical Parameter point stable ID appears in more than one lane.");
                    }
                    edits[index] = new(point, ordinal);
                }
                laneEdits.Add(new(lane, edits));
            }
            if (resolvedIds.Count != requested.Count)
            {
                string target = laneId.HasValue
                    ? "the target Logical Parameter lane"
                    : "a Logical Parameter lane in the target Segment";
                throw new ArgumentException(
                    $"Every selected ID must identify a point in {target}.",
                    nameof(pointIds));
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, resolvedIds.Count, requested.Count);
            long step = ResolveQuantizeStep(project, grid);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, resolvedIds.Count);
            int editIndex = 0;
            foreach (QuantizedCurvePointLaneEdit laneEdit in laneEdits)
            {
                foreach (QuantizedCurvePointEdit edit in laneEdit.Edits)
                {
                    PollCancellation(cancellationToken, editIndex++);
                    ReportQuantizeLoopProgress(
                        progress,
                        TimelineEditPreparationPhase.Planning,
                        editIndex,
                        resolvedIds.Count);
                    CurvePoint old = edit.Old;
                    long absolute = SegmentLocalToAbsolute(
                        old.Tick,
                        location.Segment.ProjectStartTick,
                        location.Segment.ContentOffsetTick);
                    long tick = SegmentAbsoluteToLocal(
                        SnapProjectAbsolute(absolute, step),
                        location.Segment.ProjectStartTick,
                        location.Segment.ContentOffsetTick);
                    if (tick < 0)
                    {
                        edit.Discard = true;
                        continue;
                    }
                    edit.Replacement = new CurvePoint(
                        project,
                        old.Id,
                        tick,
                        old.Value,
                        CurveInterpolation.Step);
                }
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, resolvedIds.Count, resolvedIds.Count);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, resolvedIds.Count);
            foreach (QuantizedCurvePointLaneEdit laneEdit in laneEdits)
            {
                ResolveCurvePointQuantizeCollisions(
                    laneEdit.Lane.Points,
                    laneEdit.Edits,
                    cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, resolvedIds.Count, resolvedIds.Count);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, resolvedIds.Count);
            IPreparedProjectEdit prepared = PrepareCurvePointQuantize(
                project,
                location.Track,
                location.Segment,
                laneEdits,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand QuantizeDirectMidiEvents(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        TimelineQuantizeGrid grid) =>
        ResultCommand("Quantize Direct MIDI events", (project, publishResult, cancellationToken, progress) =>
        {
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (eventIds.Count > 4096 || location.Segment.ChannelEvents.Count > 4096)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                long frozenStep = ResolveQuantizeStep(project, grid);
                return PrepareBoundedDirectMidiEventTransform(project, segmentId, eventIds, _ => value =>
                {
                    if (value.Kind is DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff)
                        throw new ArgumentException("Select non-note MIDI Events only.", nameof(eventIds));
                    long absolute = SegmentLocalToAbsolute(value.Tick, location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick);
                    long tick = SegmentAbsoluteToLocal(SnapProjectAbsolute(absolute, frozenStep),
                        location.Segment.ProjectStartTick, location.Segment.ContentOffsetTick);
                    return tick < 0 ? null : value with { Tick = tick };
                }, BoundedEventCollisionMode.FormalLatest, publishResult);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            IReadOnlySet<MidoraId> requested = ValidateTimelineSelectionIds(
                eventIds,
                nameof(eventIds),
                "Direct MIDI Event");
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, requested.Count);
            QuantizedDirectEventEdit[] edits = location.Segment.ChannelEvents.ResolveByIds(requested)
                .OrderBy(static value => value.Value.Order)
                .ThenBy(static value => value.Value.Id)
                .Select(static value => new QuantizedDirectEventEdit(
                    value.Value,
                    new AdvancedFormalOrder(value.Value.Order, value.Value.Id),
                    SnapshotDirectEvent(value.Value)))
                .ToArray();
            if (edits.Length != requested.Count
                || edits.Any(static value => value.Old.Kind is
                    DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a non-note Direct MIDI Event in the target Segment.",
                    nameof(eventIds));
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, edits.Length, requested.Count);
            long step = ResolveQuantizeStep(project, grid);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, edits.Length);
            for (int index = 0; index < edits.Length; index++)
            {
                PollCancellation(cancellationToken, index);
                ReportQuantizeLoopProgress(progress, TimelineEditPreparationPhase.Planning, index, edits.Length);
                DirectMidiEventValue old = edits[index].Old;
                long absolute = SegmentLocalToAbsolute(
                    old.Tick,
                    location.Segment.ProjectStartTick,
                    location.Segment.ContentOffsetTick);
                long tick = SegmentAbsoluteToLocal(
                    SnapProjectAbsolute(absolute, step),
                    location.Segment.ProjectStartTick,
                    location.Segment.ContentOffsetTick);
                if (tick < 0)
                {
                    edits[index].Discard = true;
                    continue;
                }
                edits[index].Replacement = old with { Tick = tick };
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveDirectEventQuantizeCollisions(
                location.Segment.ChannelEvents,
                edits,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, edits.Length);
            IPreparedProjectEdit prepared = PrepareDirectEventQuantize(
                project,
                location.Track,
                location.Segment,
                edits,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand QuantizeTemplateEvents(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> eventIds,
        TimelineQuantizeGrid grid) =>
        eventIds.Count >= BoundedPointThreshold
        ? BoundedTemplatePoints("Quantize SubVoice events", eventInstrumentId, subVoiceId,
            eventIds, BoundedPointOperation.Quantize, grid: grid)
        :
        ResultCommand("Quantize SubVoice events", (project, publishResult, cancellationToken, progress) =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (voice.Events.Count >= BoundedPointThreshold)
                return ForwardBoundedSelection(BoundedTemplatePoints("Quantize SubVoice events", eventInstrumentId, subVoiceId, eventIds, BoundedPointOperation.Quantize, grid: grid), project, publishResult, cancellationToken, progress);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            IReadOnlySet<MidoraId> requested = ValidateTimelineSelectionIds(
                eventIds,
                nameof(eventIds),
                "Template Event");
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, requested.Count);
            QuantizedTemplateEventEdit[] edits = voice.Events
                .ResolveByIdsWithIndicesInCollectionOrder(requested)
                .Select(static value => new QuantizedTemplateEventEdit(
                    value.Value,
                    value.Index,
                    CaptureTemplateEvent(value.Value)))
                .ToArray();
            if (edits.Length != requested.Count
                || edits.Any(static value => value.Old.Kind == TemplateEventKind.Note))
            {
                throw new ArgumentException(
                    "Every selected ID must identify a non-note Template Event in the target SubVoice.",
                    nameof(eventIds));
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, edits.Length, requested.Count);
            long step = ResolveQuantizeStep(project, grid);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, edits.Length);
            for (int index = 0; index < edits.Length; index++)
            {
                PollCancellation(cancellationToken, index);
                ReportQuantizeLoopProgress(progress, TimelineEditPreparationPhase.Planning, index, edits.Length);
                TemplateEventValue old = edits[index].Old;
                long tick = SnapNonnegative(old.Tick, step);
                if (tick >= instrument.TemplateLengthTicks)
                {
                    edits[index].Discard = true;
                    continue;
                }
                edits[index].Replacement = old with { Tick = tick };
            }
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveTemplateEventQuantizeCollisions(voice, edits, cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, edits.Length, edits.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, edits.Length);
            IPreparedProjectEdit prepared = PrepareTemplateEventQuantize(
                project,
                instrument,
                voice,
                edits,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    private static long ResolveQuantizeStep(MidoraProject project, TimelineQuantizeGrid grid)
    {
        if (!Enum.IsDefined(grid.Kind)) throw new ArgumentOutOfRangeException(nameof(grid));
        if (grid.Kind == TimelineQuantizeGridKind.CustomTicks)
        {
            if (grid.CustomTicks < 1) throw new ArgumentOutOfRangeException(nameof(grid));
            return grid.CustomTicks;
        }
        if (grid.Numerator < 1 || grid.Denominator < 1)
            throw new ArgumentOutOfRangeException(nameof(grid));
        return ProjectTimelineGrid.ResolveWholeNoteFractionStep(
            project.TicksPerQuarterNote,
            grid.Numerator,
            grid.Denominator);
    }

    private static QuantizedNote QuantizeSegmentNote(
        AdvancedNoteValue old,
        long projectStartTick,
        long contentOffsetTick,
        long step,
        NoteQuantizeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        long absoluteStart = SegmentLocalToAbsolute(old.StartTick, projectStartTick, contentOffsetTick);
        long snappedStart = SnapProjectAbsolute(absoluteStart, step);
        long localStart = SegmentAbsoluteToLocal(snappedStart, projectStartTick, contentOffsetTick);
        if (localStart < 0) return new(old, true);
        long length = old.LengthTicks;
        if (mode == NoteQuantizeMode.StartAndEnd)
        {
            long localEnd = checked(old.StartTick + old.LengthTicks);
            long absoluteEnd = SegmentLocalToAbsolute(localEnd, projectStartTick, contentOffsetTick);
            long snappedEnd = SnapProjectAbsolute(absoluteEnd, step);
            if (snappedEnd <= snappedStart) snappedEnd = checked(snappedStart + 1);
            length = checked(snappedEnd - snappedStart);
        }
        _ = checked(localStart + length);
        return new(old with { StartTick = localStart, LengthTicks = length }, false);
    }

    private static QuantizedNote QuantizeTemplateNote(
        AdvancedNoteValue old,
        long templateLengthTicks,
        long step,
        NoteQuantizeMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        long start = SnapNonnegative(old.StartTick, step);
        if (start < 0 || start >= templateLengthTicks) return new(old, true);
        long length = old.LengthTicks;
        if (mode == NoteQuantizeMode.StartAndEnd)
        {
            long end = SnapNonnegative(checked(old.StartTick + old.LengthTicks), step);
            if (end <= start) end = checked(start + 1);
            length = checked(end - start);
            length = Math.Min(length, checked(templateLengthTicks - start));
        }
        else if (length > checked(templateLengthTicks - start))
        {
            // Start-only quantization is not allowed to rewrite Gate. Moving the
            // unchanged Note beyond the Template's hard end therefore discards
            // the Note under the normal hard-boundary rule.
            return new(old, true);
        }
        if (length < 1) return new(old, true);
        return new(old with { StartTick = start, LengthTicks = length }, false);
    }

    private static long SegmentLocalToAbsolute(
        long localTick,
        long projectStartTick,
        long contentOffsetTick) =>
        checked(projectStartTick + checked(localTick - contentOffsetTick));

    private static long SegmentAbsoluteToLocal(
        long absoluteTick,
        long projectStartTick,
        long contentOffsetTick) =>
        checked(contentOffsetTick + checked(absoluteTick - projectStartTick));

    private static long SnapNonnegative(long tick, long step) =>
        ProjectTimelineGrid.Snap(tick, step, tieDirection: 0);

    private static long SnapProjectAbsolute(long tick, long step) =>
        ProjectTimelineGrid.SnapSigned(tick, step, tieDirection: 0);

    private static void ResolveCurvePointQuantizeCollisions(
        CurvePointCollection points,
        QuantizedCurvePointEdit[] edits,
        CancellationToken cancellationToken)
    {
        HashSet<long> targets = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedCurvePointEdit edit = edits[index];
            if (!edit.Discard)
                targets.Add(edit.Replacement!.Tick);
        }
        if (targets.Count == 0) return;
        Dictionary<CurvePoint, QuantizedCurvePointEdit> selected = [];
        Dictionary<long, (int Ordinal, QuantizedCurvePointEdit? Edit, CurvePoint? Point)>
            winners = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedCurvePointEdit edit = edits[index];
            selected.Add(edit.Old, edit);
            if (edit.Discard || !targets.Contains(edit.Replacement!.Tick)) continue;
            long key = edit.Replacement.Tick;
            if (!winners.TryGetValue(key, out var winner) || edit.Ordinal > winner.Ordinal)
                winners[key] = (edit.Ordinal, edit, null);
        }
        HashSet<MidoraId> currentIds = [];
        int queryIndex = 0;
        foreach (CurvePointSnapshotValue value in points.CreateQuerySnapshot().QueryTicks(targets))
        {
            PollCancellation(cancellationToken, queryIndex++);
            currentIds.Add(value.Id);
        }
        IReadOnlyList<(int Index, CurvePoint Value)> current =
            points.ResolveByIdsWithIndicesInCollectionOrder(currentIds);
        for (int index = 0; index < current.Count; index++)
        {
            PollCancellation(cancellationToken, index);
            (int ordinal, CurvePoint value) = current[index];
            if (selected.ContainsKey(value)) continue;
            if (!winners.TryGetValue(value.Tick, out var winner)
                || ordinal > winner.Ordinal)
                winners[value.Tick] = (ordinal, null, value);
        }

        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedCurvePointEdit edit = edits[index];
            if (edit.Discard || !targets.Contains(edit.Replacement!.Tick)) continue;
            if (!ReferenceEquals(winners[edit.Replacement.Tick].Edit, edit))
                edit.Discard = true;
        }

        QuantizedCurvePointEdit displacementOwner = edits[0];
        for (int index = 0; index < current.Count; index++)
        {
            PollCancellation(cancellationToken, index);
            CurvePoint value = current[index].Value;
            if (selected.ContainsKey(value)) continue;
            if (!ReferenceEquals(winners[value.Tick].Point, value))
                displacementOwner.Displaced.Add(value);
        }
    }

    private static void ResolveDirectEventQuantizeCollisions(
        DirectMidiChannelEventCollection events,
        QuantizedDirectEventEdit[] edits,
        CancellationToken cancellationToken)
    {
        HashSet<DirectMidiEventStartKey> targets = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedDirectEventEdit edit = edits[index];
            if (!edit.Discard)
                targets.Add(DirectEventKey(edit.Replacement));
        }
        if (targets.Count == 0) return;
        Dictionary<DirectMidiChannelEvent, QuantizedDirectEventEdit> selected = [];
        Dictionary<DirectMidiEventStartKey,
            (AdvancedFormalOrder FormalOrder, QuantizedDirectEventEdit? Edit, DirectMidiChannelEvent? Event)> winners = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedDirectEventEdit edit = edits[index];
            selected.Add(edit.Event, edit);
            if (edit.Discard) continue;
            DirectMidiEventStartKey key = DirectEventKey(edit.Replacement);
            if (!targets.Contains(key)) continue;
            if (!winners.TryGetValue(key, out var winner)
                || edit.FormalOrder.CompareTo(winner.FormalOrder) > 0)
            {
                winners[key] = (edit.FormalOrder, edit, null);
            }
        }

        MidoraId[] currentIds = events.QueryStartKeys(targets)
            .Select(static value => value.Id)
            .Distinct()
            .ToArray();
        IReadOnlyList<DirectMidiChannelEventMatch> current = events.ResolveByIds(currentIds);
        for (int index = 0; index < current.Count; index++)
        {
            PollCancellation(cancellationToken, index);
            DirectMidiChannelEventMatch match = current[index];
            DirectMidiChannelEvent value = match.Value;
            if (selected.ContainsKey(value)) continue;
            DirectMidiEventStartKey key = DirectEventKey(SnapshotDirectEvent(value));
            AdvancedFormalOrder formalOrder = new(value.Order, value.Id);
            if (!winners.TryGetValue(key, out var winner)
                || formalOrder.CompareTo(winner.FormalOrder) > 0)
            {
                winners[key] = (formalOrder, null, value);
            }
        }

        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedDirectEventEdit edit = edits[index];
            DirectMidiEventStartKey key = DirectEventKey(edit.Replacement);
            if (edit.Discard || !targets.Contains(key)) continue;
            if (!ReferenceEquals(winners[key].Edit, edit)) edit.Discard = true;
        }

        QuantizedDirectEventEdit displacementOwner = edits[0];
        for (int index = 0; index < current.Count; index++)
        {
            PollCancellation(cancellationToken, index);
            DirectMidiChannelEvent value = current[index].Value;
            if (selected.ContainsKey(value)) continue;
            DirectMidiEventStartKey key = DirectEventKey(SnapshotDirectEvent(value));
            if (!ReferenceEquals(winners[key].Event, value))
                displacementOwner.Displaced.Add(value);
        }
    }

    private static void ResolveTemplateEventQuantizeCollisions(
        SubVoice voice,
        QuantizedTemplateEventEdit[] edits,
        CancellationToken cancellationToken)
    {
        HashSet<QuantizedTemplatePointKey> targets = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            QuantizedTemplateEventEdit edit = edits[index];
            if (edit.Discard) continue;
            foreach (QuantizedTemplatePointKey key in TemplateEventKeys(edit.Replacement))
                targets.Add(key);
        }
        if (targets.Count == 0) return;
        HashSet<long> targetTicks = [];
        int targetIndex = 0;
        foreach (QuantizedTemplatePointKey key in targets)
        {
            PollCancellation(cancellationToken, targetIndex++);
            targetTicks.Add(key.Tick);
        }
        HashSet<MidoraId> ids = [];
        int queryIndex = 0;
        foreach (TemplateEventSnapshotValue value in voice.Events.CreateQuerySnapshot()
            .QueryEventTicks(targetTicks))
        {
            PollCancellation(cancellationToken, queryIndex++);
            ids.Add(value.Id);
        }
        IReadOnlyList<(int Index, TemplateEvent Value)> current =
            voice.Events.ResolveByIdsWithIndicesInCollectionOrder(ids);
        HashSet<TemplateEvent> selected = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollCancellation(cancellationToken, index);
            selected.Add(edits[index].Event);
        }
        HashSet<QuantizedTemplatePointKey> claimed = [];
        int editIndex = edits.Length - 1;
        int currentIndex = current.Count - 1;
        int resolvedIndex = 0;
        while (editIndex >= 0 || currentIndex >= 0)
        {
            PollCancellation(cancellationToken, resolvedIndex++);
            while (editIndex >= 0)
            {
                QuantizedTemplateEventEdit candidate = edits[editIndex];
                if (!candidate.Discard
                    && TemplateEventKeys(candidate.Replacement).Any(targets.Contains))
                    break;
                editIndex--;
            }
            while (currentIndex >= 0)
            {
                TemplateEvent candidate = current[currentIndex].Value;
                if (!selected.Contains(candidate)
                    && TemplateEventKeys(CaptureTemplateEvent(candidate)).Any(targets.Contains))
                    break;
                currentIndex--;
            }

            bool useEdit = editIndex >= 0
                && (currentIndex < 0 || edits[editIndex].Ordinal > current[currentIndex].Index);
            if (useEdit)
            {
                QuantizedTemplateEventEdit edit = edits[editIndex--];
                QuantizedTemplatePointKey[] keys = TemplateEventKeys(edit.Replacement)
                    .Where(targets.Contains)
                    .ToArray();
                if (keys.Any(claimed.Contains)) edit.Discard = true;
                else claimed.UnionWith(keys);
            }
            else if (currentIndex >= 0)
            {
                TemplateEvent value = current[currentIndex--].Value;
                QuantizedTemplatePointKey[] keys = TemplateEventKeys(
                        CaptureTemplateEvent(value))
                    .Where(targets.Contains)
                    .ToArray();
                if (keys.Any(claimed.Contains)) edits[0].Displaced.Add(value);
                else claimed.UnionWith(keys);
            }
        }
    }

    private static IPreparedProjectEdit PrepareCurvePointQuantize(
        MidoraProject project,
        LogicalTrack track,
        Segment segment,
        IReadOnlyList<QuantizedCurvePointLaneEdit> laneEdits,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        List<MidoraId> original = [];
        List<MidoraId> result = [];
        bool changed = false;
        int itemIndex = 0;
        for (int laneIndex = 0; laneIndex < laneEdits.Count; laneIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuantizedCurvePointLaneEdit laneEdit = laneEdits[laneIndex];
            foreach (QuantizedCurvePointEdit edit in laneEdit.Edits)
            {
                PollCancellation(cancellationToken, itemIndex++);
                original.Add(edit.Old.Id);
                if (!edit.Discard)
                    result.Add(edit.Old.Id);
                changed |= edit.Discard || edit.Old != edit.Replacement;
                if (edit.Displaced.Count != 0) changed = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        MidoraId[] originalSelection = original.ToArray();
        MidoraId[] resultSelection = result.ToArray();
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(originalSelection);
        if (!changed)
        {
            return WithSelectionPublication(
                ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(
                    project,
                    track,
                    segment,
                    TrackChange(track.Id),
                    sourceStamp),
                frozenOriginal,
                resultSelection,
                publishResult);
        }

        Segment replacementRoot = ProjectTimelineOwnerRootClone.CloneLogicalSegment(
            project,
            segment,
            cancellationToken);
        Dictionary<MidoraId, LogicalParameterLane> cloneLanes = replacementRoot.ParameterLanes
            .ToDictionary(static value => value.Id);
        foreach (QuantizedCurvePointLaneEdit laneEdit in laneEdits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogicalParameterLane cloneLane = cloneLanes[laneEdit.Lane.Id];
            CurvePoint[] discarded = laneEdit.Edits
                .Where(static value => value.Discard)
                .Select(static value => value.Old)
                .Concat(laneEdit.Edits.SelectMany(static value => value.Displaced))
                .Distinct()
                .ToArray();
            MidoraId[] affectedIds = laneEdit.Edits.Select(static value => value.Old.Id)
                .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
            Dictionary<MidoraId, CurvePoint> cloneById = cloneLane.Points
                .ResolveByIdsInCollectionOrder(affectedIds)
                .ToDictionary(static value => value.Id);
            List<CurvePoint> retainedOld = [];
            List<CurvePoint> retainedNew = [];
            foreach (QuantizedCurvePointEdit edit in laneEdit.Edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edit.Discard) continue;
                CurvePoint replacement = edit.Replacement!;
                retainedOld.Add(cloneById[edit.Old.Id]);
                retainedNew.Add(new CurvePoint(
                    project,
                    edit.Old.Id,
                    replacement.Tick,
                    replacement.Value,
                    CurveInterpolation.Step));
            }
            using (cloneLane.Points.BeginBatchChange())
            {
                cloneLane.Points.ReplaceRange(retainedOld, retainedNew);
                if (discarded.Length != 0)
                {
                    _ = cloneLane.Points.RemoveRange(
                        discarded.Select(value => cloneById[value.Id]).ToArray());
                }
            }
        }

        ProjectChangeSet changes = TrackChange(track.Id);
        foreach (QuantizedCurvePointLaneEdit laneEdit in laneEdits)
        {
            LogicalParameterLane cloneLane = cloneLanes[laneEdit.Lane.Id];
            ProjectTimelineOwnerChangeSetBuilder.AddLogicalParameterPoints(
                changes,
                segment.Id,
                laneEdit.Lane,
                cloneLane,
                laneEdit.Edits.Select(static value => value.Old.Id)
                    .Concat(laneEdit.Edits.SelectMany(static value => value.Displaced)
                        .Select(static value => value.Id)));
        }
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            segment,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(
            rootSwap,
            frozenOriginal,
            resultSelection,
            publishResult);
    }

    private static IPreparedProjectEdit PrepareDirectEventQuantize(
        MidoraProject project,
        PureMidiTrack track,
        MidiSegment segment,
        QuantizedDirectEventEdit[] edits,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        DirectMidiChannelEvent[] discarded = edits.Where(static value => value.Discard)
            .Select(static value => value.Event)
            .Concat(edits.SelectMany(static value => value.Displaced))
            .Distinct().ToArray();
        MidoraId[] original = edits.Select(static value => value.Event.Id).ToArray();
        MidoraId[] result = edits.Where(static value => !value.Discard)
            .Select(static value => value.Event.Id).ToArray();
        bool changed = discarded.Length != 0 || edits.Any(static value => !value.Discard
            && value.Old != value.Replacement);
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(original);
        if (!changed)
        {
            return WithSelectionPublication(
                ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegmentRevisionGate(
                    project,
                    track,
                    segment,
                    PureMidiTrackChange(track.Id),
                    sourceStamp),
                frozenOriginal,
                result,
                publishResult);
        }

        MidiSegment replacementRoot = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(
            project,
            segment,
            cancellationToken);
        MidoraId[] affectedIds = edits.Select(static value => value.Event.Id)
            .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, DirectMidiChannelEvent> cloneById = replacementRoot.ChannelEvents
            .ResolveByIds(affectedIds)
            .ToDictionary(static value => value.Value.Id, static value => value.Value);
        DirectMidiChannelEvent[] affected = cloneById.Values.ToArray();
        using (replacementRoot.ChannelEvents.BeginBatchChange(affected))
        {
            foreach (QuantizedDirectEventEdit edit in edits)
            {
                if (!edit.Discard)
                    ApplyDirectEvent(cloneById[edit.Event.Id], edit.Replacement);
            }
            if (discarded.Length != 0)
            {
                _ = replacementRoot.ChannelEvents.RemoveRange(
                    discarded.Select(value => cloneById[value.Id]).ToArray());
            }
        }
        ProjectChangeSet changes = PureMidiTrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddDirectEvents(
            changes,
            segment,
            replacementRoot,
            edits.Select(static value => value.Event.Id)
                .Concat(discarded.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(
            project,
            track,
            segment,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static IPreparedProjectEdit PrepareTemplateEventQuantize(
        MidoraProject project,
        EventInstrument instrument,
        SubVoice voice,
        QuantizedTemplateEventEdit[] edits,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        TemplateEvent[] discarded = edits.Where(static value => value.Discard)
            .Select(static value => value.Event)
            .Concat(edits.SelectMany(static value => value.Displaced))
            .Distinct().ToArray();
        MidoraId[] original = edits.Select(static value => value.Event.Id).ToArray();
        MidoraId[] result = edits.Where(static value => !value.Discard)
            .Select(static value => value.Event.Id).ToArray();
        bool changed = discarded.Length != 0 || edits.Any(static value => !value.Discard
            && value.Old != value.Replacement);
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(original);
        if (!changed)
        {
            return WithSelectionPublication(
                ProjectTimelineOwnerRootReplacement.PrepareSubVoiceRevisionGate(
                    project,
                    instrument,
                    voice,
                    EventInstrumentChange(instrument.Id),
                    sourceStamp),
                frozenOriginal,
                result,
                publishResult);
        }

        SubVoice replacementRoot = ProjectTimelineOwnerRootClone.CloneSubVoice(
            project,
            voice,
            cancellationToken);
        MidoraId[] affectedIds = edits.Select(static value => value.Event.Id)
            .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, TemplateEvent> cloneById = replacementRoot.Events
            .ResolveByIdsInCollectionOrder(affectedIds)
            .ToDictionary(static value => value.Id);
        using (replacementRoot.Events.BeginBatchChange())
        {
            foreach (QuantizedTemplateEventEdit edit in edits)
            {
                if (!edit.Discard)
                    SetTemplateEvent(cloneById[edit.Event.Id], edit.Replacement);
            }
            if (discarded.Length != 0)
            {
                _ = replacementRoot.Events.RemoveRange(
                    discarded.Select(value => cloneById[value.Id]).ToArray());
            }
        }
        ProjectChangeSet changes = EventInstrumentChange(instrument.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(
            changes,
            voice,
            replacementRoot,
            edits.Select(static value => value.Event.Id)
                .Concat(discarded.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(
            project,
            instrument,
            voice,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static DirectMidiEventStartKey DirectEventKey(DirectMidiEventValue value) => new(
        value.Tick,
        value.Kind,
        DirectMidiEventUsesData1Selector(value.Kind) ? value.Data1 : 0);

    private static QuantizedTemplatePointKey[] TemplateEventKeys(TemplateEventValue value) =>
        TemplateEventExactCollision.GetNonNoteDetails(
                value.Kind,
                value.Number,
                value.HasBankMsb,
                value.HasBankLsb)
            .Select(detail => new QuantizedTemplatePointKey(value.Tick, detail))
            .ToArray();

    private static void PollCancellation(CancellationToken cancellationToken, int index)
    {
        if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ReportQuantizeLoopProgress(
        IProgress<TimelineEditPreparationProgress>? progress,
        TimelineEditPreparationPhase phase,
        int completed,
        int total)
    {
        if ((completed & 4095) == 0)
            ReportPreparationProgress(progress, phase, completed, total);
    }

    private readonly record struct QuantizedNote(AdvancedNoteValue Value, bool Discard);
    private readonly record struct QuantizedTemplatePointKey(long Tick, int Detail);

    private sealed class QuantizedCurvePointEdit(CurvePoint old, int ordinal)
    {
        public CurvePoint Old { get; } = old;
        public int Ordinal { get; } = ordinal;
        public CurvePoint? Replacement { get; set; } = old;
        public bool Discard { get; set; }
        public List<CurvePoint> Displaced { get; } = [];
    }

    private sealed class QuantizedCurvePointLaneEdit(
        LogicalParameterLane lane,
        QuantizedCurvePointEdit[] edits)
    {
        public LogicalParameterLane Lane { get; } = lane;
        public QuantizedCurvePointEdit[] Edits { get; } = edits;
    }

    private sealed class PreparedCurvePointLaneEdit
    {
        private readonly CurvePointCollection _points;
        private readonly CurvePoint[] _retainedOld;
        private readonly CurvePoint[] _retainedNew;
        private readonly CurvePoint[] _discarded;
        private Action? _restoreDiscarded;

        public PreparedCurvePointLaneEdit(
            QuantizedCurvePointLaneEdit laneEdit,
            CancellationToken cancellationToken)
        {
            _points = laneEdit.Lane.Points;
            List<CurvePoint> retainedOld = [];
            List<CurvePoint> retainedNew = [];
            List<CurvePoint> discarded = [];
            HashSet<CurvePoint> discardedSet = [];
            bool changed = false;
            int itemIndex = 0;
            foreach (QuantizedCurvePointEdit edit in laneEdit.Edits)
            {
                PollCancellation(cancellationToken, itemIndex++);
                if (edit.Discard)
                {
                    if (discardedSet.Add(edit.Old))
                        discarded.Add(edit.Old);
                }
                else
                {
                    retainedOld.Add(edit.Old);
                    retainedNew.Add(edit.Replacement!);
                    changed |= edit.Old != edit.Replacement;
                }
                foreach (CurvePoint displaced in edit.Displaced)
                {
                    PollCancellation(cancellationToken, itemIndex++);
                    if (discardedSet.Add(displaced))
                        discarded.Add(displaced);
                }
            }
            _retainedOld = retainedOld.ToArray();
            _retainedNew = retainedNew.ToArray();
            _discarded = discarded.ToArray();
            HasChanges = changed || _discarded.Length != 0;
        }

        public bool HasChanges { get; }

        public void Apply()
        {
            using IDisposable batch = _points.BeginBatchChange();
            bool replacementsApplied = false;
            try
            {
                _points.ReplaceRange(_retainedOld, _retainedNew);
                replacementsApplied = true;
                if (_discarded.Length != 0)
                    _restoreDiscarded = _points.RemoveRangeWithUndo(_discarded);
            }
            catch
            {
                if (_restoreDiscarded is not null)
                {
                    _restoreDiscarded();
                    _restoreDiscarded = null;
                }
                if (replacementsApplied)
                    _points.ReplaceRange(_retainedNew, _retainedOld);
                throw;
            }
        }

        public void Undo()
        {
            using IDisposable batch = _points.BeginBatchChange();
            if (_restoreDiscarded is not null)
            {
                _restoreDiscarded();
                _restoreDiscarded = null;
            }
            _points.ReplaceRange(_retainedNew, _retainedOld);
        }
    }

    private sealed class QuantizedDirectEventEdit(
        DirectMidiChannelEvent @event,
        AdvancedFormalOrder formalOrder,
        DirectMidiEventValue old)
    {
        public DirectMidiChannelEvent Event { get; } = @event;
        public AdvancedFormalOrder FormalOrder { get; } = formalOrder;
        public DirectMidiEventValue Old { get; } = old;
        public DirectMidiEventValue Replacement { get; set; } = old;
        public bool Discard { get; set; }
        public List<DirectMidiChannelEvent> Displaced { get; } = [];
    }

    private sealed class QuantizedTemplateEventEdit(
        TemplateEvent @event,
        int ordinal,
        TemplateEventValue old)
    {
        public TemplateEvent Event { get; } = @event;
        public int Ordinal { get; } = ordinal;
        public TemplateEventValue Old { get; } = old;
        public TemplateEventValue Replacement { get; set; } = old;
        public bool Discard { get; set; }
        public List<TemplateEvent> Displaced { get; } = [];
    }
}
