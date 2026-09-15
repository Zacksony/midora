using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static ITimelineSelectionResultEditCommand SplitLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteSplitOptions options) => SplitLogicalNotesCore(segmentId, noteIds, options, null);

    private static ITimelineSelectionResultEditCommand SplitLogicalNotesCore(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteSplitOptions options,
        DetachedStableIdAllocator? sharedAllocator) =>
        ResultCommand("Split logical notes", (project, publishResult, cancellationToken, progress) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            SegmentLocation location = FindSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold
                || options.MaximumResultObjects >= BoundedNoteThreshold)
                return PrepareBoundedLogicalSplit(project, location, noteIds, options, publishResult, cancellationToken, progress, sharedAllocator);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedLogicalNote[] selected = SelectAdvancedLogicalNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            SplitPlan plan = PlanSplit(
                selected.Select(static value => ToAdvanced(value.Old)).ToArray(),
                selected.Select(static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal)).ToArray(),
                options,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            long fragmentCount = CountSplitFragments(plan);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, fragmentCount);
            LogicalNote[] displaced = ResolveLogicalSplitCollisions(
                location.Segment,
                selected,
                plan,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, fragmentCount, fragmentCount);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, fragmentCount);
            IPreparedProjectEdit prepared = PrepareLogicalSplit(
                project,
                location.Track,
                location.Segment,
                selected,
                plan,
                displaced,
                publishResult,
                cancellationToken,
                sourceStamp,
                sharedAllocator);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand SplitDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteSplitOptions options) => SplitDirectMidiNotesCore(segmentId, noteIds, options, null);

    private static ITimelineSelectionResultEditCommand SplitDirectMidiNotesCore(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteSplitOptions options,
        DetachedStableIdAllocator? sharedAllocator) =>
        ResultCommand("Split Direct MIDI notes", (project, publishResult, cancellationToken, progress) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold
                || options.MaximumResultObjects >= BoundedNoteThreshold)
                return PrepareBoundedDirectSplit(project, location, noteIds, options, publishResult, cancellationToken, progress, sharedAllocator);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedDirectNote[] selected = SelectAdvancedDirectNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            SplitPlan plan = PlanSplit(
                selected.Select(static value => ToAdvanced(value.Old)).ToArray(),
                selected.Select(static value => value.FormalOrder).ToArray(),
                options,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            long fragmentCount = CountSplitFragments(plan);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, fragmentCount);
            DirectMidiNote[] displaced = ResolveDirectSplitCollisions(
                location.Segment,
                selected,
                plan,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, fragmentCount, fragmentCount);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, fragmentCount);
            DirectNoteValue[][] frozenFragments = FreezeDirectSplitFragments(
                location.Segment,
                selected,
                plan,
                cancellationToken);
            IPreparedProjectEdit prepared = PrepareDirectSplit(
                project,
                location.Track,
                location.Segment,
                selected,
                plan,
                frozenFragments,
                displaced,
                publishResult,
                cancellationToken,
                sourceStamp,
                sharedAllocator);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand SplitTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteSplitOptions options) => SplitTemplateNotesCore(
            eventInstrumentId,
            subVoiceId,
            noteIds,
            options,
            null);

    private static ITimelineSelectionResultEditCommand SplitTemplateNotesCore(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteSplitOptions options,
        DetachedStableIdAllocator? sharedAllocator) =>
        ResultCommand("Split SubVoice notes", (project, publishResult, cancellationToken, progress) =>
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold
                || options.MaximumResultObjects >= BoundedNoteThreshold)
                return PrepareBoundedTemplateSplit(project, instrument, voice, noteIds, options, publishResult, cancellationToken, progress, sharedAllocator);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            AdvancedTemplateNote[] selected = SelectAdvancedTemplateNotes(voice, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            SplitPlan plan = PlanSplit(
                selected.Select(static value => ToAdvanced(value.Old)).ToArray(),
                selected.Select(static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal)).ToArray(),
                options,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            long fragmentCount = CountSplitFragments(plan);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, fragmentCount);
            TemplateEvent[] displaced = ResolveTemplateSplitCollisions(
                voice,
                selected,
                plan,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, fragmentCount, fragmentCount);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, fragmentCount);
            IPreparedProjectEdit prepared = PrepareTemplateSplit(
                project,
                instrument,
                voice,
                selected,
                plan,
                displaced,
                publishResult,
                cancellationToken,
                sourceStamp,
                sharedAllocator);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand JoinLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteJoinOptions options) =>
        ResultCommand("Join logical notes", (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            SegmentLocation location = FindSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedLogicalJoin(project, location, noteIds, options, publishResult, cancellationToken, progress);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedLogicalNote[] selected = SelectAdvancedLogicalNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            JoinPlan plan = PlanJoin(
                selected.Select(static value => ToAdvanced(value.Old)).ToArray(),
                selected.Select(static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal)).ToArray(),
                options,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, plan.Runs.Count);
            AdvancedLogicalNoteEdit[] edits = plan.Runs.Select(run =>
                new AdvancedLogicalNoteEdit(
                    selected[run.FirstSourceIndex],
                    FromAdvancedLogical(run.Value),
                    discard: false)).ToArray();
            LogicalNote[] removed = plan.RemovedSourceIndices
                .Select(index => selected[index].Note).ToArray();
            IPreparedProjectEdit prepared = PrepareLogicalJoin(
                project,
                location.Track,
                location.Segment,
                selected,
                edits,
                removed,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand JoinDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteJoinOptions options) =>
        ResultCommand("Join Direct MIDI notes", (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold)
                return PrepareBoundedDirectJoin(project, location, noteIds, options, publishResult, cancellationToken, progress);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedDirectNote[] selected = SelectAdvancedDirectNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            JoinPlan plan = PlanJoin(
                selected.Select(static value => ToAdvanced(value.Old)).ToArray(),
                selected.Select(static value => value.FormalOrder).ToArray(),
                options,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, plan.Runs.Count);
            AdvancedDirectNoteEdit[] edits = plan.Runs.Select(run =>
            {
                DirectNoteValue first = selected[run.FirstSourceIndex].Old;
                DirectNoteValue last = selected[run.LastSourceIndex].Old;
                return new AdvancedDirectNoteEdit(
                    selected[run.FirstSourceIndex],
                    FromAdvancedDirect(run.Value, first) with
                    {
                        NoteOffVelocity = last.NoteOffVelocity,
                        NoteOffOrder = last.NoteOffOrder
                    },
                    discard: false);
            }).ToArray();
            DirectMidiNote[] removed = plan.RemovedSourceIndices
                .Select(index => selected[index].Note).ToArray();
            IPreparedProjectEdit prepared = PrepareDirectJoin(
                project,
                location.Track,
                location.Segment,
                selected,
                edits,
                removed,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand JoinTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        NoteJoinOptions options) =>
        ResultCommand("Join SubVoice notes", (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
                return PrepareBoundedTemplateJoin(project, instrument, voice, noteIds, options, publishResult, cancellationToken, progress);
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            AdvancedTemplateNote[] selected = SelectAdvancedTemplateNotes(voice, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            JoinPlan plan = PlanJoin(
                selected.Select(static value => ToAdvanced(value.Old)).ToArray(),
                selected.Select(static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal)).ToArray(),
                options,
                cancellationToken);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.BuildingResult, 0, plan.Runs.Count);
            AdvancedTemplateNoteEdit[] edits = plan.Runs.Select(run =>
                new AdvancedTemplateNoteEdit(
                    selected[run.FirstSourceIndex],
                    FromAdvancedTemplate(
                        run.Value,
                        selected[run.FirstSourceIndex].Old),
                    discard: false)).ToArray();
            TemplateEvent[] removed = plan.RemovedSourceIndices
                .Select(index => selected[index].Event).ToArray();
            IPreparedProjectEdit prepared = PrepareTemplateJoin(
                project,
                instrument,
                voice,
                selected,
                edits,
                removed,
                publishResult,
                cancellationToken,
                sourceStamp);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return prepared;
        });

    public static ITimelineSelectionResultEditCommand HumanizeLogicalNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        TimelineHumanizeOptions options) =>
        ResultCommand("Humanize logical notes", (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            SegmentLocation location = FindSegment(project, segmentId);
            if (noteIds.Count >= BoundedNoteThreshold || location.Segment.Notes.Count >= BoundedNoteThreshold)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                ulong frozenSeed = ResolveHumanizeSeed(options);
                var frozen = location.Segment.Notes.CreateQuerySnapshot();
                return PrepareBoundedLogicalNotes(project, location, noteIds, _ => value =>
                {
                    if (!frozen.TryFindOrdinalById(value.Id, out int ordinal)) throw new InvalidOperationException("The frozen note disappeared.");
                    var result = HumanizeNote(new(value.StartTick, value.LengthTicks, value.Note, value.Velocity),
                        segmentId, AdvancedFormalOrder.FromCollectionOrdinal(ordinal), options, frozenSeed, 0, null);
                    if (result.StartTick < 0) return null;
                    result = NormalizeHumanizedGate(result, null);
                    return value with { StartTick = result.StartTick, LengthTicks = result.LengthTicks, Note = result.Key, Velocity = result.Velocity };
                }, publishResult: publishResult, formalCollisions: true);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedLogicalNote[] selected = SelectAdvancedLogicalNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ulong seed = ResolveHumanizeSeed(options);
            const long minimumStart = 0;
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            AdvancedLogicalNoteEdit[] edits = selected.Select((value, index) =>
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((index & 4095) == 0)
                    ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, index, selected.Length);
                AdvancedNoteValue replacement = HumanizeNote(
                    ToAdvanced(value.Old),
                    segmentId,
                    AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal),
                    options,
                    seed,
                    minimumStart,
                    exclusiveMaximumStart: null);
                bool discard = replacement.StartTick < minimumStart;
                if (!discard)
                    replacement = NormalizeHumanizedGate(replacement, exclusiveEnd: null);
                return new AdvancedLogicalNoteEdit(value, FromAdvancedLogical(replacement), discard);
            }).ToArray();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveLogicalNoteCollisions(location.Segment, edits, cancellationToken);
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

    public static ITimelineSelectionResultEditCommand HumanizeDirectMidiNotes(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds,
        TimelineHumanizeOptions options) =>
        ResultCommand("Humanize Direct MIDI notes", (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            MidiSegmentLocation location = FindMidiSegment(project, segmentId);
            if (noteIds.Count > 4096 || location.Segment.Notes.Count > 4096)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                ulong frozenSeed = ResolveHumanizeSeed(options);
                return PrepareBoundedDirectMidiNoteTransform(project, segmentId, noteIds, _ => value =>
                {
                    var humanized = HumanizeNote(new(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity),
                        segmentId, new(value.NoteOnOrder, value.Id), options, frozenSeed, 0, null);
                    if (humanized.StartTick < 0) return null;
                    humanized = NormalizeHumanizedGate(humanized, null);
                    return value with { StartTick = humanized.StartTick, LengthTicks = humanized.LengthTicks,
                        Key = humanized.Key, NoteOnVelocity = humanized.Velocity };
                }, formalCollisions: true, publishResult: publishResult);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
            AdvancedDirectNote[] selected = SelectAdvancedDirectNotes(location.Segment, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ulong seed = ResolveHumanizeSeed(options);
            const long minimumStart = 0;
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            AdvancedDirectNoteEdit[] edits = selected.Select((value, index) =>
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((index & 4095) == 0)
                    ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, index, selected.Length);
                AdvancedNoteValue replacement = HumanizeNote(
                    ToAdvanced(value.Old),
                    segmentId,
                    value.FormalOrder,
                    options,
                    seed,
                    minimumStart,
                    exclusiveMaximumStart: null);
                bool discard = replacement.StartTick < minimumStart;
                if (!discard)
                    replacement = NormalizeHumanizedGate(replacement, exclusiveEnd: null);
                return new AdvancedDirectNoteEdit(value, FromAdvancedDirect(replacement, value.Old), discard);
            }).ToArray();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveDirectNoteCollisions(location.Segment, edits, cancellationToken);
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

    public static ITimelineSelectionResultEditCommand HumanizeTemplateNotes(
        MidoraId eventInstrumentId,
        MidoraId subVoiceId,
        IReadOnlyCollection<MidoraId> noteIds,
        TimelineHumanizeOptions options) =>
        ResultCommand("Humanize SubVoice notes", (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, 0, noteIds.Count);
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, subVoiceId);
            if (noteIds.Count >= BoundedNoteThreshold || voice.Events.Count >= BoundedNoteThreshold)
            {
                using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
                ulong frozenSeed = ResolveHumanizeSeed(options);
                var frozen = voice.Events.CreateQuerySnapshot();
                return PrepareBoundedTemplateNotes(project, instrument, voice, noteIds, _ => value =>
                {
                    if (!frozen.TryFindOrdinalById(value.Id, out int ordinal)) throw new InvalidOperationException("The frozen note disappeared.");
                    var result = HumanizeNote(new(value.Tick, value.LengthTicks, value.Number, value.Value),
                        subVoiceId, AdvancedFormalOrder.FromCollectionOrdinal(ordinal), options, frozenSeed, 0, instrument.TemplateLengthTicks);
                    if (result.StartTick < 0 || result.StartTick >= instrument.TemplateLengthTicks) return null;
                    result = NormalizeHumanizedGate(result, instrument.TemplateLengthTicks);
                    return value with { Tick = result.StartTick, LengthTicks = result.LengthTicks, Number = result.Key, Value = result.Velocity };
                }, publishResult: publishResult, formalCollisions: true);
            }
            ProjectTimelineOwnerSourceStamp sourceStamp =
                ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
            AdvancedTemplateNote[] selected = SelectAdvancedTemplateNotes(voice, noteIds);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingSelection, selected.Length, selected.Length);
            ulong seed = ResolveHumanizeSeed(options);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, 0, selected.Length);
            AdvancedTemplateNoteEdit[] edits = selected.Select((value, index) =>
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((index & 4095) == 0)
                    ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, index, selected.Length);
                AdvancedNoteValue replacement = HumanizeNote(
                    ToAdvanced(value.Old),
                    subVoiceId,
                    AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal),
                    options,
                    seed,
                    minimumStart: 0,
                    exclusiveMaximumStart: instrument.TemplateLengthTicks);
                bool discard = replacement.StartTick < 0
                    || replacement.StartTick >= instrument.TemplateLengthTicks;
                if (!discard)
                {
                    replacement = NormalizeHumanizedGate(
                        replacement,
                        instrument.TemplateLengthTicks);
                }
                return new AdvancedTemplateNoteEdit(
                    value,
                    FromAdvancedTemplate(replacement, value.Old),
                    discard);
            }).ToArray();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Planning, selected.Length, selected.Length);
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.ResolvingCollisions, 0, edits.Length);
            ResolveTemplateNoteCollisions(voice, edits, cancellationToken);
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

    private static ulong ResolveHumanizeSeed(TimelineHumanizeOptions options)
    {
        ValidateHumanizeField(options.Tick, nameof(options.Tick));
        ValidateHumanizeField(options.Gate, nameof(options.Gate));
        ValidateHumanizeField(options.Velocity, nameof(options.Velocity));
        if (options.Seed is ulong seed) return seed;
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt64(bytes);
    }

    private static SplitPlan PlanSplit(
        IReadOnlyList<AdvancedNoteValue> notes,
        IReadOnlyList<AdvancedFormalOrder> ordinals,
        NoteSplitOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options.Mode));
        if (options.Mode == NoteSplitMode.Expression
            && options.MaximumCuts is < 1 or > NoteSplitOptions.MaximumSupportedCuts)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumCuts));
        if (options.MaximumResultObjects is < 1
            or > NoteSplitOptions.MaximumSupportedResultObjects)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumResultObjects));
        if (notes.Count == 0 || notes.Count != ordinals.Count)
            throw new ArgumentException("Note Split requires a non-empty, aligned selection.", nameof(notes));
        long left = long.MaxValue;
        long right = long.MinValue;
        for (int index = 0; index < notes.Count; index++)
        {
            PollAdvancedCancellation(cancellationToken, index);
            AdvancedNoteValue note = notes[index];
            long end = checked(note.StartTick + note.LengthTicks);
            left = Math.Min(left, note.StartTick);
            right = Math.Max(right, end);
        }
        long span = checked(right - left);
        long[] knives = CreateSplitKnives(left, span, options, cancellationToken);

        // Establish the complete result cardinality before allocating any
        // per-source fragment buffers. This makes the formal result limit a
        // planning boundary rather than an allocation-after-the-fact check.
        long resultCount = 0;
        for (int sourceIndex = 0; sourceIndex < notes.Count; sourceIndex++)
        {
            PollAdvancedCancellation(cancellationToken, sourceIndex);
            AdvancedNoteValue note = notes[sourceIndex];
            long end = checked(note.StartTick + note.LengthTicks);
            int firstKnife = Array.BinarySearch(knives, note.StartTick);
            firstKnife = firstKnife >= 0 ? firstKnife + 1 : ~firstKnife;
            int endKnife = LowerBound(knives, end);
            long fragmentCount = checked((long)endKnife - firstKnife + 1);
            resultCount = checked(resultCount + fragmentCount);
            if (resultCount > options.MaximumResultObjects)
            {
                throw new InvalidOperationException(
                    "Note Split exceeds the configured maximum result-object count.");
            }
        }

        List<SplitSourcePlan> sources = new(notes.Count);
        int visitedFragments = 0;
        for (int sourceIndex = 0; sourceIndex < notes.Count; sourceIndex++)
        {
            PollAdvancedCancellation(cancellationToken, sourceIndex);
            AdvancedNoteValue note = notes[sourceIndex];
            long end = checked(note.StartTick + note.LengthTicks);
            int firstKnife = Array.BinarySearch(knives, note.StartTick);
            firstKnife = firstKnife >= 0 ? firstKnife + 1 : ~firstKnife;
            int endKnife = LowerBound(knives, end);
            int fragmentCount = checked(endKnife - firstKnife + 1);
            List<AdvancedNoteValue> fragments = new(fragmentCount);
            long fragmentStart = note.StartTick;
            for (int knifeIndex = firstKnife; knifeIndex < endKnife; knifeIndex++)
            {
                PollAdvancedCancellation(cancellationToken, visitedFragments++);
                long knife = knives[knifeIndex];
                fragments.Add(note with
                {
                    StartTick = fragmentStart,
                    LengthTicks = checked(knife - fragmentStart)
                });
                fragmentStart = knife;
            }
            PollAdvancedCancellation(cancellationToken, visitedFragments++);
            fragments.Add(note with
            {
                StartTick = fragmentStart,
                LengthTicks = checked(end - fragmentStart)
            });
            sources.Add(new(sourceIndex, ordinals[sourceIndex], fragments.ToArray()));
        }
        return new(left, right, knives, sources.ToArray());
    }

    private static long[] CreateSplitKnives(
        long left,
        long span,
        NoteSplitOptions options,
        CancellationToken cancellationToken)
    {
        // Validate the selected mode even when the selection is too short to
        // yield a knife. A one-tick selection must not turn an invalid command
        // into a successful no-op.
        switch (options.Mode)
        {
            case NoteSplitMode.FixedPieceLength:
                if (options.FixedPieceLengthTicks < 1)
                    throw new ArgumentOutOfRangeException(nameof(options.FixedPieceLengthTicks));
                break;
            case NoteSplitMode.MaximumPieceCount:
                if (options.MaximumPieceCount < 1)
                    throw new ArgumentOutOfRangeException(nameof(options.MaximumPieceCount));
                break;
            case NoteSplitMode.Expression:
                if (options.ExpressionProgram is null)
                {
                    throw new ArgumentException(
                        "Expression Note Split requires a compiled expression program.",
                        nameof(options));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Mode));
        }

        if (span <= 1) return [];
        List<long> result = [];
        switch (options.Mode)
        {
            case NoteSplitMode.FixedPieceLength:
            {
                long requiredCuts = (span - 1) / options.FixedPieceLengthTicks;
                ValidateAutomaticSplitCutCount(requiredCuts, options.Mode);
                int cutCount = checked((int)requiredCuts);
                for (int index = 1; index <= cutCount; index++)
                {
                    PollAdvancedCancellation(cancellationToken, index);
                    long relative = checked((long)(
                        (Int128)index * options.FixedPieceLengthTicks));
                    result.Add(checked(left + relative));
                }
                break;
            }
            case NoteSplitMode.MaximumPieceCount:
            {
                long blocks = Math.Min((long)options.MaximumPieceCount, span);
                long requiredCuts = blocks - 1;
                ValidateAutomaticSplitCutCount(requiredCuts, options.Mode);
                int cutCount = checked((int)requiredCuts);
                long previous = 0;
                for (int index = 1; index <= cutCount; index++)
                {
                    PollAdvancedCancellation(cancellationToken, index);
                    long relative = checked((long)(((Int128)index * span) / blocks));
                    if (relative <= previous || relative >= span) continue;
                    result.Add(checked(left + relative));
                    previous = relative;
                }
                break;
            }
            case NoteSplitMode.Expression:
            {
                Midora.Compiler.NoteSplitExpressionProgram program = options.ExpressionProgram!;
                long relative = 0;
                for (int index = 0; index < options.MaximumCuts; index++)
                {
                    PollAdvancedCancellation(cancellationToken, index);
                    double evaluated = program.Evaluate(index, relative);
                    if (!double.IsFinite(evaluated))
                        throw new InvalidOperationException(
                            "The Note Split expression returned a non-finite length.");
                    double rounded = Math.Round(evaluated, MidpointRounding.AwayFromZero);
                    long length = rounded >= long.MaxValue
                        ? long.MaxValue
                        : rounded <= 1
                            ? 1
                            : checked((long)rounded);
                    if (relative > long.MaxValue - length) break;
                    relative += length;
                    if (relative >= span) break;
                    result.Add(checked(left + relative));
                }
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options.Mode));
        }
        return result.ToArray();
    }

    private static void ValidateAutomaticSplitCutCount(long requiredCuts, NoteSplitMode mode)
    {
        if (requiredCuts <= NoteSplitOptions.MaximumSupportedCuts) return;
        throw new InvalidOperationException(
            $"{mode} Note Split requires {requiredCuts:N0} cuts, which exceeds the formal hard limit of {NoteSplitOptions.MaximumSupportedCuts:N0}.");
    }

    private static long CountSplitFragments(SplitPlan plan)
    {
        long count = 0;
        foreach (SplitSourcePlan source in plan.Sources)
            count = checked(count + source.Fragments.Count);
        return count;
    }

    private static int LowerBound(long[] sorted, long value)
    {
        int low = 0;
        int high = sorted.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sorted[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int CountAtOrBefore(int[] sorted, int value)
    {
        int low = 0;
        int high = sorted.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (sorted[middle] <= value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static JoinPlan PlanJoin(
        IReadOnlyList<AdvancedNoteValue> notes,
        IReadOnlyList<AdvancedFormalOrder> ordinals,
        NoteJoinOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumGapTicks < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumGapTicks));
        if (notes.Count == 0 || notes.Count != ordinals.Count)
            throw new ArgumentException("Join Notes requires a non-empty, aligned selection.", nameof(notes));
        int[] ordered = new int[notes.Count];
        for (int index = 0; index < ordered.Length; index++)
        {
            PollAdvancedCancellation(cancellationToken, index);
            ordered[index] = index;
        }
        int compared = 0;
        Array.Sort(ordered, (leftIndex, rightIndex) =>
        {
            PollAdvancedCancellation(cancellationToken, compared++);
            int key = notes[leftIndex].Key.CompareTo(notes[rightIndex].Key);
            if (key != 0) return key;
            int tick = notes[leftIndex].StartTick.CompareTo(notes[rightIndex].StartTick);
            return tick != 0 ? tick : ordinals[leftIndex].CompareTo(ordinals[rightIndex]);
        });

        List<JoinRun> runs = [];
        List<int> removed = [];
        int visited = 0;
        int keyStart = 0;
        while (keyStart < ordered.Length)
        {
            PollAdvancedCancellation(cancellationToken, visited++);
            int keyEnd = keyStart + 1;
            int key = notes[ordered[keyStart]].Key;
            while (keyEnd < ordered.Length && notes[ordered[keyEnd]].Key == key)
            {
                PollAdvancedCancellation(cancellationToken, visited++);
                keyEnd++;
            }

            int runStart = keyStart;
            while (runStart < keyEnd)
            {
                PollAdvancedCancellation(cancellationToken, visited++);
                int runEnd = runStart;
                long maximumEnd = checked(
                    notes[ordered[runStart]].StartTick + notes[ordered[runStart]].LengthTicks);
                while (runEnd + 1 < keyEnd)
                {
                    PollAdvancedCancellation(cancellationToken, visited++);
                    AdvancedNoteValue next = notes[ordered[runEnd + 1]];
                    long gap = checked(next.StartTick - maximumEnd);
                    if (gap > options.MaximumGapTicks) break;
                    runEnd++;
                    maximumEnd = Math.Max(
                        maximumEnd,
                        checked(next.StartTick + next.LengthTicks));
                }
                int firstIndex = ordered[runStart];
                int lastIndex = ordered[runEnd];
                AdvancedNoteValue first = notes[firstIndex];
                AdvancedNoteValue replacement = first with
                {
                    LengthTicks = checked(maximumEnd - first.StartTick)
                };
                runs.Add(new(firstIndex, lastIndex, replacement));
                for (int index = runStart + 1; index <= runEnd; index++)
                {
                    PollAdvancedCancellation(cancellationToken, visited++);
                    removed.Add(ordered[index]);
                }
                runStart = runEnd + 1;
            }
            keyStart = keyEnd;
        }
        int runComparisons = 0;
        runs.Sort((leftRun, rightRun) =>
        {
            PollAdvancedCancellation(cancellationToken, runComparisons++);
            return ordinals[leftRun.FirstSourceIndex]
                .CompareTo(ordinals[rightRun.FirstSourceIndex]);
        });
        int[] removedInFormalOrder = removed.ToArray();
        int removedComparisons = 0;
        Array.Sort(removedInFormalOrder, (leftIndex, rightIndex) =>
        {
            PollAdvancedCancellation(cancellationToken, removedComparisons++);
            return ordinals[leftIndex].CompareTo(ordinals[rightIndex]);
        });
        return new(runs.ToArray(), removedInFormalOrder);
    }

    private static LogicalNote[] ResolveLogicalSplitCollisions(
        Segment segment,
        AdvancedLogicalNote[] selected,
        SplitPlan plan,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> targets = SplitTargets(plan, cancellationToken);
        if (targets.Count == 0) return [];
        HashSet<TimelineStartLaneKey> query = targets.Select(static value =>
            new TimelineStartLaneKey(value.Tick, value.Key)).ToHashSet();
        MidoraId[] ids = segment.Notes.CreateQuerySnapshot().QueryStartKeys(query)
            .Select(static value => value.Id).ToArray();
        var current = segment.Notes.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static value => (Item: value.Value, Ordinal: value.Index,
                Key: new AdvancedNoteKey(value.Value.StartTick, value.Value.Note)))
            .ToArray();
        return ResolveSplitCollisionCore(
            plan,
            selected.Select(static value => value.Note).ToHashSet(),
            current,
            static value => value.Item,
            static value => value.Key,
            static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal),
            targets,
            cancellationToken);
    }

    private static DirectMidiNote[] ResolveDirectSplitCollisions(
        MidiSegment segment,
        AdvancedDirectNote[] selected,
        SplitPlan plan,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> targets = SplitTargets(plan, cancellationToken);
        if (targets.Count == 0) return [];
        HashSet<DirectMidiNoteStartKey> query = targets.Select(static value =>
            new DirectMidiNoteStartKey(value.Tick, value.Key)).ToHashSet();
        DirectMidiNote[] values = segment.Notes.QueryStartKeys(query).ToArray();
        var current = segment.Notes.ResolveByIds(values.Select(static value => value.Id).ToArray())
            .Select(static value => (Item: value.Value, FormalOrder: new AdvancedFormalOrder(
                    value.Value.NoteOnOrder,
                    value.Value.Id),
                Key: new AdvancedNoteKey(value.Value.StartTick, value.Value.Key)))
            .ToArray();
        return ResolveSplitCollisionCore(
            plan,
            selected.Select(static value => value.Note).ToHashSet(),
            current,
            static value => value.Item,
            static value => value.Key,
            static value => value.FormalOrder,
            targets,
            cancellationToken);
    }

    private static TemplateEvent[] ResolveTemplateSplitCollisions(
        SubVoice voice,
        AdvancedTemplateNote[] selected,
        SplitPlan plan,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> targets = SplitTargets(plan, cancellationToken);
        if (targets.Count == 0) return [];
        HashSet<TimelineStartLaneKey> query = targets.Select(static value =>
            new TimelineStartLaneKey(value.Tick, value.Key)).ToHashSet();
        MidoraId[] ids = voice.Events.CreateQuerySnapshot().QueryNoteStartKeys(query)
            .Select(static value => value.Id).ToArray();
        var current = voice.Events.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static value => (Item: value.Value, Ordinal: value.Index,
                Key: new AdvancedNoteKey(value.Value.Tick, value.Value.Number)))
            .ToArray();
        return ResolveSplitCollisionCore(
            plan,
            selected.Select(static value => value.Event).ToHashSet(),
            current,
            static value => value.Item,
            static value => value.Key,
            static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal),
            targets,
            cancellationToken);
    }

    private static HashSet<AdvancedNoteKey> SplitTargets(
        SplitPlan plan,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> result = [];
        int visited = 0;
        foreach (SplitSourcePlan source in plan.Sources)
        {
            for (int fragmentIndex = 0; fragmentIndex < source.Fragments.Count; fragmentIndex++)
            {
                PollAdvancedCancellation(cancellationToken, visited++);
                AdvancedNoteValue value = source.Fragments[fragmentIndex];
                result.Add(new(value.StartTick, value.Key));
            }
        }
        return result;
    }

    private static TItem[] ResolveSplitCollisionCore<TItem, TCurrent>(
        SplitPlan plan,
        IReadOnlySet<TItem> selectedItems,
        IReadOnlyCollection<TCurrent> current,
        Func<TCurrent, TItem> currentItem,
        Func<TCurrent, AdvancedNoteKey> currentKey,
        Func<TCurrent, AdvancedFormalOrder> currentOrdinal,
        IReadOnlySet<AdvancedNoteKey> touched,
        CancellationToken cancellationToken)
        where TItem : class
    {
        Dictionary<AdvancedNoteKey, SplitCollisionWinner<TItem>> winners = [];
        int visited = 0;

        // Every fragment at a key introduced by splitting participates in one
        // reducer, including a selected source's first (identity-preserving)
        // fragment. The order is the source's frozen formal ordinal followed
        // by the fragment ordinal; Stable ID allocation is deliberately absent.
        foreach (SplitSourcePlan source in plan.Sources)
        {
            for (int fragmentIndex = 0; fragmentIndex < source.Fragments.Count; fragmentIndex++)
            {
                if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                AdvancedNoteValue fragment = source.Fragments[fragmentIndex];
                AdvancedNoteKey key = new(fragment.StartTick, fragment.Key);
                if (!touched.Contains(key)) continue;
                SplitFragmentFormalOrder order = new(source.Ordinal, fragmentIndex);
                if (!winners.TryGetValue(key, out SplitCollisionWinner<TItem>? winner)
                    || winner is null
                    || order.CompareTo(winner.Order) < 0)
                {
                    winners[key] = new(order, source, fragmentIndex, null);
                }
            }
        }

        foreach (TCurrent value in current)
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            TItem item = currentItem(value);
            if (selectedItems.Contains(item)) continue;
            AdvancedNoteKey key = currentKey(value);
            if (!touched.Contains(key)) continue;
            SplitFragmentFormalOrder order = new(currentOrdinal(value), 0);
            if (!winners.TryGetValue(key, out SplitCollisionWinner<TItem>? winner)
                || winner is null
                || order.CompareTo(winner.Order) < 0)
            {
                winners[key] = new(order, null, -1, item);
            }
        }

        foreach (SplitSourcePlan source in plan.Sources)
        {
            for (int fragmentIndex = 0; fragmentIndex < source.Fragments.Count; fragmentIndex++)
            {
                if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                AdvancedNoteValue fragment = source.Fragments[fragmentIndex];
                AdvancedNoteKey key = new(fragment.StartTick, fragment.Key);
                if (!touched.Contains(key)) continue;
                SplitCollisionWinner<TItem> winner = winners[key];
                if (!ReferenceEquals(winner.Source, source)
                    || winner.FragmentIndex != fragmentIndex)
                {
                    source.DiscardedFragmentIndices.Add(fragmentIndex);
                }
            }
        }

        List<TItem> displaced = [];
        foreach (TCurrent value in current)
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            TItem item = currentItem(value);
            if (selectedItems.Contains(item)) continue;
            AdvancedNoteKey key = currentKey(value);
            if (!touched.Contains(key)) continue;
            if (!ReferenceEquals(winners[key].CurrentItem, item)) displaced.Add(item);
        }
        return displaced.ToArray();
    }

    private static DirectNoteValue[][] FreezeDirectSplitFragments(
        MidiSegment segment,
        IReadOnlyList<AdvancedDirectNote> selected,
        SplitPlan plan,
        CancellationToken cancellationToken)
    {
        DirectNoteValue[][] result = new DirectNoteValue[selected.Count][];
        HashSet<long> cutTicks = [];
        int visited = 0;
        foreach (SplitSourcePlan source in plan.Sources)
        {
            DirectNoteValue original = selected[source.SourceIndex].Old;
            DirectNoteValue[] fragments = new DirectNoteValue[source.Fragments.Count];
            for (int fragmentIndex = 0; fragmentIndex < fragments.Length; fragmentIndex++)
            {
                PollAdvancedCancellation(cancellationToken, visited++);
                fragments[fragmentIndex] = FromAdvancedDirect(
                    source.Fragments[fragmentIndex],
                    original);
                if (fragmentIndex != 0)
                    cutTicks.Add(source.Fragments[fragmentIndex].StartTick);
            }
            result[source.SourceIndex] = fragments;
        }

        if (cutTicks.Count == 0) return result;

        // Existing same-tick order is immutable source data. Generated split
        // endpoints are therefore appended after its high-water mark, while
        // every internal boundary reserves one adjacent Off -> On pair.
        Dictionary<long, long> nextOrderByTick = cutTicks.ToDictionary(
            static tick => tick,
            static _ => -1L);
        long firstTick = cutTicks.Min();
        long endTick = checked(cutTicks.Max() + 1);

        void Observe(long tick, long order)
        {
            PollAdvancedCancellation(cancellationToken, visited++);
            if (nextOrderByTick.TryGetValue(tick, out long current) && order > current)
                nextOrderByTick[tick] = order;
        }

        foreach (DirectMidiNoteValue value in segment.Notes.QueryStartValues(firstTick, endTick))
            Observe(value.StartTick, value.NoteOnOrder);
        foreach (DirectMidiNoteValue value in segment.Notes.QueryEndValues(firstTick, endTick))
            Observe(checked(value.StartTick + value.LengthTicks), value.NoteOffOrder);
        foreach (var value in segment.ChannelEvents.QueryValues(firstTick, endTick))
            Observe(value.Tick, value.Order);
        foreach (var value in segment.OpaqueEvents.QueryValues(firstTick, endTick))
            Observe(value.Tick, value.Order);

        foreach (SplitSourcePlan source in plan.Sources
            .OrderBy(static value => value.Ordinal)
            .ThenBy(static value => value.SourceIndex))
        {
            DirectNoteValue[] fragments = result[source.SourceIndex];
            for (int fragmentIndex = 1; fragmentIndex < fragments.Length; fragmentIndex++)
            {
                PollAdvancedCancellation(cancellationToken, visited++);
                long tick = source.Fragments[fragmentIndex].StartTick;
                long noteOffOrder = checked(nextOrderByTick[tick] + 1);
                long noteOnOrder = checked(noteOffOrder + 1);
                nextOrderByTick[tick] = noteOnOrder;
                fragments[fragmentIndex - 1] = fragments[fragmentIndex - 1] with
                {
                    NoteOffOrder = noteOffOrder
                };
                fragments[fragmentIndex] = fragments[fragmentIndex] with
                {
                    NoteOnOrder = noteOnOrder
                };
            }
        }
        return result;
    }

    private static IPreparedProjectEdit PrepareLogicalSplit(
        MidoraProject project,
        LogicalTrack track,
        Segment segment,
        AdvancedLogicalNote[] selected,
        SplitPlan plan,
        LogicalNote[] displaced,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp,
        DetachedStableIdAllocator? sharedAllocator)
    {
        MidoraId[] original = selected.Select(static value => value.Note.Id).ToArray();
        LogicalNote[] discardedFirst = plan.Sources
            .Where(static value => value.DiscardedFragmentIndices.Contains(0))
            .Select(value => selected[value.SourceIndex].Note)
            .ToArray();
        LogicalNote[] discarded = discardedFirst.Concat(displaced).Distinct().ToArray();
        bool changed = plan.Sources.Any(static value => value.Fragments.Count > 1)
            || discarded.Length != 0;
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(original);
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
                frozenOriginal,
                publishResult);
        }

        int createdCount = plan.Sources.Sum(source => source.Fragments
            .Select((_, fragmentIndex) => fragmentIndex)
            .Count(fragmentIndex => fragmentIndex != 0
                && !source.DiscardedFragmentIndices.Contains(fragmentIndex)));
        DetachedStableIdReservation reservation = sharedAllocator?.Reserve(createdCount)
            ?? ReserveDetachedStableIds(project, createdCount);
        int[] discardedOrdinals = segment.Notes
            .ResolveByIdsWithIndicesInCollectionOrder(discarded.Select(static value => value.Id).ToArray())
            .Select(static value => value.Index)
            .Order()
            .ToArray();
        Segment replacementRoot = ProjectTimelineOwnerRootClone.CloneLogicalSegment(
            project,
            segment,
            cancellationToken);
        Dictionary<MidoraId, LogicalNote> cloneById = replacementRoot.Notes
            .ResolveByIdsInCollectionOrder(selected.Select(static value => value.Note.Id)
                .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray())
            .ToDictionary(static value => value.Id);
        List<LogicalNote> created = new(createdCount);
        List<LogicalNote>[] createdBySource = new List<LogicalNote>[plan.Sources.Count];
        int createdIndex = 0;
        using (replacementRoot.Notes.BeginBatchChange())
        {
            for (int sourcePlanIndex = 0; sourcePlanIndex < plan.Sources.Count; sourcePlanIndex++)
            {
                SplitSourcePlan source = plan.Sources[sourcePlanIndex];
                List<LogicalNote> sourceCreated = [];
                createdBySource[sourcePlanIndex] = sourceCreated;
                if (!source.DiscardedFragmentIndices.Contains(0))
                {
                    SetLogicalNote(
                        cloneById[selected[source.SourceIndex].Note.Id],
                        FromAdvancedLogical(source.Fragments[0]));
                }
                for (int fragmentIndex = 1; fragmentIndex < source.Fragments.Count; fragmentIndex++)
                {
                    if (source.DiscardedFragmentIndices.Contains(fragmentIndex)) continue;
                    LogicalNote note = new(project, reservation.Ids[createdIndex++]);
                    SetLogicalNote(note, FromAdvancedLogical(source.Fragments[fragmentIndex]));
                    created.Add(note);
                    sourceCreated.Add(note);
                }
            }
            if (discarded.Length != 0)
                _ = replacementRoot.Notes.RemoveRange(discarded.Select(value => cloneById[value.Id]).ToArray());
            int insertedCount = 0;
            for (int sourcePlanIndex = 0; sourcePlanIndex < plan.Sources.Count; sourcePlanIndex++)
            {
                IReadOnlyList<LogicalNote> sourceCreated = createdBySource[sourcePlanIndex];
                if (sourceCreated.Count == 0) continue;
                SplitSourcePlan source = plan.Sources[sourcePlanIndex];
                int sourceOrdinal = selected[source.SourceIndex].Ordinal;
                int insertionIndex = checked(
                    sourceOrdinal + 1
                    - CountAtOrBefore(discardedOrdinals, sourceOrdinal)
                    + insertedCount);
                replacementRoot.Notes.InsertRange(insertionIndex, sourceCreated);
                insertedCount = checked(insertedCount + sourceCreated.Count);
            }
        }
        if (createdIndex != createdCount)
            throw new InvalidOperationException("The detached Logical Note split ID plan is inconsistent.");

        MidoraId[] result = BuildLogicalSplitResultSelection(selected, plan, created);
        ProjectChangeSet changes = TrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(
            changes,
            segment,
            replacementRoot,
            selected.Select(static value => value.Note.Id)
                .Concat(displaced.Select(static value => value.Id))
                .Concat(created.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            segment,
            replacementRoot,
            changes,
            reservation.ExpectedNextStableId,
            reservation.ReplacementNextStableId,
            sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static IPreparedProjectEdit PrepareDirectSplit(
        MidoraProject project,
        PureMidiTrack track,
        MidiSegment segment,
        AdvancedDirectNote[] selected,
        SplitPlan plan,
        IReadOnlyList<DirectNoteValue[]> frozenFragments,
        DirectMidiNote[] displaced,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp,
        DetachedStableIdAllocator? sharedAllocator)
    {
        MidoraId[] original = selected.Select(static value => value.Note.Id).ToArray();
        DirectMidiNote[] discardedFirst = plan.Sources
            .Where(static value => value.DiscardedFragmentIndices.Contains(0))
            .Select(value => selected[value.SourceIndex].Note)
            .ToArray();
        DirectMidiNote[] discarded = discardedFirst.Concat(displaced).Distinct().ToArray();
        bool changed = plan.Sources.Any(static value => value.Fragments.Count > 1)
            || discarded.Length != 0;
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
                frozenOriginal,
                publishResult);
        }

        int createdCount = plan.Sources.Sum(source => source.Fragments
            .Select((_, fragmentIndex) => fragmentIndex)
            .Count(fragmentIndex => fragmentIndex != 0
                && !source.DiscardedFragmentIndices.Contains(fragmentIndex)));
        DetachedStableIdReservation reservation = sharedAllocator?.Reserve(createdCount)
            ?? ReserveDetachedStableIds(project, createdCount);
        MidiSegment replacementRoot = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(
            project,
            segment,
            cancellationToken);
        MidoraId[] affectedIds = selected.Select(static value => value.Note.Id)
            .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, DirectMidiNote> cloneById = replacementRoot.Notes
            .ResolveByIds(affectedIds)
            .ToDictionary(static value => value.Value.Id, static value => value.Value);
        DirectMidiNote[] retained = cloneById.Values.ToArray();
        List<DirectMidiNote> created = new(createdCount);
        int createdIndex = 0;
        using (replacementRoot.Notes.BeginBatchChange(retained))
        {
            foreach (SplitSourcePlan source in plan.Sources)
            {
                if (!source.DiscardedFragmentIndices.Contains(0))
                {
                    ApplyDirectNote(
                        cloneById[selected[source.SourceIndex].Note.Id],
                        frozenFragments[source.SourceIndex][0]);
                }
                for (int fragmentIndex = 1; fragmentIndex < source.Fragments.Count; fragmentIndex++)
                {
                    if (source.DiscardedFragmentIndices.Contains(fragmentIndex)) continue;
                    created.Add(CreateDirectNote(
                        project,
                        reservation.Ids[createdIndex++],
                        frozenFragments[source.SourceIndex][fragmentIndex]));
                }
            }
            if (discarded.Length != 0)
                _ = replacementRoot.Notes.RemoveRange(discarded.Select(value => cloneById[value.Id]).ToArray());
            foreach (DirectMidiNote note in created)
                replacementRoot.Notes.Add(note);
        }
        if (createdIndex != createdCount)
            throw new InvalidOperationException("The detached Direct MIDI Note split ID plan is inconsistent.");

        MidoraId[] result = BuildDirectSplitResultSelection(selected, plan, created);
        ProjectChangeSet changes = PureMidiTrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddDirectNotes(
            changes,
            segment,
            replacementRoot,
            selected.Select(static value => value.Note.Id)
                .Concat(displaced.Select(static value => value.Id))
                .Concat(created.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(
            project,
            track,
            segment,
            replacementRoot,
            changes,
            reservation.ExpectedNextStableId,
            reservation.ReplacementNextStableId,
            sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static IPreparedProjectEdit PrepareTemplateSplit(
        MidoraProject project,
        EventInstrument instrument,
        SubVoice voice,
        AdvancedTemplateNote[] selected,
        SplitPlan plan,
        TemplateEvent[] displaced,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp,
        DetachedStableIdAllocator? sharedAllocator)
    {
        MidoraId[] original = selected.Select(static value => value.Event.Id).ToArray();
        TemplateEvent[] discardedFirst = plan.Sources
            .Where(static value => value.DiscardedFragmentIndices.Contains(0))
            .Select(value => selected[value.SourceIndex].Event)
            .ToArray();
        TemplateEvent[] discarded = discardedFirst.Concat(displaced).Distinct().ToArray();
        bool changed = plan.Sources.Any(static value => value.Fragments.Count > 1)
            || discarded.Length != 0;
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
                frozenOriginal,
                publishResult);
        }

        int createdCount = plan.Sources.Sum(source => source.Fragments
            .Select((_, fragmentIndex) => fragmentIndex)
            .Count(fragmentIndex => fragmentIndex != 0
                && !source.DiscardedFragmentIndices.Contains(fragmentIndex)));
        DetachedStableIdReservation reservation = sharedAllocator?.Reserve(createdCount)
            ?? ReserveDetachedStableIds(project, createdCount);
        int[] discardedOrdinals = voice.Events
            .ResolveByIdsWithIndicesInCollectionOrder(discarded.Select(static value => value.Id).ToArray())
            .Select(static value => value.Index)
            .Order()
            .ToArray();
        SubVoice replacementRoot = ProjectTimelineOwnerRootClone.CloneSubVoice(
            project,
            voice,
            cancellationToken);
        Dictionary<MidoraId, TemplateEvent> cloneById = replacementRoot.Events
            .ResolveByIdsInCollectionOrder(selected.Select(static value => value.Event.Id)
                .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray())
            .ToDictionary(static value => value.Id);
        List<TemplateEvent> created = new(createdCount);
        List<TemplateEvent>[] createdBySource = new List<TemplateEvent>[plan.Sources.Count];
        int createdIndex = 0;
        using (replacementRoot.Events.BeginBatchChange())
        {
            for (int sourcePlanIndex = 0; sourcePlanIndex < plan.Sources.Count; sourcePlanIndex++)
            {
                SplitSourcePlan source = plan.Sources[sourcePlanIndex];
                List<TemplateEvent> sourceCreated = [];
                createdBySource[sourcePlanIndex] = sourceCreated;
                if (!source.DiscardedFragmentIndices.Contains(0))
                {
                    SetTemplateEvent(
                        cloneById[selected[source.SourceIndex].Event.Id],
                        FromAdvancedTemplate(source.Fragments[0], selected[source.SourceIndex].Old));
                }
                for (int fragmentIndex = 1; fragmentIndex < source.Fragments.Count; fragmentIndex++)
                {
                    if (source.DiscardedFragmentIndices.Contains(fragmentIndex)) continue;
                    TemplateEvent note = new(project, reservation.Ids[createdIndex++]);
                    SetTemplateEvent(
                        note,
                        FromAdvancedTemplate(
                            source.Fragments[fragmentIndex],
                            selected[source.SourceIndex].Old));
                    created.Add(note);
                    sourceCreated.Add(note);
                }
            }
            if (discarded.Length != 0)
                _ = replacementRoot.Events.RemoveRange(discarded.Select(value => cloneById[value.Id]).ToArray());
            int insertedCount = 0;
            for (int sourcePlanIndex = 0; sourcePlanIndex < plan.Sources.Count; sourcePlanIndex++)
            {
                IReadOnlyList<TemplateEvent> sourceCreated = createdBySource[sourcePlanIndex];
                if (sourceCreated.Count == 0) continue;
                SplitSourcePlan source = plan.Sources[sourcePlanIndex];
                int sourceOrdinal = selected[source.SourceIndex].Ordinal;
                int insertionIndex = checked(
                    sourceOrdinal + 1
                    - CountAtOrBefore(discardedOrdinals, sourceOrdinal)
                    + insertedCount);
                replacementRoot.Events.InsertRangeWithoutOptionalMappingCreation(
                    insertionIndex,
                    sourceCreated);
                insertedCount = checked(insertedCount + sourceCreated.Count);
            }
        }
        if (createdIndex != createdCount)
            throw new InvalidOperationException("The detached SubVoice Note split ID plan is inconsistent.");

        MidoraId[] result = BuildTemplateSplitResultSelection(selected, plan, created);
        ProjectChangeSet changes = EventInstrumentChange(instrument.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(
            changes,
            voice,
            replacementRoot,
            selected.Select(static value => value.Event.Id)
                .Concat(displaced.Select(static value => value.Id))
                .Concat(created.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(
            project,
            instrument,
            voice,
            replacementRoot,
            changes,
            reservation.ExpectedNextStableId,
            reservation.ReplacementNextStableId,
            sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static MidoraId[] BuildLogicalSplitResultSelection(
        AdvancedLogicalNote[] selected,
        SplitPlan plan,
        IReadOnlyList<LogicalNote> created) =>
        BuildSplitResultSelection(
            plan,
            sourceIndex => selected[sourceIndex].Note.Id,
            created.Select(static value => value.Id).ToArray());

    private static MidoraId[] BuildDirectSplitResultSelection(
        AdvancedDirectNote[] selected,
        SplitPlan plan,
        IReadOnlyList<DirectMidiNote> created) =>
        BuildSplitResultSelection(
            plan,
            sourceIndex => selected[sourceIndex].Note.Id,
            created.Select(static value => value.Id).ToArray());

    private static MidoraId[] BuildTemplateSplitResultSelection(
        AdvancedTemplateNote[] selected,
        SplitPlan plan,
        IReadOnlyList<TemplateEvent> created) =>
        BuildSplitResultSelection(
            plan,
            sourceIndex => selected[sourceIndex].Event.Id,
            created.Select(static value => value.Id).ToArray());

    private static MidoraId[] BuildSplitResultSelection(
        SplitPlan plan,
        Func<int, MidoraId> sourceId,
        IReadOnlyList<MidoraId> createdIds)
    {
        List<MidoraId> result = [];
        int createdIndex = 0;
        foreach (SplitSourcePlan source in plan.Sources)
        {
            for (int fragmentIndex = 0; fragmentIndex < source.Fragments.Count; fragmentIndex++)
            {
                if (source.DiscardedFragmentIndices.Contains(fragmentIndex)) continue;
                result.Add(fragmentIndex == 0
                    ? sourceId(source.SourceIndex)
                    : createdIds[createdIndex++]);
            }
        }
        if (createdIndex != createdIds.Count)
        {
            throw new InvalidOperationException(
                "The split result selection does not match the created fragments.");
        }
        return result.ToArray();
    }

    private static IPreparedProjectEdit PrepareLogicalJoin(
        MidoraProject project,
        LogicalTrack track,
        Segment segment,
        AdvancedLogicalNote[] selected,
        AdvancedLogicalNoteEdit[] edits,
        LogicalNote[] removed,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        MidoraId[] original = selected.Select(static value => value.Note.Id).ToArray();
        MidoraId[] result = edits.Select(static value => value.Selected.Note.Id).ToArray();
        bool changed = removed.Length != 0;
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(original);
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
                result,
                publishResult);
        }

        Segment replacementRoot = ProjectTimelineOwnerRootClone.CloneLogicalSegment(
            project,
            segment,
            cancellationToken);
        MidoraId[] affectedIds = selected.Select(static value => value.Note.Id)
            .Concat(removed.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, LogicalNote> cloneById = replacementRoot.Notes
            .ResolveByIdsInCollectionOrder(affectedIds)
            .ToDictionary(static value => value.Id);
        using (replacementRoot.Notes.BeginBatchChange())
        {
            foreach (AdvancedLogicalNoteEdit edit in edits)
                SetLogicalNote(cloneById[edit.Selected.Note.Id], edit.Replacement);
            _ = replacementRoot.Notes.RemoveRange(
                removed.Select(value => cloneById[value.Id]).ToArray());
        }
        ProjectChangeSet changes = TrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(
            changes,
            segment,
            replacementRoot,
            selected.Select(static value => value.Note.Id)
                .Concat(removed.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(
            project,
            track,
            segment,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static IPreparedProjectEdit PrepareDirectJoin(
        MidoraProject project,
        PureMidiTrack track,
        MidiSegment segment,
        AdvancedDirectNote[] selected,
        AdvancedDirectNoteEdit[] edits,
        DirectMidiNote[] removed,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        MidoraId[] original = selected.Select(static value => value.Note.Id).ToArray();
        MidoraId[] result = edits.Select(static value => value.Selected.Note.Id).ToArray();
        bool changed = removed.Length != 0;
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
        MidoraId[] affectedIds = selected.Select(static value => value.Note.Id)
            .Concat(removed.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, DirectMidiNote> cloneById = replacementRoot.Notes
            .ResolveByIds(affectedIds)
            .ToDictionary(static value => value.Value.Id, static value => value.Value);
        DirectMidiNote[] retained = cloneById.Values.ToArray();
        using (replacementRoot.Notes.BeginBatchChange(retained))
        {
            foreach (AdvancedDirectNoteEdit edit in edits)
                ApplyDirectNote(cloneById[edit.Selected.Note.Id], edit.Replacement);
            _ = replacementRoot.Notes.RemoveRange(
                removed.Select(value => cloneById[value.Id]).ToArray());
        }
        ProjectChangeSet changes = PureMidiTrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddDirectNotes(
            changes,
            segment,
            replacementRoot,
            selected.Select(static value => value.Note.Id)
                .Concat(removed.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(
            project,
            track,
            segment,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static IPreparedProjectEdit PrepareTemplateJoin(
        MidoraProject project,
        EventInstrument instrument,
        SubVoice voice,
        AdvancedTemplateNote[] selected,
        AdvancedTemplateNoteEdit[] edits,
        TemplateEvent[] removed,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        MidoraId[] original = selected.Select(static value => value.Event.Id).ToArray();
        MidoraId[] result = edits.Select(static value => value.Selected.Event.Id).ToArray();
        bool changed = removed.Length != 0;
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
        MidoraId[] affectedIds = selected.Select(static value => value.Event.Id)
            .Concat(removed.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, TemplateEvent> cloneById = replacementRoot.Events
            .ResolveByIdsInCollectionOrder(affectedIds)
            .ToDictionary(static value => value.Id);
        using (replacementRoot.Events.BeginBatchChange())
        {
            foreach (AdvancedTemplateNoteEdit edit in edits)
                SetTemplateEvent(cloneById[edit.Selected.Event.Id], edit.Replacement);
            _ = replacementRoot.Events.RemoveRange(
                removed.Select(value => cloneById[value.Id]).ToArray());
        }
        ProjectChangeSet changes = EventInstrumentChange(instrument.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(
            changes,
            voice,
            replacementRoot,
            selected.Select(static value => value.Event.Id)
                .Concat(removed.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(
            project,
            instrument,
            voice,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(rootSwap, frozenOriginal, result, publishResult);
    }

    private static void ValidateHumanizeField(TimelineHumanizeField field, string parameterName)
    {
        if (!Enum.IsDefined(field.Mode))
            throw new ArgumentOutOfRangeException(parameterName);
        switch (field.Mode)
        {
            case TimelineHumanizeMode.Disabled:
                return;
            case TimelineHumanizeMode.Add:
            case TimelineHumanizeMode.Override:
                if (field.IntegerMinimum > field.IntegerMaximum)
                    throw new ArgumentException("Humanize integer ranges must be ordered.", parameterName);
                return;
            case TimelineHumanizeMode.Multiply:
                if (!double.IsFinite(field.FactorMinimum)
                    || !double.IsFinite(field.FactorMaximum)
                    || field.FactorMinimum > field.FactorMaximum)
                {
                    throw new ArgumentException(
                        "Humanize multiply ranges must be finite and ordered.",
                        parameterName);
                }
                if (field.FactorMinimum < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        parameterName,
                        "Humanize multiply factors cannot be negative.");
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static AdvancedNoteValue HumanizeNote(
        AdvancedNoteValue old,
        MidoraId ownerId,
        AdvancedFormalOrder formalOrder,
        TimelineHumanizeOptions options,
        ulong seed,
        long minimumStart,
        long? exclusiveMaximumStart)
    {
        long rawTick = HumanizeInteger(
            old.StartTick,
            options.Tick,
            CreateHumanizeRandom(seed, ownerId, formalOrder, 0));
        long rawGate = HumanizeInteger(
            old.LengthTicks,
            options.Gate,
            CreateHumanizeRandom(seed, ownerId, formalOrder, 1));
        long rawVelocity = HumanizeInteger(
            old.Velocity,
            options.Velocity,
            CreateHumanizeRandom(seed, ownerId, formalOrder, 2));

        // Generate every raw field before applying the fixed normalization order.
        long tick = rawTick;
        long gate = Math.Max(1, rawGate);
        int velocity = checked((int)Math.Clamp(rawVelocity, 1, 127));
        if (tick >= minimumStart
            && (exclusiveMaximumStart is null || tick < exclusiveMaximumStart.Value))
        {
            long maximumGate = exclusiveMaximumStart is long end
                ? checked(end - tick)
                : checked(long.MaxValue - tick);
            if (maximumGate < 1)
            {
                throw new OverflowException(
                    "The Humanize result leaves no representable positive Note Gate.");
            }
            gate = Math.Clamp(gate, 1, maximumGate);
            _ = checked(tick + gate);
        }
        return old with { StartTick = tick, LengthTicks = gate, Velocity = velocity };
    }

    private static AdvancedNoteValue NormalizeHumanizedGate(
        AdvancedNoteValue value,
        long? exclusiveEnd)
    {
        long maximumGate = exclusiveEnd is long end
            ? checked(end - value.StartTick)
            : checked(long.MaxValue - value.StartTick);
        if (maximumGate < 1)
        {
            throw new OverflowException(
                "The Humanize result leaves no representable positive Note Gate.");
        }
        long length = Math.Clamp(value.LengthTicks, 1, maximumGate);
        _ = checked(value.StartTick + length);
        return value with { LengthTicks = length };
    }

    private static long HumanizeInteger(
        long old,
        TimelineHumanizeField field,
        FixedHumanizeRandom random) => field.Mode switch
        {
            TimelineHumanizeMode.Disabled => old,
            TimelineHumanizeMode.Add => checked(old + random.NextInclusive(
                field.IntegerMinimum,
                field.IntegerMaximum)),
            TimelineHumanizeMode.Multiply => RoundFiniteInt64(
                old * random.NextContinuous(field.FactorMinimum, field.FactorMaximum)),
            TimelineHumanizeMode.Override => random.NextInclusive(
                field.IntegerMinimum,
                field.IntegerMaximum),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

    private static long RoundFiniteInt64(double value)
    {
        if (!double.IsFinite(value))
            throw new OverflowException("A Humanize calculation produced a non-finite value.");
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < long.MinValue || rounded >= long.MaxValue)
            throw new OverflowException("A Humanize calculation exceeds Int64.");
        return checked((long)rounded);
    }

    private static FixedHumanizeRandom CreateHumanizeRandom(
        ulong seed,
        MidoraId ownerId,
        AdvancedFormalOrder formalOrder,
        int fieldKind)
    {
        ulong state = seed;
        state = MixHumanize(state ^ unchecked((ulong)ownerId.Value));
        state = MixHumanize(state ^ unchecked((ulong)formalOrder.ExplicitOrder));
        if (formalOrder.StableId != default)
            state = MixHumanize(state ^ unchecked((ulong)formalOrder.StableId.Value));
        state = MixHumanize(state ^ unchecked((ulong)fieldKind));
        return new(state);
    }

    private static ulong MixHumanize(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private sealed class FixedHumanizeRandom(ulong state)
    {
        private ulong _state = state;

        public ulong NextUInt64()
        {
            _state += 0x9e3779b97f4a7c15UL;
            ulong value = _state;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }

        public long NextInclusive(long minimum, long maximum)
        {
            ulong width = unchecked((ulong)(maximum - minimum)) + 1UL;
            ulong offset;
            if (width == 0)
            {
                offset = NextUInt64();
            }
            else
            {
                ulong threshold = unchecked(0UL - width) % width;
                ulong sample;
                do sample = NextUInt64(); while (sample < threshold);
                offset = sample % width;
            }
            return unchecked(minimum + (long)offset);
        }

        public double NextContinuous(double minimum, double maximum)
        {
            if (minimum == maximum) return minimum;
            double unit = (NextUInt64() >> 11) * (1d / (1UL << 53));
            return minimum + (maximum - minimum) * unit;
        }
    }

    private static AdvancedNoteValue ToAdvanced(LogicalNoteValue value) =>
        new(value.StartTick, value.LengthTicks, value.Note, value.Velocity);

    private static AdvancedNoteValue ToAdvanced(DirectNoteValue value) =>
        new(value.StartTick, value.LengthTicks, value.Key, value.NoteOnVelocity);

    private static AdvancedNoteValue ToAdvanced(TemplateEventValue value) =>
        new(value.Tick, value.LengthTicks, value.Number, value.Value);

    private static LogicalNoteValue FromAdvancedLogical(AdvancedNoteValue value) =>
        new(value.StartTick, value.LengthTicks, value.Key, value.Velocity);

    private static DirectNoteValue FromAdvancedDirect(
        AdvancedNoteValue value,
        DirectNoteValue source) => source with
        {
            StartTick = value.StartTick,
            LengthTicks = value.LengthTicks,
            Key = value.Key,
            NoteOnVelocity = value.Velocity
        };

    private static TemplateEventValue FromAdvancedTemplate(
        AdvancedNoteValue value,
        TemplateEventValue source) => source with
        {
            Tick = value.StartTick,
            LengthTicks = value.LengthTicks,
            Number = value.Key,
            Value = value.Velocity
        };

    private static AdvancedLogicalNote[] SelectAdvancedLogicalNotes(
        Segment segment,
        IReadOnlyCollection<MidoraId> ids)
    {
        IReadOnlySet<MidoraId> requested = ValidateTimelineSelectionIds(
            ids,
            nameof(ids),
            "Logical Note");
        AdvancedLogicalNote[] result = segment.Notes
            .ResolveByIdsWithIndicesInCollectionOrder(requested)
            .Select(static value => new AdvancedLogicalNote(
                value.Value,
                value.Index,
                Snapshot(value.Value)))
            .ToArray();
        if (result.Length != requested.Count)
            throw new ArgumentException(
                "Every selected ID must identify a Logical Note in the target Segment.",
                nameof(ids));
        return result;
    }

    private static AdvancedDirectNote[] SelectAdvancedDirectNotes(
        MidiSegment segment,
        IReadOnlyCollection<MidoraId> ids)
    {
        IReadOnlySet<MidoraId> requested = ValidateTimelineSelectionIds(
            ids,
            nameof(ids),
            "Direct MIDI Note");
        AdvancedDirectNote[] result = segment.Notes.ResolveByIds(requested)
            .OrderBy(static value => value.Value.NoteOnOrder)
            .ThenBy(static value => value.Value.Id)
            .Select(static value => new AdvancedDirectNote(
                value.Value,
                new AdvancedFormalOrder(value.Value.NoteOnOrder, value.Value.Id),
                SnapshotDirectNote(value.Value)))
            .ToArray();
        if (result.Length != requested.Count)
            throw new ArgumentException(
                "Every selected ID must identify a Direct MIDI Note in the target Segment.",
                nameof(ids));
        return result;
    }

    private static AdvancedTemplateNote[] SelectAdvancedTemplateNotes(
        SubVoice voice,
        IReadOnlyCollection<MidoraId> ids)
    {
        IReadOnlySet<MidoraId> requested = ValidateTimelineSelectionIds(
            ids,
            nameof(ids),
            "Template Note");
        AdvancedTemplateNote[] result = voice.Events
            .ResolveByIdsWithIndicesInCollectionOrder(requested)
            .Select(static value => new AdvancedTemplateNote(
                value.Value,
                value.Index,
                CaptureTemplateEvent(value.Value)))
            .ToArray();
        if (result.Length != requested.Count
            || result.Any(static value => value.Event.Kind != TemplateEventKind.Note))
        {
            throw new ArgumentException(
                "Every selected ID must identify a Template Note in the target SubVoice.",
                nameof(ids));
        }
        return result;
    }

    private static void ResolveLogicalNoteCollisions(
        Segment segment,
        AdvancedLogicalNoteEdit[] edits,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> targets = edits
            .Where(static value => !value.Discard)
            .Select(static value => new AdvancedNoteKey(
                value.Replacement.StartTick,
                value.Replacement.Note))
            .ToHashSet();
        if (targets.Count == 0) return;
        HashSet<TimelineStartLaneKey> query = targets
            .Select(static value => new TimelineStartLaneKey(value.Tick, value.Key))
            .ToHashSet();
        MidoraId[] ids = segment.Notes.CreateQuerySnapshot().QueryStartKeys(query)
            .Select(static value => value.Id).ToArray();
        var current = segment.Notes.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static value => (Item: value.Value, Ordinal: value.Index,
                Key: new AdvancedNoteKey(value.Value.StartTick, value.Value.Note)))
            .ToArray();
        ResolveNoteCollisionCore(
            edits,
            static value => value.Selected.Note,
            static value => new(value.Replacement.StartTick, value.Replacement.Note),
            static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Selected.Ordinal),
            current,
            static value => value.Item,
            static value => value.Key,
            static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal),
            static value => value.Discard,
            static (value, discard) => value.Discard = discard,
            static (value, displaced) => value.Displaced.Add(displaced),
            cancellationToken);
    }

    private static void ResolveDirectNoteCollisions(
        MidiSegment segment,
        AdvancedDirectNoteEdit[] edits,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> targets = edits
            .Where(static value => !value.Discard)
            .Select(static value => new AdvancedNoteKey(
                value.Replacement.StartTick,
                value.Replacement.Key))
            .ToHashSet();
        if (targets.Count == 0) return;
        HashSet<DirectMidiNoteStartKey> query = targets
            .Select(static value => new DirectMidiNoteStartKey(value.Tick, value.Key))
            .ToHashSet();
        DirectMidiNote[] values = segment.Notes.QueryStartKeys(query).ToArray();
        var current = segment.Notes.ResolveByIds(values.Select(static value => value.Id).ToArray())
            .Select(static value => (Item: value.Value, FormalOrder: new AdvancedFormalOrder(
                    value.Value.NoteOnOrder,
                    value.Value.Id),
                Key: new AdvancedNoteKey(value.Value.StartTick, value.Value.Key)))
            .ToArray();
        ResolveNoteCollisionCore(
            edits,
            static value => value.Selected.Note,
            static value => new(value.Replacement.StartTick, value.Replacement.Key),
            static value => value.Selected.FormalOrder,
            current,
            static value => value.Item,
            static value => value.Key,
            static value => value.FormalOrder,
            static value => value.Discard,
            static (value, discard) => value.Discard = discard,
            static (value, displaced) => value.Displaced.Add(displaced),
            cancellationToken);
    }

    private static void ResolveTemplateNoteCollisions(
        SubVoice voice,
        AdvancedTemplateNoteEdit[] edits,
        CancellationToken cancellationToken)
    {
        HashSet<AdvancedNoteKey> targets = edits
            .Where(static value => !value.Discard)
            .Select(static value => new AdvancedNoteKey(
                value.Replacement.Tick,
                value.Replacement.Number))
            .ToHashSet();
        if (targets.Count == 0) return;
        HashSet<TimelineStartLaneKey> query = targets
            .Select(static value => new TimelineStartLaneKey(value.Tick, value.Key))
            .ToHashSet();
        MidoraId[] ids = voice.Events.CreateQuerySnapshot().QueryNoteStartKeys(query)
            .Select(static value => value.Id).ToArray();
        var current = voice.Events.ResolveByIdsWithIndicesInCollectionOrder(ids)
            .Select(static value => (Item: value.Value, Ordinal: value.Index,
                Key: new AdvancedNoteKey(value.Value.Tick, value.Value.Number)))
            .ToArray();
        ResolveNoteCollisionCore(
            edits,
            static value => value.Selected.Event,
            static value => new(value.Replacement.Tick, value.Replacement.Number),
            static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Selected.Ordinal),
            current,
            static value => value.Item,
            static value => value.Key,
            static value => AdvancedFormalOrder.FromCollectionOrdinal(value.Ordinal),
            static value => value.Discard,
            static (value, discard) => value.Discard = discard,
            static (value, displaced) => value.Displaced.Add(displaced),
            cancellationToken);
    }

    private static void ResolveNoteCollisionCore<TEdit, TItem, TCurrent>(
        TEdit[] edits,
        Func<TEdit, TItem> editItem,
        Func<TEdit, AdvancedNoteKey> newKey,
        Func<TEdit, AdvancedFormalOrder> editOrdinal,
        IReadOnlyCollection<TCurrent> current,
        Func<TCurrent, TItem> currentItem,
        Func<TCurrent, AdvancedNoteKey> currentKey,
        Func<TCurrent, AdvancedFormalOrder> currentOrdinal,
        Func<TEdit, bool> isDiscarded,
        Action<TEdit, bool> setDiscarded,
        Action<TEdit, TItem> addDisplaced,
        CancellationToken cancellationToken)
        where TItem : class
        where TEdit : class
    {
        Dictionary<TItem, TEdit> selected = [];
        HashSet<AdvancedNoteKey> touched = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollAdvancedCancellation(cancellationToken, index);
            TEdit edit = edits[index];
            selected.Add(editItem(edit), edit);
            if (!isDiscarded(edit))
                touched.Add(newKey(edit));
        }
        if (touched.Count == 0) return;

        Dictionary<AdvancedNoteKey, (AdvancedFormalOrder FormalOrder, TEdit? Edit, TItem? Item)> winners = [];
        for (int index = 0; index < edits.Length; index++)
        {
            PollAdvancedCancellation(cancellationToken, index);
            TEdit edit = edits[index];
            AdvancedNoteKey key = newKey(edit);
            if (isDiscarded(edit) || !touched.Contains(key)) continue;
            AdvancedFormalOrder formalOrder = editOrdinal(edit);
            if (!winners.TryGetValue(key, out var winner)
                || formalOrder.CompareTo(winner.FormalOrder) < 0)
            {
                winners[key] = (formalOrder, edit, null);
            }
        }

        int visited = 0;
        foreach (TCurrent value in current)
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            AdvancedNoteKey key = currentKey(value);
            if (!touched.Contains(key)) continue;
            TItem item = currentItem(value);
            // A selected object's current position is not a final-state candidate. Its
            // replacement (including a stationary replacement) was considered above.
            if (selected.ContainsKey(item)) continue;
            AdvancedFormalOrder formalOrder = currentOrdinal(value);
            if (!winners.TryGetValue(key, out var winner)
                || formalOrder.CompareTo(winner.FormalOrder) < 0)
            {
                winners[key] = (formalOrder, null, item);
            }
        }

        int resolved = 0;
        foreach (TEdit edit in edits)
        {
            PollAdvancedCancellation(cancellationToken, resolved++);
            AdvancedNoteKey key = newKey(edit);
            if (isDiscarded(edit) || !touched.Contains(key)) continue;
            if (!ReferenceEquals(winners[key].Edit, edit)) setDiscarded(edit, true);
        }

        TEdit displacementOwner = edits[0];
        foreach (TCurrent value in current)
        {
            PollAdvancedCancellation(cancellationToken, resolved++);
            AdvancedNoteKey key = currentKey(value);
            if (!touched.Contains(key)) continue;
            TItem item = currentItem(value);
            if (selected.ContainsKey(item)) continue;
            if (!ReferenceEquals(winners[key].Item, item))
                addDisplaced(displacementOwner, item);
        }
    }

    private static void PollAdvancedCancellation(CancellationToken cancellationToken, int index)
    {
        if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
    }

    private static IPreparedProjectEdit PrepareLogicalAdvancedValueEdit(
        MidoraProject project,
        LogicalTrack track,
        Segment segment,
        AdvancedLogicalNoteEdit[] edits,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        LogicalNote[] discarded = edits.Where(static value => value.Discard)
            .Select(static value => value.Selected.Note)
            .Concat(edits.SelectMany(static value => value.Displaced))
            .Distinct()
            .ToArray();
        MidoraId[] originalSelection = edits.Select(static value => value.Selected.Note.Id).ToArray();
        MidoraId[] resultSelection = edits.Where(static value => !value.Discard)
            .Select(static value => value.Selected.Note.Id).ToArray();
        bool changed = discarded.Length != 0 || edits.Any(static value =>
            value.Selected.Old != value.Replacement);
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
        MidoraId[] affectedIds = edits.Select(static value => value.Selected.Note.Id)
            .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, LogicalNote> cloneById = replacementRoot.Notes
            .ResolveByIdsInCollectionOrder(affectedIds)
            .ToDictionary(static value => value.Id);
        using (replacementRoot.Notes.BeginBatchChange())
        {
            foreach (AdvancedLogicalNoteEdit edit in edits)
            {
                if (!edit.Discard)
                    SetLogicalNote(cloneById[edit.Selected.Note.Id], edit.Replacement);
            }
            if (discarded.Length != 0)
                _ = replacementRoot.Notes.RemoveRange(
                    discarded.Select(value => cloneById[value.Id]).ToArray());
        }
        ProjectChangeSet changes = TrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(
            changes,
            segment,
            replacementRoot,
            edits.Select(static value => value.Selected.Note.Id)
                .Concat(discarded.Select(static value => value.Id)));
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

    private static IPreparedProjectEdit PrepareDirectAdvancedValueEdit(
        MidoraProject project,
        PureMidiTrack track,
        MidiSegment segment,
        AdvancedDirectNoteEdit[] edits,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        DirectMidiNote[] discarded = edits.Where(static value => value.Discard)
            .Select(static value => value.Selected.Note)
            .Concat(edits.SelectMany(static value => value.Displaced))
            .Distinct()
            .ToArray();
        MidoraId[] originalSelection = edits.Select(static value => value.Selected.Note.Id).ToArray();
        MidoraId[] resultSelection = edits.Where(static value => !value.Discard)
            .Select(static value => value.Selected.Note.Id).ToArray();
        bool changed = discarded.Length != 0 || edits.Any(static value =>
            value.Selected.Old != value.Replacement);
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(originalSelection);
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
                resultSelection,
                publishResult);
        }

        MidiSegment replacementRoot = ProjectTimelineOwnerRootClone.CloneDirectMidiSegment(
            project,
            segment,
            cancellationToken);
        MidoraId[] affectedIds = edits.Select(static value => value.Selected.Note.Id)
            .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, DirectMidiNote> cloneById = replacementRoot.Notes
            .ResolveByIds(affectedIds)
            .ToDictionary(static value => value.Value.Id, static value => value.Value);
        DirectMidiNote[] retained = cloneById.Values.ToArray();
        using (replacementRoot.Notes.BeginBatchChange(retained))
        {
            foreach (AdvancedDirectNoteEdit edit in edits)
            {
                if (!edit.Discard)
                    ApplyDirectNote(cloneById[edit.Selected.Note.Id], edit.Replacement);
            }
            if (discarded.Length != 0)
                _ = replacementRoot.Notes.RemoveRange(
                    discarded.Select(value => cloneById[value.Id]).ToArray());
        }
        ProjectChangeSet changes = PureMidiTrackChange(track.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddDirectNotes(
            changes,
            segment,
            replacementRoot,
            edits.Select(static value => value.Selected.Note.Id)
                .Concat(discarded.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareDirectMidiSegment(
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

    private static IPreparedProjectEdit PrepareTemplateAdvancedValueEdit(
        MidoraProject project,
        EventInstrument instrument,
        SubVoice voice,
        AdvancedTemplateNoteEdit[] edits,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        ProjectTimelineOwnerSourceStamp sourceStamp)
    {
        TemplateEvent[] discarded = edits.Where(static value => value.Discard)
            .Select(static value => value.Selected.Event)
            .Concat(edits.SelectMany(static value => value.Displaced))
            .Distinct()
            .ToArray();
        MidoraId[] originalSelection = edits.Select(static value => value.Selected.Event.Id).ToArray();
        MidoraId[] resultSelection = edits.Where(static value => !value.Discard)
            .Select(static value => value.Selected.Event.Id).ToArray();
        bool changed = discarded.Length != 0 || edits.Any(static value =>
            value.Selected.Old != value.Replacement);
        IReadOnlyList<MidoraId> frozenOriginal = publishResult(originalSelection);
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
                resultSelection,
                publishResult);
        }

        SubVoice replacementRoot = ProjectTimelineOwnerRootClone.CloneSubVoice(
            project,
            voice,
            cancellationToken);
        MidoraId[] affectedIds = edits.Select(static value => value.Selected.Event.Id)
            .Concat(discarded.Select(static value => value.Id)).Distinct().ToArray();
        Dictionary<MidoraId, TemplateEvent> cloneById = replacementRoot.Events
            .ResolveByIdsInCollectionOrder(affectedIds)
            .ToDictionary(static value => value.Id);
        using (replacementRoot.Events.BeginBatchChange())
        {
            foreach (AdvancedTemplateNoteEdit edit in edits)
            {
                if (!edit.Discard)
                    SetTemplateEvent(cloneById[edit.Selected.Event.Id], edit.Replacement);
            }
            if (discarded.Length != 0)
                _ = replacementRoot.Events.RemoveRange(
                    discarded.Select(value => cloneById[value.Id]).ToArray());
        }
        ProjectChangeSet changes = EventInstrumentChange(instrument.Id);
        ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(
            changes,
            voice,
            replacementRoot,
            edits.Select(static value => value.Selected.Event.Id)
                .Concat(discarded.Select(static value => value.Id)));
        IPreparedProjectEdit rootSwap = ProjectTimelineOwnerRootReplacement.PrepareSubVoice(
            project,
            instrument,
            voice,
            replacementRoot,
            changes,
            expectedSourceStamp: sourceStamp);
        return WithSelectionPublication(
            rootSwap,
            frozenOriginal,
            resultSelection,
            publishResult);
    }

    private static ITimelineSelectionResultEditCommand ResultCommand(
        string name,
        Func<MidoraProject, SelectionPublisher, CancellationToken,
            IPreparedProjectEdit> prepare) =>
        new TimelineSelectionResultCommand(
            name,
            (project, publishResult, cancellationToken, _) =>
                prepare(project, publishResult, cancellationToken));

    private static ITimelineSelectionResultEditCommand ResultCommand(
        string name,
        Func<MidoraProject, SelectionPublisher, CancellationToken,
            IProgress<TimelineEditPreparationProgress>?, IPreparedProjectEdit> prepare) =>
        new TimelineSelectionResultCommand(name, prepare);

    private sealed class TimelineSelectionResultCommand(
        string name,
        Func<MidoraProject, SelectionPublisher, CancellationToken,
            IProgress<TimelineEditPreparationProgress>?, IPreparedProjectEdit> prepare)
        : ITimelineSelectionResultEditCommand, IProgressReportingProjectEditCommand
    {
        private IReadOnlyList<MidoraId> _result = Array.Empty<MidoraId>();

        public string Name { get; } = name;
        public IReadOnlyList<MidoraId> ResultSelectionIds => _result;

        public IPreparedProjectEdit Prepare(MidoraProject project) =>
            Prepare(project, CancellationToken.None);

        public IPreparedProjectEdit Prepare(
            MidoraProject project,
            CancellationToken cancellationToken) =>
            Prepare(project, cancellationToken, progress: null);

        public IPreparedProjectEdit Prepare(
            MidoraProject project,
            CancellationToken cancellationToken,
            IProgress<TimelineEditPreparationProgress>? progress) =>
            prepare(
                project,
                PublishResult,
                cancellationToken,
                progress);

        private IReadOnlyList<MidoraId> PublishResult(IReadOnlyList<MidoraId> value)
        {
            IReadOnlyList<MidoraId> frozen = CompactMidoraIdList.Freeze(value);
            _result = frozen;
            return frozen;
        }
    }

    private delegate IReadOnlyList<MidoraId> SelectionPublisher(
        IReadOnlyList<MidoraId> value);

    private static void ReportPreparationProgress(
        IProgress<TimelineEditPreparationProgress>? progress,
        TimelineEditPreparationPhase phase,
        long completed,
        long total) =>
        progress?.Report(new(phase, completed, total));

    private static IReadOnlySet<MidoraId> ValidateTimelineSelectionIds(
        IReadOnlyCollection<MidoraId> values,
        string parameterName,
        string objectName)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException(
                $"At least one {objectName} must be selected.",
                parameterName);
        }
        if (values is not IReadOnlySet<MidoraId> set)
            return ValidateBatchIds(values, parameterName, objectName);
        int observed = 0;
        foreach (MidoraId value in set)
        {
            if (value == default)
            {
                throw new ArgumentException(
                    $"{objectName} selections must contain distinct valid stable IDs.",
                    parameterName);
            }
            observed = checked(observed + 1);
        }
        if (observed != values.Count)
        {
            throw new ArgumentException(
                $"{objectName} selections must contain distinct valid stable IDs.",
                parameterName);
        }
        return set;
    }

    private readonly record struct AdvancedNoteValue(
        long StartTick,
        long LengthTicks,
        int Key,
        int Velocity);
    private readonly record struct AdvancedNoteKey(long Tick, int Key);
    private readonly record struct AdvancedLogicalNote(
        LogicalNote Note,
        int Ordinal,
        LogicalNoteValue Old);
    private sealed class AdvancedLogicalNoteEdit(
        AdvancedLogicalNote selected,
        LogicalNoteValue replacement,
        bool discard)
    {
        public AdvancedLogicalNote Selected { get; } = selected;
        public LogicalNoteValue Replacement { get; } = replacement;
        public bool Discard { get; set; } = discard;
        public List<LogicalNote> Displaced { get; } = [];
    }
    private readonly record struct AdvancedDirectNote(
        DirectMidiNote Note,
        AdvancedFormalOrder FormalOrder,
        DirectNoteValue Old);
    private sealed class AdvancedDirectNoteEdit(
        AdvancedDirectNote selected,
        DirectNoteValue replacement,
        bool discard)
    {
        public AdvancedDirectNote Selected { get; } = selected;
        public DirectNoteValue Replacement { get; } = replacement;
        public bool Discard { get; set; } = discard;
        public List<DirectMidiNote> Displaced { get; } = [];
    }
    private readonly record struct AdvancedTemplateNote(
        TemplateEvent Event,
        int Ordinal,
        TemplateEventValue Old);
    private sealed class AdvancedTemplateNoteEdit(
        AdvancedTemplateNote selected,
        TemplateEventValue replacement,
        bool discard)
    {
        public AdvancedTemplateNote Selected { get; } = selected;
        public TemplateEventValue Replacement { get; } = replacement;
        public bool Discard { get; set; } = discard;
        public List<TemplateEvent> Displaced { get; } = [];
    }
    private sealed record SplitPlan(
        long Left,
        long Right,
        IReadOnlyList<long> Knives,
        IReadOnlyList<SplitSourcePlan> Sources);
    private sealed record SplitSourcePlan(
        int SourceIndex,
        AdvancedFormalOrder Ordinal,
        IReadOnlyList<AdvancedNoteValue> Fragments)
    {
        public HashSet<int> DiscardedFragmentIndices { get; } = [];
    }
    private readonly record struct SplitFragmentFormalOrder(
        AdvancedFormalOrder SourceOrdinal,
        int FragmentOrdinal) : IComparable<SplitFragmentFormalOrder>
    {
        public int CompareTo(SplitFragmentFormalOrder other)
        {
            int source = SourceOrdinal.CompareTo(other.SourceOrdinal);
            return source != 0 ? source : FragmentOrdinal.CompareTo(other.FragmentOrdinal);
        }
    }
    private readonly record struct AdvancedFormalOrder(
        long ExplicitOrder,
        MidoraId StableId) : IComparable<AdvancedFormalOrder>
    {
        public static AdvancedFormalOrder FromCollectionOrdinal(int ordinal) =>
            new(ordinal, default);

        public int CompareTo(AdvancedFormalOrder other)
        {
            int order = ExplicitOrder.CompareTo(other.ExplicitOrder);
            return order != 0 ? order : StableId.CompareTo(other.StableId);
        }
    }
    private sealed record SplitCollisionWinner<TItem>(
        SplitFragmentFormalOrder Order,
        SplitSourcePlan? Source,
        int FragmentIndex,
        TItem? CurrentItem)
        where TItem : class;
    private sealed record JoinPlan(
        IReadOnlyList<JoinRun> Runs,
        IReadOnlyList<int> RemovedSourceIndices);
    private sealed record JoinRun(
        int FirstSourceIndex,
        int LastSourceIndex,
        AdvancedNoteValue Value);
}
