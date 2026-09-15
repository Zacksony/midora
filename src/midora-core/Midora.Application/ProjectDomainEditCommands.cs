using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand RenameLogicalTrack(MidoraId trackId, string name) =>
        Command("Rename logical track", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: true,
                nameof(name));
            string oldName = track.Name;
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                TrackChange(trackId),
                _ => track.Name = normalized,
                _ => track.Name = oldName);
        });

    public static IProjectEditCommand BindLogicalTrack(
        MidoraId trackId,
        MidoraId? eventInstrumentId) =>
        Command("Change logical track binding", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            if (!eventInstrumentId.HasValue)
            {
                if (track.Segments.Count != 0)
                {
                    throw new InvalidOperationException(
                        "A Logical Track with musical content cannot be unbound.");
                }
            }
            EventInstrument? target = eventInstrumentId is MidoraId targetId
                ? FindEventInstrument(project, targetId)
                : null;
            EventInstrumentUsage? oldUsage = track.EventInstrumentUsageId is MidoraId oldUsageId
                ? project.EventInstrumentUsages.SingleOrDefault(value => value.Id == oldUsageId)
                    ?? throw new InvalidOperationException(
                        "The Logical Track references a missing Event Instrument Usage.")
                : null;
            int oldUsageIndex = oldUsage is null
                ? -1
                : project.EventInstrumentUsages.IndexOf(oldUsage);
            bool removeOldUsage = oldUsage is not null
                && project.Tracks.Count(value => value.EventInstrumentUsageId == oldUsage.Id) == 1;
            MidoraId? oldDefinitionId = oldUsage?.EventInstrumentId;
            ArrangementTrackReference reference = new(
                ArrangementTrackKind.LogicalTrack,
                track.Id);
            ArrangementTrackReference[] beforeOrder = project.ArrangementTracks.ToArray();
            int currentIndex = project.ArrangementTracks.IndexOf(reference);
            if (currentIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Logical Track is missing from the Arrangement Track order.");
            }
            bool leavesSharedUsage = oldUsage is not null
                && oldDefinitionId != target?.Id
                && project.Tracks.Count(value => value.EventInstrumentUsageId == oldUsage.Id) > 1;
            ArrangementTrackReference[] afterOrder = leavesSharedUsage
                ? MoveReferenceOutsideGroup(
                    project,
                    beforeOrder,
                    reference,
                    currentIndex,
                    oldUsage!.Id)
                : beforeOrder;
            EnsureFormalGroupContiguity(
                project,
                afterOrder,
                leavesSharedUsage ? reference : null,
                null);
            MidoraId? oldUsageIdValue = track.EventInstrumentUsageId;
            string? oldLastBoundName = track.LastBoundEventInstrumentName;
            EventInstrumentUsage? newUsage = null;
            return Prepared(
                oldDefinitionId != eventInstrumentId,
                EverythingChange(),
                value =>
                {
                    if (removeOldUsage)
                    {
                        RemoveRequired(
                            value.EventInstrumentUsages,
                            oldUsage!,
                            "Event Instrument Usage");
                    }
                    if (target is not null)
                    {
                        if (newUsage is null)
                        {
                            newUsage = new(value) { EventInstrumentId = target.Id };
                        }
                        else
                        {
                            EnsureEventInstrumentUsageIdAvailable(value, newUsage.Id);
                        }
                        value.EventInstrumentUsages.Add(newUsage);
                    }
                    track.EventInstrumentUsageId = newUsage?.Id;
                    track.LastBoundEventInstrumentName = target?.Name ?? oldLastBoundName;
                    if (!beforeOrder.SequenceEqual(afterOrder))
                    {
                        ReplaceArrangementOrder(value, beforeOrder, afterOrder);
                    }
                },
                value =>
                {
                    if (!beforeOrder.SequenceEqual(afterOrder))
                    {
                        ReplaceArrangementOrder(value, afterOrder, beforeOrder);
                    }
                    if (newUsage is not null)
                    {
                        RemoveRequired(
                            value.EventInstrumentUsages,
                            newUsage,
                            "Event Instrument Usage");
                    }
                    if (removeOldUsage)
                    {
                        InsertAt(
                            value.EventInstrumentUsages,
                            oldUsageIndex,
                            oldUsage!,
                            "Event Instrument Usage");
                    }
                    track.EventInstrumentUsageId = oldUsageIdValue;
                    track.LastBoundEventInstrumentName = oldLastBoundName;
                });
        });

    public static IProjectEditCommand ReorderLogicalTrack(MidoraId trackId, int newIndex) =>
        MoveArrangementTrack(trackId, newIndex);

    public static IProjectEditCommand DeleteLogicalTrack(
        MidoraId trackId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete logical track", project =>
        {
            LogicalTrack track = FindTrack(project, trackId);
            if (track.Segments.Count != 0 && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Logical Track requires explicit confirmation.");
            }
            int originalIndex = project.Tracks.IndexOf(track);
            ArrangementTrackReference reference = new(ArrangementTrackKind.LogicalTrack, track.Id);
            int arrangementIndex = project.ArrangementTracks.IndexOf(reference);
            if (arrangementIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Logical Track is missing from the Arrangement Track order.");
            }
            EventInstrumentUsage? usage = track.EventInstrumentUsageId is MidoraId usageId
                ? project.EventInstrumentUsages.SingleOrDefault(value => value.Id == usageId)
                : null;
            int usageIndex = usage is null ? -1 : project.EventInstrumentUsages.IndexOf(usage);
            bool removeUsage = usage is not null
                && project.Tracks.Count(value => value.EventInstrumentUsageId == usage.Id) == 1;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    RemoveRequired(value.ArrangementTracks, reference, "Arrangement Track reference");
                    RemoveRequired(value.Tracks, track, "Logical Track");
                    if (removeUsage)
                    {
                        RemoveRequired(value.EventInstrumentUsages, usage!, "Event Instrument Usage");
                    }
                },
                value =>
                {
                    EnsureLogicalTrackIdAvailable(value, trackId);
                    InsertAt(value.Tracks, originalIndex, track, "Logical Track");
                    if (removeUsage)
                    {
                        InsertAt(
                            value.EventInstrumentUsages,
                            usageIndex,
                            usage!,
                            "Event Instrument Usage");
                    }
                    InsertAt(
                        value.ArrangementTracks,
                        arrangementIndex,
                        reference,
                        "Arrangement Track reference");
                });
        });

    public static IProjectEditCommand RenameEventInstrument(
        MidoraId eventInstrumentId,
        string name) =>
        Command("Rename event instrument", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            string normalized = EventInstrumentLibrary.ValidateUniqueName(
                project,
                name,
                eventInstrumentId);
            string oldName = instrument.Name;
            HashSet<MidoraId> usageIds = project.EventInstrumentUsages
                .Where(value => value.EventInstrumentId == eventInstrumentId)
                .Select(value => value.Id)
                .ToHashSet();
            InstrumentBinding[] bindings = project.Tracks
                .Where(value => value.EventInstrumentUsageId is MidoraId usageId
                    && usageIds.Contains(usageId))
                .Select(value => new InstrumentBinding(
                    value,
                    value.LastBoundEventInstrumentName))
                .ToArray();
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    instrument.Name = normalized;
                    foreach (InstrumentBinding binding in bindings)
                    {
                        binding.Track.LastBoundEventInstrumentName = normalized;
                    }
                },
                _ =>
                {
                    instrument.Name = oldName;
                    RestoreBindings(bindings);
                });
        });

    public static IProjectEditCommand ReorderEventInstrument(
        MidoraId eventInstrumentId,
        int newIndex) =>
        Command("Reorder event instrument", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            int oldIndex = project.EventInstruments.IndexOf(instrument);
            if (oldIndex < 0)
            {
                throw new InvalidOperationException(
                    "The Event Instrument is missing from the Definition order.");
            }
            ValidateExistingIndex(newIndex, project.EventInstruments.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                EverythingChange(),
                value => Move(value.EventInstruments, instrument, newIndex),
                value => Move(value.EventInstruments, instrument, oldIndex));
        });

    public static IProjectEditCommand DeleteEventInstrument(
        MidoraId eventInstrumentId,
        bool referencedDeletionConfirmed) =>
        Command("Delete event instrument", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            EventInstrumentUsage[] usages = project.EventInstrumentUsages
                .Where(value => value.EventInstrumentId == eventInstrumentId)
                .ToArray();
            if (usages.Length != 0)
            {
                throw new InvalidOperationException(
                    "A referenced Event Instrument Definition cannot be deleted. Remove or rebind all usages first.");
            }
            int originalIndex = project.EventInstruments.IndexOf(instrument);
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    RequireContains(value.EventInstruments, instrument, "Event Instrument");
                    value.EventInstruments.Remove(instrument);
                },
                value =>
                {
                    EnsureEventInstrumentIdAvailable(value, eventInstrumentId);
                    InsertAt(
                        value.EventInstruments,
                        originalIndex,
                        instrument,
                        "Event Instrument");
                });
        });

    public static IProjectEditCommand DeleteDamagedEventInstrument(MidoraId placeholderId) =>
        Command("Delete damaged event instrument", project =>
        {
            DamagedProjectObject placeholder = project.DamagedEventInstruments
                .SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedEventInstrumentDeletion? deletion = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    DamagedEventInstrumentDeletion applied =
                        DamagedProjectObjectEditing.DeleteEventInstrument(value, placeholderId);
                    deletion ??= applied;
                },
                value => DamagedProjectObjectEditing.UndoDeleteEventInstrument(
                    value,
                    deletion ?? throw new InvalidOperationException(
                        "The damaged Event Instrument deletion was not applied.")));
        });

    public static IProjectEditCommand DeleteDamagedLogicalTrack(MidoraId placeholderId) =>
        Command("Delete damaged logical track", project =>
        {
            DamagedProjectObject placeholder = project.DamagedLogicalTracks
                .SingleOrDefault(value => value.Id == placeholderId)
                ?? throw new ArgumentOutOfRangeException(nameof(placeholderId));
            DamagedLogicalTrackDeletion? deletion = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                value =>
                {
                    DamagedLogicalTrackDeletion applied =
                        DamagedProjectObjectEditing.DeleteLogicalTrack(value, placeholderId);
                    deletion ??= applied;
                },
                value => DamagedProjectObjectEditing.UndoDeleteLogicalTrack(
                    value,
                    deletion ?? throw new InvalidOperationException(
                        "The damaged Logical Track deletion was not applied.")));
        });

    public static IProjectEditCommand MoveSegment(
        MidoraId segmentId,
        MidoraId targetTrackId,
        long newProjectStartTick) =>
        Command("Move segment", project =>
        {
            SegmentLocation source = FindSegment(project, segmentId);
            LogicalTrack targetTrack = FindTrack(project, targetTrackId);
            EnsureLogicalTrackCanContainContent(project, targetTrack);
            ValidateSegmentRange(newProjectStartTick, source.Segment.LengthTicks, source.Segment.ContentOffsetTick);
            EnsureNoSegmentOverlap(
                targetTrack,
                source.Segment,
                newProjectStartTick,
                source.Segment.LengthTicks);
            long oldProjectStartTick = source.Segment.ProjectStartTick;
            return Prepared(
                source.Track.Id != targetTrackId || oldProjectStartTick != newProjectStartTick,
                TrackChange(source.Track.Id, targetTrackId),
                _ =>
                {
                    RemoveRequired(source.Track.Segments, source.Segment, "Segment");
                    source.Segment.ProjectStartTick = newProjectStartTick;
                    InsertSegmentByTime(targetTrack.Segments, source.Segment);
                },
                _ =>
                {
                    RemoveRequired(targetTrack.Segments, source.Segment, "Segment");
                    source.Segment.ProjectStartTick = oldProjectStartTick;
                    InsertAt(source.Track.Segments, source.Index, source.Segment, "Segment");
                });
        });

    public static IProjectEditCommand SetSegmentWindow(
        MidoraId segmentId,
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick) =>
        Command("Change segment window", project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            ValidateSegmentRange(projectStartTick, lengthTicks, contentOffsetTick);
            EnsureNoSegmentOverlap(
                location.Track,
                location.Segment,
                projectStartTick,
                lengthTicks);
            SegmentWindow old = new(
                location.Segment.ProjectStartTick,
                location.Segment.LengthTicks,
                location.Segment.ContentOffsetTick);
            SegmentWindow replacement = new(projectStartTick, lengthTicks, contentOffsetTick);
            return Prepared(
                old != replacement,
                TrackChange(location.Track.Id),
                _ => SetWindow(location.Segment, replacement),
                _ => SetWindow(location.Segment, old));
        });

    public static IProjectEditCommand DeleteSegment(
        MidoraId segmentId,
        bool nonEmptyDeletionConfirmed) =>
        Command("Delete segment", project =>
        {
            SegmentLocation location = FindSegment(project, segmentId);
            bool nonEmpty = location.Segment.Notes.Count != 0
                || location.Segment.ParameterLanes.Count != 0;
            if (nonEmpty && !nonEmptyDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a non-empty Segment requires explicit confirmation.");
            }
            return Prepared(
                hasChanges: true,
                TrackChange(location.Track.Id),
                _ => RemoveRequired(location.Track.Segments, location.Segment, "Segment"),
                _ => InsertAt(
                    location.Track.Segments,
                    location.Index,
                    location.Segment,
                    "Segment"));
        });

    public static IProjectEditCommand JoinSegments(MidoraId firstSegmentId, MidoraId secondSegmentId) =>
        Command("Join segments", project =>
        {
            SegmentLocation first = FindSegment(project, firstSegmentId);
            SegmentLocation second = FindSegment(project, secondSegmentId);
            if (!ReferenceEquals(first.Track, second.Track))
            {
                throw new InvalidOperationException("Only Segments on the same Logical Track can be joined.");
            }
            if (LogicalSegmentRecordCount(first.Segment) + LogicalSegmentRecordCount(second.Segment) >= BoundedNoteThreshold)
                return PrepareBoundedLogicalSegmentJoin(project, first, second);
            long nextStableId = project.NextStableId;
            Segment joined = SegmentEditing.Join(project, first.Segment, second.Segment);
            if (project.NextStableId != nextStableId)
            {
                throw new InvalidOperationException(
                    "Joining Segments unexpectedly allocated a stable ID.");
            }
            EnsureNoSegmentOverlap(
                first.Track,
                first.Segment,
                joined.ProjectStartTick,
                joined.LengthTicks,
                second.Segment);
            int insertionIndex = Math.Min(first.Index, second.Index);
            return Prepared(
                hasChanges: true,
                TrackChange(first.Track.Id),
                _ =>
                {
                    RequireContains(first.Track.Segments, first.Segment, "Segment");
                    RequireContains(first.Track.Segments, second.Segment, "Segment");
                    int high = Math.Max(
                        first.Track.Segments.IndexOf(first.Segment),
                        first.Track.Segments.IndexOf(second.Segment));
                    int low = Math.Min(
                        first.Track.Segments.IndexOf(first.Segment),
                        first.Track.Segments.IndexOf(second.Segment));
                    first.Track.Segments.RemoveAt(high);
                    first.Track.Segments.RemoveAt(low);
                    first.Track.Segments.Insert(insertionIndex, joined);
                },
                _ =>
                {
                    RemoveRequired(first.Track.Segments, joined, "joined Segment");
                    if (first.Index < second.Index)
                    {
                        InsertAt(first.Track.Segments, first.Index, first.Segment, "Segment");
                        InsertAt(first.Track.Segments, second.Index, second.Segment, "Segment");
                    }
                    else
                    {
                        InsertAt(first.Track.Segments, second.Index, second.Segment, "Segment");
                        InsertAt(first.Track.Segments, first.Index, first.Segment, "Segment");
                    }
                });
        });

    private static IProjectEditCommand Command(
        string name,
        Func<MidoraProject, IPreparedProjectEdit> prepare) =>
        new DelegateCommand(name, prepare);

    private static IPreparedProjectEdit Prepared(
        bool hasChanges,
        ProjectChangeSet changes,
        Action<MidoraProject> apply,
        Action<MidoraProject> undo) =>
        new DelegatePreparedEdit(hasChanges, changes, apply, undo);

    private static IPreparedProjectEdit ResolveTargetedExactLogicalNoteCollisions(
        IPreparedProjectEdit source,
        IEnumerable<LogicalNoteCollisionTarget> targets) =>
        ExactTimelineCollisionPolicy.Scope(source, logicalNoteTargets: targets);

    private static IPreparedProjectEdit ResolveTargetedExactLogicalParameterPointCollisions(
        IPreparedProjectEdit source,
        LogicalParameterLane lane,
        IEnumerable<long> ticks) =>
        ExactTimelineCollisionPolicy.Scope(
            source,
            logicalParameterPointTargets: ticks.Select(tick =>
                new LogicalParameterPointCollisionTarget(lane, tick)));

    private static IPreparedProjectEdit ResolveTargetedExactTemplateNoteCollisions(
        IPreparedProjectEdit source,
        IEnumerable<TemplateNoteCollisionTarget> targets) =>
        ExactTimelineCollisionPolicy.Scope(source, templateNoteTargets: targets);

    private static IPreparedProjectEdit ResolveTargetedExactTemplateEventPointCollisions(
        IPreparedProjectEdit source,
        IEnumerable<TemplateEventPointCollisionTarget> targets) =>
        ExactTimelineCollisionPolicy.Scope(source, templateEventPointTargets: targets);

    private static IEnumerable<TemplateEventPointCollisionTarget> CreateTemplateEventPointCollisionTargets(
        SubVoice voice,
        TemplateEventValue value) => TemplateEventExactCollision.GetNonNoteDetails(
            value.Kind,
            value.Number,
            value.HasBankMsb,
            value.HasBankLsb)
        .Select(detail => new TemplateEventPointCollisionTarget(voice, value.Tick, detail));

    private static IEnumerable<TemplateEventPointCollisionTarget> CreateTemplateEventPointCollisionTargets(
        SubVoice voice,
        TemplateEvent value) => TemplateEventExactCollision.GetNonNoteDetails(
            value.Kind,
            value.Number,
            value.HasBankMsb,
            value.HasBankLsb)
        .Select(detail => new TemplateEventPointCollisionTarget(voice, value.Tick, detail));

    private static IPreparedProjectEdit ResolveTargetedExactValueCurvePointCollisions(
        IPreparedProjectEdit source,
        ValueCurve valueCurve,
        IEnumerable<long> ticks) =>
        ExactTimelineCollisionPolicy.Scope(
            source,
            valueCurvePointTargets: ticks.Select(tick =>
                new ValueCurvePointCollisionTarget(valueCurve, tick)));

    private static IPreparedProjectEdit ResolveTargetedExactDirectMidiCollisions(
        IPreparedProjectEdit source,
        IEnumerable<DirectMidiNoteCollisionTarget>? noteTargets = null,
        IEnumerable<DirectMidiEventCollisionTarget>? eventTargets = null) =>
        ExactTimelineCollisionPolicy.Scope(
            source,
            directMidiNoteTargets: noteTargets,
            directMidiEventTargets: eventTargets);

    private static IPreparedProjectEdit DeferredCreate<T>(
        ProjectChangeSet changes,
        Func<MidoraProject, T> createAndAttach,
        Action<MidoraProject, T> reattach,
        Action<MidoraProject, T> detach)
        where T : class
    {
        T? created = null;
        return Prepared(
            hasChanges: true,
            changes,
            project =>
            {
                if (created is null)
                {
                    created = createAndAttach(project)
                        ?? throw new InvalidOperationException(
                            "A Project creation command returned no object.");
                }
                else
                {
                    reattach(project, created);
                }
            },
            project =>
            {
                if (created is null)
                {
                    throw new InvalidOperationException(
                        "A Project creation command cannot be undone before its first Apply.");
                }
                detach(project, created);
            });
    }

    private static LogicalTrack FindTrack(MidoraProject project, MidoraId trackId) =>
        project.Tracks.SingleOrDefault(value => value.Id == trackId)
        ?? throw new ArgumentOutOfRangeException(nameof(trackId));

    internal static int FindArrangementTrackIndex(
        MidoraProject project,
        ArrangementTrackKind kind,
        MidoraId trackId)
    {
        ArgumentNullException.ThrowIfNull(project);
        int index = project.ArrangementTracks.IndexOf(new(kind, trackId));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"The {kind} is missing from the authoritative Arrangement order.");
        }
        return index;
    }

    private static void EnsureLogicalTrackCanContainContent(
        MidoraProject project,
        LogicalTrack track)
    {
        if (track.EventInstrumentUsageId is not MidoraId usageId)
        {
            throw new InvalidOperationException(
                "Bind an Event Instrument before adding content to a Logical Track.");
        }
        EventInstrumentUsage usage = project.EventInstrumentUsages
            .SingleOrDefault(value => value.Id == usageId)
            ?? throw new InvalidOperationException(
                "The Logical Track references a missing Event Instrument Usage.");
        if (!project.EventInstruments.Any(value => value.Id == usage.EventInstrumentId))
        {
            throw new InvalidOperationException(
                "The Logical Track's Event Instrument Usage references a missing Definition.");
        }
    }

    private static EventInstrument FindEventInstrument(
        MidoraProject project,
        MidoraId eventInstrumentId) =>
        project.EventInstruments.SingleOrDefault(value => value.Id == eventInstrumentId)
        ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));

    private static SegmentLocation FindSegment(MidoraProject project, MidoraId segmentId)
    {
        LogicalSegmentIndexEntry result = ProjectSegmentIndex.FindLogical(project, segmentId)
            ?? throw new ArgumentOutOfRangeException(nameof(segmentId));
        return new(result.Track, result.Segment, result.Index);
    }

    private static void ValidateSegmentRange(
        long projectStartTick,
        long lengthTicks,
        long contentOffsetTick)
    {
        if (projectStartTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(projectStartTick));
        }
        if (lengthTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthTicks));
        }
        if (contentOffsetTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentOffsetTick));
        }
        _ = checked(projectStartTick + lengthTicks);
        _ = checked(contentOffsetTick + lengthTicks);
    }

    private static void EnsureNoSegmentOverlap(
        LogicalTrack track,
        Segment? primaryExcluded,
        long projectStartTick,
        long lengthTicks,
        Segment? secondaryExcluded = null)
    {
        TickRange candidate = new(projectStartTick, checked(projectStartTick + lengthTicks));
        if (track.Segments.Any(value =>
            !ReferenceEquals(value, primaryExcluded)
            && !ReferenceEquals(value, secondaryExcluded)
            && candidate.Intersects(value.ProjectRange)))
        {
            throw new InvalidOperationException(
                "Segments on the same Logical Track cannot overlap.");
        }
    }

    private static void SetWindow(Segment segment, SegmentWindow window)
    {
        segment.ProjectStartTick = window.ProjectStartTick;
        segment.LengthTicks = window.LengthTicks;
        segment.ContentOffsetTick = window.ContentOffsetTick;
    }

    private static void RestoreBindings(IEnumerable<InstrumentBinding> bindings)
    {
        foreach (InstrumentBinding binding in bindings)
        {
            binding.Track.LastBoundEventInstrumentName = binding.LastBoundEventInstrumentName;
        }
    }

    private static void ValidateExistingIndex(int index, int count, string parameterName)
    {
        if ((uint)index >= (uint)count)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateInsertionIndex(int index, int count, string parameterName)
    {
        if ((uint)index > (uint)count)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void Move<T>(List<T> values, T value, int targetIndex)
    {
        int currentIndex = values.IndexOf(value);
        if (currentIndex < 0)
        {
            throw new InvalidOperationException("The Project object is no longer present.");
        }
        values.RemoveAt(currentIndex);
        values.Insert(targetIndex, value);
    }

    private static void ReplaceLogicalTrackOrder(
        List<LogicalTrack> tracks,
        IReadOnlyList<LogicalTrack> expectedCurrent,
        IReadOnlyList<LogicalTrack> replacement)
    {
        if (!tracks.SequenceEqual(expectedCurrent, ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(
                "Logical Track membership changed while applying a reorder operation.");
        }
        tracks.Clear();
        tracks.AddRange(replacement);
    }

    private static void InsertSegmentByTime(List<Segment> segments, Segment segment)
    {
        int index = 0;
        while (index < segments.Count
            && (segments[index].ProjectStartTick < segment.ProjectStartTick
                || segments[index].ProjectStartTick == segment.ProjectStartTick
                && segments[index].Id.CompareTo(segment.Id) < 0))
        {
            index++;
        }
        segments.Insert(index, segment);
    }

    private static void RequireContains<T>(ICollection<T> values, T value, string objectName)
        where T : class
    {
        if (!values.Contains(value))
        {
            throw new InvalidOperationException($"The {objectName} is no longer present.");
        }
    }

    private static void RemoveRequired<T>(ICollection<T> values, T value, string objectName)
    {
        if (!values.Remove(value))
        {
            throw new InvalidOperationException($"The {objectName} is no longer present.");
        }
    }

    private static void InsertAt<T>(IList<T> values, int index, T value, string objectName)
    {
        if ((uint)index > (uint)values.Count)
        {
            throw new InvalidOperationException(
                $"The original {objectName} index can no longer be restored.");
        }
        values.Insert(index, value);
    }

    private static void EnsureLogicalTrackIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.Tracks.Any(value => value.Id == id)
            || project.DamagedLogicalTracks.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The Logical Track stable ID is already present.");
        }
    }

    private static void EnsureEventInstrumentIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstruments.Any(value => value.Id == id)
            || project.DamagedEventInstruments.Any(value => value.Id == id))
        {
            throw new InvalidOperationException("The Event Instrument stable ID is already present.");
        }
    }

    private static void EnsureEventInstrumentUsageIdAvailable(MidoraProject project, MidoraId id)
    {
        if (project.EventInstrumentUsages.Any(value => value.Id == id)
            || project.DamagedEventInstrumentUsages.Any(value => value.Id == id))
        {
            throw new InvalidOperationException(
                "The Event Instrument Usage stable ID is already present.");
        }
    }

    private static ProjectChangeSet NoCompilationChange() => new();

    private static ProjectChangeSet EverythingChange() => new() { AffectsEverything = true };

    private static ProjectChangeSet TrackChange(params MidoraId[] trackIds)
    {
        ProjectChangeSet result = new();
        result.TrackIds.UnionWith(trackIds);
        return result;
    }

    private static ProjectChangeSet EventInstrumentChange(MidoraId eventInstrumentId)
    {
        ProjectChangeSet result = new();
        result.EventInstrumentIds.Add(eventInstrumentId);
        return result;
    }

    private static ProjectChangeSet TrackPresentationChange(params MidoraId[] trackIds)
    {
        ProjectChangeSet result = new();
        result.PresentationTrackIds.UnionWith(trackIds);
        return result;
    }

    private static ProjectChangeSet EventInstrumentPresentationChange(
        MidoraId eventInstrumentId)
    {
        ProjectChangeSet result = new();
        result.PresentationEventInstrumentIds.Add(eventInstrumentId);
        return result;
    }

    private static ProjectChangeSet ConductorChange() => new() { AffectsConductor = true };

    private sealed class DelegateCommand(
        string name,
        Func<MidoraProject, IPreparedProjectEdit> prepare) : IProjectEditCommand
    {
        public string Name { get; } = name;
        public IPreparedProjectEdit Prepare(MidoraProject project) => prepare(project);
    }

    private sealed class DelegatePreparedEdit(
        bool hasChanges,
        ProjectChangeSet changes,
        Action<MidoraProject> apply,
        Action<MidoraProject> undo) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = hasChanges;
        public ProjectChangeSet Changes { get; } = changes;
        public void Apply(MidoraProject project) => apply(project);
        public void Undo(MidoraProject project) => undo(project);
    }

    private readonly record struct InstrumentBinding(
        LogicalTrack Track,
        string? LastBoundEventInstrumentName);

    private readonly record struct SegmentLocation(
        LogicalTrack Track,
        Segment Segment,
        int Index);

    private readonly record struct SegmentWindow(
        long ProjectStartTick,
        long LengthTicks,
        long ContentOffsetTick);
}
