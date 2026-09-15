using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreateLogicalTrack(
        string? name = null,
        MidoraId? eventInstrumentId = null,
        int? insertionIndex = null) =>
        Command("Create logical track", project =>
        {
            EventInstrument? instrument = eventInstrumentId is MidoraId instrumentId
                ? FindEventInstrument(project, instrumentId)
                : null;
            string normalizedName = ProjectTextRules.NormalizeShortText(
                name ?? "Logical Track",
                allowEmpty: true,
                nameof(name));
            int trackIndex = insertionIndex ?? project.ArrangementTracks.Count;
            ValidateInsertionIndex(trackIndex, project.ArrangementTracks.Count, nameof(insertionIndex));
            EventInstrumentUsage? createdUsage = null;
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    if (instrument is not null)
                    {
                        createdUsage = new(value) { EventInstrumentId = instrument.Id };
                        value.EventInstrumentUsages.Add(createdUsage);
                    }
                    LogicalTrack track = new(value)
                    {
                        Name = normalizedName,
                        EventInstrumentUsageId = createdUsage?.Id,
                        LastBoundEventInstrumentName = instrument?.Name
                    };
                    value.Tracks.Add(track);
                    value.ArrangementTracks.Insert(
                        trackIndex,
                        new(ArrangementTrackKind.LogicalTrack, track.Id));
                    return track;
                },
                (value, track) =>
                {
                    EnsureLogicalTrackIdAvailable(value, track.Id);
                    if (createdUsage is not null)
                    {
                        EnsureEventInstrumentUsageIdAvailable(value, createdUsage.Id);
                        value.EventInstrumentUsages.Add(createdUsage);
                    }
                    value.Tracks.Add(track);
                    InsertAt(
                        value.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(ArrangementTrackKind.LogicalTrack, track.Id),
                        "Arrangement Track reference");
                },
                (value, track) =>
                {
                    RemoveRequired(
                        value.ArrangementTracks,
                        new ArrangementTrackReference(ArrangementTrackKind.LogicalTrack, track.Id),
                        "Arrangement Track reference");
                    RemoveRequired(value.Tracks, track, "Logical Track");
                    if (createdUsage is not null)
                    {
                        RemoveRequired(value.EventInstrumentUsages, createdUsage, "Event Instrument Usage");
                    }
                });
        });

    public static IProjectEditCommand CreateLogicalTrackWithNewEventInstrument(
        string eventInstrumentName,
        string? trackName = null,
        int? insertionIndex = null) =>
        Command("Create logical track with Event Instrument", project =>
        {
            string normalizedInstrumentName = EventInstrumentLibrary.ValidateUniqueName(
                project,
                eventInstrumentName,
                default);
            string normalizedTrackName = ProjectTextRules.NormalizeShortText(
                trackName ?? "Logical Track",
                allowEmpty: true,
                nameof(trackName));
            int trackIndex = insertionIndex ?? project.ArrangementTracks.Count;
            ValidateInsertionIndex(trackIndex, project.ArrangementTracks.Count, nameof(insertionIndex));
            EventInstrument? instrument = null;
            EventInstrumentUsage? usage = null;
            LogicalTrack? track = null;
            return Prepared(
                true,
                EverythingChange(),
                value =>
                {
                    if (instrument is null)
                    {
                        instrument = EventInstrumentLibrary.Create(value, normalizedInstrumentName);
                        _ = SubVoiceMappingConventions.AddDefaultInstanceVelocityMapping(
                            value,
                            instrument.SubVoices[0]);
                        usage = new(value) { EventInstrumentId = instrument.Id };
                        track = new(value)
                        {
                            Name = normalizedTrackName,
                            EventInstrumentUsageId = usage.Id,
                            LastBoundEventInstrumentName = instrument.Name
                        };
                    }
                    else
                    {
                        EnsureEventInstrumentIdAvailable(value, instrument.Id);
                        value.EventInstruments.Add(instrument);
                        EnsureEventInstrumentUsageIdAvailable(value, usage!.Id);
                        EnsureLogicalTrackIdAvailable(value, track!.Id);
                    }
                    value.EventInstrumentUsages.Add(usage!);
                    value.Tracks.Add(track!);
                    value.ArrangementTracks.Insert(
                        trackIndex,
                        new(ArrangementTrackKind.LogicalTrack, track!.Id));
                },
                value =>
                {
                    RemoveRequired(
                        value.ArrangementTracks,
                        new ArrangementTrackReference(ArrangementTrackKind.LogicalTrack, track!.Id),
                        "Arrangement Track reference");
                    RemoveRequired(value.Tracks, track!, "Logical Track");
                    RemoveRequired(value.EventInstrumentUsages, usage!, "Event Instrument Usage");
                    RemoveRequired(value.EventInstruments, instrument!, "Event Instrument");
                });
        });

    public static IProjectEditCommand DuplicateLogicalTrack(
        MidoraId trackId,
        string? name = null) =>
        new ProjectPresentationCloneCommand(DuplicateLogicalTrack(trackId, shareInstrumentState: false, name), PresentationCloneKind.LogicalTrack, trackId);

    public static IProjectEditCommand DuplicateLogicalTrackAndShareState(
        MidoraId trackId,
        string? name = null) =>
        new ProjectPresentationCloneCommand(DuplicateLogicalTrack(trackId, shareInstrumentState: true, name), PresentationCloneKind.LogicalTrack, trackId);

    private static IProjectEditCommand DuplicateLogicalTrack(
        MidoraId trackId,
        bool shareInstrumentState,
        string? name) =>
        Command(
            shareInstrumentState
                ? "Duplicate logical track and share state"
                : "Duplicate logical track",
            project =>
        {
            LogicalTrack source = FindTrack(project, trackId);
            string copyName = ProjectTextRules.NormalizeShortText(
                name ?? $"{source.Name} Copy",
                allowEmpty: true,
                nameof(name));
            int sourceIndex = project.ArrangementTracks.IndexOf(
                new(ArrangementTrackKind.LogicalTrack, source.Id));
            if (sourceIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Logical Track is missing from the Arrangement Track order.");
            }
            int trackIndex = sourceIndex + 1;
            if (!shareInstrumentState
                && source.EventInstrumentUsageId is MidoraId sourceUsageId)
            {
                while (trackIndex < project.ArrangementTracks.Count
                    && project.ArrangementTracks[trackIndex] is
                    {
                        Kind: ArrangementTrackKind.LogicalTrack,
                        TrackId: MidoraId candidateTrackId
                    }
                    && FindTrack(project, candidateTrackId).EventInstrumentUsageId == sourceUsageId)
                {
                    trackIndex++;
                }
            }
            if (source.Segments.Sum(LogicalSegmentRecordCount) >= BoundedNoteThreshold)
                return PrepareBoundedLogicalTrackCopy(project, source, copyName, shareInstrumentState, trackIndex);
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    LogicalTrack copy = CloneLogicalTrack(value, source, copyName);
                    EventInstrumentUsage? independentUsage = null;
                    if (!shareInstrumentState
                        && source.EventInstrumentUsageId is MidoraId sourceUsageId)
                    {
                        EventInstrumentUsage sourceUsage = value.EventInstrumentUsages.SingleOrDefault(
                            candidate => candidate.Id == sourceUsageId)
                            ?? throw new InvalidOperationException(
                                "The Logical Track Event Instrument Usage no longer exists.");
                        independentUsage = new(value)
                        {
                            EventInstrumentId = sourceUsage.EventInstrumentId
                        };
                        copy.EventInstrumentUsageId = independentUsage.Id;
                        value.EventInstrumentUsages.Add(independentUsage);
                    }
                    value.Tracks.Add(copy);
                    value.ArrangementTracks.Insert(
                        trackIndex,
                        new(ArrangementTrackKind.LogicalTrack, copy.Id));
                    return new LogicalTrackDuplication(copy, independentUsage);
                },
                (value, duplication) =>
                {
                    if (duplication.IndependentUsage is EventInstrumentUsage independentUsage)
                    {
                        EnsureEventInstrumentUsageIdAvailable(value, independentUsage.Id);
                        value.EventInstrumentUsages.Add(independentUsage);
                    }
                    EnsureLogicalTrackIdAvailable(value, duplication.Track.Id);
                    value.Tracks.Add(duplication.Track);
                    InsertAt(
                        value.ArrangementTracks,
                        trackIndex,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.LogicalTrack,
                            duplication.Track.Id),
                        "Arrangement Track reference");
                },
                (value, duplication) =>
                {
                    RemoveRequired(
                        value.ArrangementTracks,
                        new ArrangementTrackReference(
                            ArrangementTrackKind.LogicalTrack,
                            duplication.Track.Id),
                        "Arrangement Track reference");
                    RemoveRequired(value.Tracks, duplication.Track, "Logical Track");
                    if (duplication.IndependentUsage is EventInstrumentUsage independentUsage)
                    {
                        RemoveRequired(
                            value.EventInstrumentUsages,
                            independentUsage,
                            "Event Instrument Usage");
                    }
                });
        });

    public static IProjectEditCommand CreateEventInstrument(
        string? name = null,
        int? insertionIndex = null) =>
        Command("Create event instrument", project =>
        {
            string? normalized = name is null
                ? null
                : EventInstrumentLibrary.ValidateUniqueName(project, name, default);
            int definitionIndex = insertionIndex ?? project.EventInstruments.Count;
            ValidateInsertionIndex(definitionIndex, project.EventInstruments.Count, nameof(insertionIndex));
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    EventInstrument instrument = EventInstrumentLibrary.Create(value, normalized);
                    _ = SubVoiceMappingConventions.AddDefaultInstanceVelocityMapping(
                        value,
                        instrument.SubVoices[0]);
                    Move(value.EventInstruments, instrument, definitionIndex);
                    return instrument;
                },
                (value, instrument) =>
                {
                    EnsureEventInstrumentIdAvailable(value, instrument.Id);
                    InsertAt(value.EventInstruments, definitionIndex, instrument, "Event Instrument");
                },
                (value, instrument) =>
                {
                    RemoveRequired(value.EventInstruments, instrument, "Event Instrument");
                });
        });

    public static IProjectEditCommand DuplicateEventInstrument(
        MidoraId eventInstrumentId,
        string? name = null) =>
        new ProjectPresentationCloneCommand(DuplicateEventInstrumentCore(eventInstrumentId, name), PresentationCloneKind.Instrument, eventInstrumentId);

    public static IProjectEditCommand DuplicateEventInstrumentOnly(
        MidoraId eventInstrumentId,
        string? name = null) =>
        new ProjectPresentationCloneCommand(DuplicateEventInstrumentCore(eventInstrumentId, name), PresentationCloneKind.Instrument, eventInstrumentId);

    private static IProjectEditCommand DuplicateEventInstrumentCore(
        MidoraId eventInstrumentId,
        string? name) =>
        Command("Duplicate event instrument", project =>
        {
            EventInstrument source = FindEventInstrument(project, eventInstrumentId);
            string? normalized = name is null
                ? null
                : EventInstrumentLibrary.ValidateUniqueName(project, name, default);
            int definitionIndex = project.EventInstruments.IndexOf(source) + 1;
            if (definitionIndex == 0)
            {
                throw new InvalidOperationException(
                    "The Event Instrument is missing from the Definition order.");
            }
            return DeferredCreate(
                EverythingChange(),
                value =>
                {
                    EventInstrument copy = CopyBoundedEventInstrument(
                        value,
                        source,
                        normalized);
                    Move(value.EventInstruments, copy, definitionIndex);
                    return copy;
                },
                (value, copy) =>
                {
                    EnsureEventInstrumentIdAvailable(value, copy.Id);
                    InsertAt(value.EventInstruments, definitionIndex, copy, "Event Instrument");
                },
                (value, copy) =>
                {
                    RemoveRequired(value.EventInstruments, copy, "Event Instrument");
                });
        });

    public static IProjectEditCommand CreateSegment(
        MidoraId trackId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick = 0) =>
        Command("Create segment", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            EnsureLogicalTrackCanContainContent(project, track);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            EnsureNoSegmentOverlap(track, null, projectStartTick, lengthTicks);
            return DeferredCreate(
                TrackChange(track.Id),
                value =>
                {
                    Segment segment = new(value)
                    {
                        ProjectStartTick = projectStartTick,
                        LengthTicks = lengthTicks,
                        ContentOffsetTick = contentOffsetTick
                    };
                    InsertSegmentByTime(track.Segments, segment);
                    return segment;
                },
                (_, segment) => InsertSegmentByTime(track.Segments, segment),
                (_, segment) => RemoveRequired(track.Segments, segment, "Segment"));
        });

    public static IProjectEditCommand DuplicateSegment(
        MidoraId segmentId,
        MidoraId targetTrackId,
        long newProjectStartTick) =>
        Command("Duplicate segment", project =>
        {
            SegmentLocation source = FindSegment(project, segmentId);
            LogicalTrack target = FindTrack(project, targetTrackId);
            EnsureLogicalTrackCanContainContent(project, target);
            ValidateSegmentRange(
                newProjectStartTick,
                source.Segment.LengthTicks,
                source.Segment.ContentOffsetTick);
            EnsureNoSegmentOverlap(target, null, newProjectStartTick, source.Segment.LengthTicks);
            if (LogicalSegmentRecordCount(source.Segment) >= BoundedNoteThreshold)
                return PrepareBoundedSegmentCopies(project, [new(source, target, newProjectStartTick)]);
            return DeferredCreate(
                TrackChange(source.Track.Id, target.Id),
                value =>
                {
                    Segment copy = SegmentEditing.Duplicate(value, source.Segment);
                    copy.ProjectStartTick = newProjectStartTick;
                    InsertSegmentByTime(target.Segments, copy);
                    return copy;
                },
                (_, copy) => InsertSegmentByTime(target.Segments, copy),
                (_, copy) => RemoveRequired(target.Segments, copy, "Segment"));
        });

    public static IProjectEditCommand SplitSegment(
        MidoraId segmentId,
        long projectSplitTick) =>
        Command("Split segment", project =>
        {
            SegmentLocation source = FindSegment(project, segmentId);
            if (projectSplitTick <= source.Segment.ProjectStartTick
                || projectSplitTick >= source.Segment.ProjectRange.EndTick)
            {
                throw new ArgumentOutOfRangeException(nameof(projectSplitTick));
            }
            if (LogicalSegmentRecordCount(source.Segment) >= BoundedNoteThreshold)
                return PrepareBoundedLogicalSegmentSplit(project, source, projectSplitTick);
            SegmentSplitResult? result = null;
            return Prepared(
                hasChanges: true,
                TrackChange(source.Track.Id),
                value =>
                {
                    if (result is null)
                    {
                        result = SegmentEditing.Split(value, source.Segment, projectSplitTick);
                    }
                    RequireContains(source.Track.Segments, source.Segment, "Segment");
                    source.Track.Segments.RemoveAt(source.Track.Segments.IndexOf(source.Segment));
                    source.Track.Segments.Insert(source.Index, result.Value.Left);
                    source.Track.Segments.Insert(source.Index + 1, result.Value.Right);
                },
                _ =>
                {
                    if (result is null)
                    {
                        throw new InvalidOperationException(
                            "A Segment split cannot be undone before its first Apply.");
                    }
                    RemoveRequired(source.Track.Segments, result.Value.Right, "right Segment");
                    RemoveRequired(source.Track.Segments, result.Value.Left, "left Segment");
                    InsertAt(source.Track.Segments, source.Index, source.Segment, "Segment");
                });
        });

    public static IProjectEditCommand CreateLogicalNote(
        MidoraId segmentId,
        long startTick,
        long lengthTicks,
        int note,
        int velocity,
        int? insertionIndex = null) =>
        Command("Create logical note", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            ValidateLogicalNote(startTick, lengthTicks, note, velocity);
            int index = insertionIndex ?? segment.Segment.Notes.Count;
            ValidateInsertionIndex(index, segment.Segment.Notes.Count, nameof(insertionIndex));
            return ResolveTargetedExactLogicalNoteCollisions(DeferredCreate(
                TrackChange(segment.Track.Id),
                value =>
                {
                    LogicalNote logicalNote = new(value)
                    {
                        StartTick = startTick,
                        LengthTicks = lengthTicks,
                        Note = note,
                        Velocity = velocity
                    };
                    segment.Segment.Notes.Insert(index, logicalNote);
                    return logicalNote;
                },
                (_, logicalNote) => InsertAt(
                    segment.Segment.Notes,
                    index,
                    logicalNote,
                    "Logical Note"),
                (_, logicalNote) => RemoveRequired(
                    segment.Segment.Notes,
                    logicalNote,
                    "Logical Note")),
                [new(segment.Segment, startTick, note)]);
        });

    public static IProjectEditCommand CreateLogicalParameterLane(
        MidoraId segmentId,
        MidoraId parameterId) =>
        Command("Create logical parameter lane", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            _ = FindBoundLogicalParameter(project, segment.Track, parameterId);
            LogicalParameterLane? existing = segment.Segment.ParameterLanes
                .SingleOrDefault(value => value.ParameterId == parameterId);
            if (existing is not null)
            {
                return Prepared(
                    hasChanges: false,
                    TrackChange(segment.Track.Id),
                    _ => { },
                    _ => { });
            }
            int index = segment.Segment.ParameterLanes.Count;
            return DeferredCreate(
                TrackChange(segment.Track.Id),
                value =>
                {
                    LogicalParameterLane lane = new(value) { ParameterId = parameterId };
                    segment.Segment.ParameterLanes.Add(lane);
                    return lane;
                },
                (_, lane) => InsertAt(
                    segment.Segment.ParameterLanes,
                    index,
                    lane,
                    "Logical Parameter Lane"),
                (_, lane) => RemoveRequired(
                    segment.Segment.ParameterLanes,
                    lane,
                    "Logical Parameter Lane"));
        });

    public static IProjectEditCommand CreateLogicalParameterPoint(
        MidoraId segmentId,
        MidoraId laneId,
        long tick,
        double value,
        CurveInterpolation interpolation) =>
        Command("Create logical parameter point", project =>
        {
            SegmentLocation segment = FindSegment(project, segmentId);
            LogicalParameterLane lane = FindLogicalParameterLane(segment.Segment, laneId);
            if (tick < 0 || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(tick < 0 ? nameof(tick) : nameof(value));
            }
            if (!Enum.IsDefined(interpolation))
            {
                throw new ArgumentOutOfRangeException(nameof(interpolation));
            }
            LogicalParameterDefinition definition = FindBoundLogicalParameter(
                project,
                segment.Track,
                lane.ParameterId);
            ValidatePointValue(definition, value, interpolation);
            return ResolveTargetedExactLogicalParameterPointCollisions(DeferredCreate(
                TrackChange(segment.Track.Id),
                owner =>
                {
                    CurvePoint point = new(owner, tick, value, interpolation);
                    InsertCurvePoint(lane.Points, point);
                    return point;
                },
                (_, point) => InsertCurvePoint(lane.Points, point),
                (_, point) => RemoveRequired(lane.Points, point, "Logical Parameter point")),
                lane,
                [tick]);
        });

    public static IProjectEditCommand CreateTempo(long tick, decimal beatsPerMinute) =>
        CreateConductorValue("Create tempo", ConductorKind.Tempo, tick, bpm: beatsPerMinute);

    public static IProjectEditCommand CreateTimeSignature(long tick, int numerator, int denominator) =>
        CreateConductorValue("Create time signature", ConductorKind.TimeSignature, tick, primary: numerator, secondary: denominator);

    public static IProjectEditCommand CreateKeySignature(long tick, int sharpsFlats, bool isMinor) =>
        CreateConductorValue("Create key signature", ConductorKind.KeySignature, tick, primary: sharpsFlats, flag: isMinor);

    public static IProjectEditCommand CreateProjectMarker(long tick, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return CreateConductorValue("Create project marker", ConductorKind.Marker, tick, text: name);
    }

    public static IProjectEditCommand CreateProjectEndMarker(long tick) =>
        Command("Create project end marker", project =>
        {
            ValidateConductorTick(tick, nameof(tick));
            if (project.Conductor.EndMarker is not null)
            {
                throw new InvalidOperationException("The Project End Marker already exists.");
            }
            return DeferredCreate(
                ConductorChange(),
                value =>
                {
                    ProjectEndMarker marker = new(value, tick);
                    value.Conductor.EndMarker = marker;
                    return marker;
                },
                (value, marker) =>
                {
                    if (value.Conductor.EndMarker is not null)
                    {
                        throw new InvalidOperationException(
                            "The Project End Marker already exists.");
                    }
                    value.Conductor.EndMarker = marker;
                },
                (value, marker) =>
                {
                    if (!ReferenceEquals(value.Conductor.EndMarker, marker))
                    {
                        throw new InvalidOperationException(
                            "The Project End Marker is no longer present.");
                    }
                    value.Conductor.EndMarker = null;
                });
        });

    private static LogicalTrack CloneLogicalTrack(
        MidoraProject project,
        LogicalTrack source,
        string name)
    {
        LogicalTrack copy = new(project)
        {
            Name = name,
            EventInstrumentUsageId = source.EventInstrumentUsageId,
            LastBoundEventInstrumentName = source.LastBoundEventInstrumentName,
            ColorOverride = source.ColorOverride
        };
        foreach (Segment segment in source.Segments)
        {
            copy.Segments.Add(SegmentEditing.Duplicate(project, segment));
        }
        return copy;
    }

    private sealed record LogicalTrackDuplication(
        LogicalTrack Track,
        EventInstrumentUsage? IndependentUsage);

    private static void InsertCurvePoint(CurvePointCollection points, CurvePoint point)
    {
        if (points.TryGetById(point.Id, out _))
        {
            throw new InvalidOperationException(
                "The Logical Parameter point ID is already present.");
        }
        points.Insert(FindCurvePointInsertionIndex(points, point.Tick, point.Id), point);
    }

    private static int FindCurvePointInsertionIndex(
        CurvePointCollection points,
        long tick,
        MidoraId id)
    {
        int low = 0;
        int high = points.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            CurvePoint candidate = points[middle];
            if (candidate.Tick < tick
                || candidate.Tick == tick && candidate.Id.CompareTo(id) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

}
