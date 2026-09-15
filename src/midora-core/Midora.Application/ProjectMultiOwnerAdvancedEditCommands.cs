using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static ITimelineSelectionResultEditCommand SplitLogicalNotes(
        IReadOnlyCollection<LogicalNoteOwnerSelection> owners,
        NoteSplitOptions options)
    {
        LogicalNoteOwnerSelection[] frozen = FreezeLogicalOwners(owners, static value => value.NoteIds);
        ArgumentNullException.ThrowIfNull(options);
        return MultiOwnerSplitResultCommand(
            "Split logical notes",
            allocator => frozen.Select(value => SplitLogicalNotesCore(
                value.SegmentId,
                value.NoteIds,
                options,
                allocator)).ToArray());
    }

    public static ITimelineSelectionResultEditCommand SplitDirectMidiNotes(
        IReadOnlyCollection<DirectMidiNoteOwnerSelection> owners,
        NoteSplitOptions options)
    {
        DirectMidiNoteOwnerSelection[] frozen = FreezeDirectOwners(owners, static value => value.NoteIds);
        ArgumentNullException.ThrowIfNull(options);
        return MultiOwnerSplitResultCommand(
            "Split Direct MIDI notes",
            allocator => frozen.Select(value => SplitDirectMidiNotesCore(
                value.SegmentId,
                value.NoteIds,
                options,
                allocator)).ToArray());
    }

    public static ITimelineSelectionResultEditCommand SplitTemplateNotes(
        IReadOnlyCollection<TemplateNoteOwnerSelection> owners,
        NoteSplitOptions options)
    {
        TemplateNoteOwnerSelection[] frozen = FreezeTemplateOwners(owners, static value => value.NoteIds);
        ArgumentNullException.ThrowIfNull(options);
        return MultiOwnerSplitResultCommand(
            "Split SubVoice notes",
            allocator => frozen.Select(value => SplitTemplateNotesCore(
                value.EventInstrumentId,
                value.SubVoiceId,
                value.NoteIds,
                options,
                allocator)).ToArray());
    }

    public static ITimelineSelectionResultEditCommand HumanizeLogicalNotes(
        IReadOnlyCollection<LogicalNoteOwnerSelection> owners,
        TimelineHumanizeOptions options)
    {
        LogicalNoteOwnerSelection[] frozen = FreezeLogicalOwners(owners, static value => value.NoteIds);
        ArgumentNullException.ThrowIfNull(options);
        return MultiOwnerResultCommand(
            "Humanize logical notes",
            (project, _) =>
            {
                TimelineHumanizeOptions resolved = options with { Seed = ResolveHumanizeSeed(options) };
                return frozen.Select(value => HumanizeLogicalNotes(
                    value.SegmentId,
                    value.NoteIds,
                    resolved)).ToArray();
            });
    }

    public static ITimelineSelectionResultEditCommand HumanizeDirectMidiNotes(
        IReadOnlyCollection<DirectMidiNoteOwnerSelection> owners,
        TimelineHumanizeOptions options)
    {
        DirectMidiNoteOwnerSelection[] frozen = FreezeDirectOwners(owners, static value => value.NoteIds);
        ArgumentNullException.ThrowIfNull(options);
        return MultiOwnerResultCommand(
            "Humanize Direct MIDI notes",
            (project, _) =>
            {
                TimelineHumanizeOptions resolved = options with { Seed = ResolveHumanizeSeed(options) };
                return frozen.Select(value => HumanizeDirectMidiNotes(
                    value.SegmentId,
                    value.NoteIds,
                    resolved)).ToArray();
            });
    }

    public static ITimelineSelectionResultEditCommand HumanizeTemplateNotes(
        IReadOnlyCollection<TemplateNoteOwnerSelection> owners,
        TimelineHumanizeOptions options)
    {
        TemplateNoteOwnerSelection[] frozen = FreezeTemplateOwners(owners, static value => value.NoteIds);
        ArgumentNullException.ThrowIfNull(options);
        return MultiOwnerResultCommand(
            "Humanize SubVoice notes",
            (project, _) =>
            {
                TimelineHumanizeOptions resolved = options with { Seed = ResolveHumanizeSeed(options) };
                return frozen.Select(value => HumanizeTemplateNotes(
                    value.EventInstrumentId,
                    value.SubVoiceId,
                    value.NoteIds,
                    resolved)).ToArray();
            });
    }

    public static ITimelineSelectionResultEditCommand JoinLogicalNotes(
        IReadOnlyCollection<LogicalNoteOwnerSelection> owners,
        NoteJoinOptions options) => MultiOwnerResultCommand(
        "Join logical notes",
        FreezeLogicalOwners(owners, static value => value.NoteIds)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() => JoinLogicalNotes(
                value.SegmentId,
                value.NoteIds,
                options)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand JoinDirectMidiNotes(
        IReadOnlyCollection<DirectMidiNoteOwnerSelection> owners,
        NoteJoinOptions options) => MultiOwnerResultCommand(
        "Join Direct MIDI notes",
        FreezeDirectOwners(owners, static value => value.NoteIds)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() => JoinDirectMidiNotes(
                value.SegmentId,
                value.NoteIds,
                options)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand JoinTemplateNotes(
        IReadOnlyCollection<TemplateNoteOwnerSelection> owners,
        NoteJoinOptions options) => MultiOwnerResultCommand(
        "Join SubVoice notes",
        FreezeTemplateOwners(owners, static value => value.NoteIds)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() => JoinTemplateNotes(
                value.EventInstrumentId,
                value.SubVoiceId,
                value.NoteIds,
                options)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand QuantizeLogicalNotes(
        IReadOnlyCollection<LogicalNoteOwnerSelection> owners,
        NoteQuantizeOptions options) => MultiOwnerResultCommand(
        "Quantize logical notes",
        FreezeLogicalOwners(owners, static value => value.NoteIds)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() => QuantizeLogicalNotes(
                value.SegmentId,
                value.NoteIds,
                options)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand QuantizeDirectMidiNotes(
        IReadOnlyCollection<DirectMidiNoteOwnerSelection> owners,
        NoteQuantizeOptions options) => MultiOwnerResultCommand(
        "Quantize Direct MIDI notes",
        FreezeDirectOwners(owners, static value => value.NoteIds)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() => QuantizeDirectMidiNotes(
                value.SegmentId,
                value.NoteIds,
                options)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand QuantizeTemplateNotes(
        IReadOnlyCollection<TemplateNoteOwnerSelection> owners,
        NoteQuantizeOptions options) => MultiOwnerResultCommand(
        "Quantize SubVoice notes",
        FreezeTemplateOwners(owners, static value => value.NoteIds)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() => QuantizeTemplateNotes(
                value.EventInstrumentId,
                value.SubVoiceId,
                value.NoteIds,
                options)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand QuantizeLogicalParameterPoints(
        IReadOnlyCollection<LogicalParameterPointOwnerSelection> owners,
        TimelineQuantizeGrid grid) => MultiOwnerResultCommand(
        "Quantize logical parameter points",
        FreezeLogicalParameterOwners(owners)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() =>
                QuantizeLogicalParameterPoints(value.SegmentId, value.PointIds, grid)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand QuantizeDirectMidiEvents(
        IReadOnlyCollection<DirectMidiEventOwnerSelection> owners,
        TimelineQuantizeGrid grid) => MultiOwnerResultCommand(
        "Quantize Direct MIDI events",
        FreezeDirectEventOwners(owners)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() =>
                QuantizeDirectMidiEvents(value.SegmentId, value.EventIds, grid)))
            .ToArray());

    public static ITimelineSelectionResultEditCommand QuantizeTemplateEvents(
        IReadOnlyCollection<TemplateEventOwnerSelection> owners,
        TimelineQuantizeGrid grid) => MultiOwnerResultCommand(
        "Quantize SubVoice events",
        FreezeTemplateEventOwners(owners)
            .Select(value => (Func<ITimelineSelectionResultEditCommand>)(() =>
                QuantizeTemplateEvents(
                    value.EventInstrumentId,
                    value.SubVoiceId,
                    value.EventIds,
                    grid)))
            .ToArray());

    private static ITimelineSelectionResultEditCommand MultiOwnerResultCommand(
        string name,
        IReadOnlyList<Func<ITimelineSelectionResultEditCommand>> factories) =>
        MultiOwnerResultCommand(
            name,
            (_, _) => factories.Select(static factory => factory()).ToArray());

    private static ITimelineSelectionResultEditCommand MultiOwnerResultCommand(
        string name,
        Func<MidoraProject, CancellationToken,
            IReadOnlyList<ITimelineSelectionResultEditCommand>> createCommands) =>
        ResultCommand(name, (project, publishResult, cancellationToken, progress) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ITimelineSelectionResultEditCommand> commands =
                createCommands(project, cancellationToken);
            return PrepareMultiOwnerCommands(
                project,
                commands,
                publishResult,
                cancellationToken,
                progress);
        });

    private static ITimelineSelectionResultEditCommand MultiOwnerSplitResultCommand(
        string name,
        Func<DetachedStableIdAllocator,
            IReadOnlyList<ITimelineSelectionResultEditCommand>> createCommands) =>
        ResultCommand(name, (project, publishResult, cancellationToken, progress) =>
        {
            DetachedStableIdAllocator allocator = new(project);
            IReadOnlyList<ITimelineSelectionResultEditCommand> commands =
                createCommands(allocator);
            IPreparedProjectEdit prepared = PrepareMultiOwnerCommands(
                project,
                commands,
                publishResult,
                cancellationToken,
                progress,
                allocator);
            return prepared;
        });

    private static IPreparedProjectEdit PrepareMultiOwnerCommands(
        MidoraProject project,
        IReadOnlyList<ITimelineSelectionResultEditCommand> commands,
        SelectionPublisher publishResult,
        CancellationToken cancellationToken,
        IProgress<TimelineEditPreparationProgress>? progress,
        DetachedStableIdAllocator? allocator = null)
    {
        if (commands.Count == 0)
            throw new InvalidOperationException(
                "A Timeline owner operation requires at least one owner.");

        List<IPreparedProjectEdit> prepared = new(commands.Count);
        using var scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
        try
        {
            ReportPreparationProgress(
                progress,
                TimelineEditPreparationPhase.ResolvingSelection,
                0,
                commands.Count);
            for (int index = 0; index < commands.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ITimelineSelectionResultEditCommand command = commands[index];
                IProgress<TimelineEditPreparationProgress> childProgress = new BulkEditProgressRange(
                    progress, (double)index / commands.Count, 1d / commands.Count);
                using var childScope = BulkEditPreparationContext.Enter(cancellationToken, childProgress, project: project);
                IPreparedProjectEdit edit = command is IProgressReportingProjectEditCommand reporting
                    ? reporting.Prepare(project, cancellationToken, childProgress)
                    : command is ICancellableProjectEditCommand cancellable
                        ? cancellable.Prepare(project, cancellationToken)
                        : command.Prepare(project);
                prepared.Add(edit);
                ReportPreparationProgress(
                    progress,
                    TimelineEditPreparationPhase.ResolvingSelection,
                    index + 1,
                    commands.Count);
            }

            PreparedTimelineSelection[] selections = prepared
                .Select(static edit => edit as IPreparedTimelineSelectionEdit
                    ?? throw new InvalidOperationException(
                        "A Timeline selection edit did not expose its frozen selection result."))
                .Select(static edit => edit.PreparedSelection)
                .ToArray();
            PreparedTimelineSelection preparedSelection = new(
                CompactMidoraIdList.FreezeConcatenated(
                    selections.Select(static value => value.OriginalSelectionIds)),
                CompactMidoraIdList.FreezeConcatenated(
                    selections.Select(static value => value.ResultSelectionIds)));
            _ = publishResult(preparedSelection.OriginalSelectionIds);
            ProjectChangeSet changes = MergeTimelineChanges(
                prepared.Select(static value => value.Changes));
            IPreparedProjectEdit result = new PreparedMultiOwnerTimelineEdit(
                project,
                prepared.ToArray(),
                changes,
                publishResult,
                preparedSelection,
                allocator?.ExpectedNextStableId,
                allocator?.ReplacementNextStableId);
            prepared.Clear();
            ReportPreparationProgress(progress, TimelineEditPreparationPhase.Ready, 1, 1);
            return ExactTimelineCollisionPolicy.CombineScopes(
                result,
                ((PreparedMultiOwnerTimelineEdit)result).Edits);
        }
        finally
        {
            foreach (IPreparedProjectEdit edit in prepared)
                if (edit is IDisposable disposable) disposable.Dispose();
        }
    }

    private static T[] FreezeOwners<T, TKey>(
        IReadOnlyCollection<T> owners,
        Func<T, TKey> ownerKey,
        Func<T, IReadOnlyCollection<MidoraId>> ids,
        Func<T, T> freeze)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (owners.Count == 0)
            throw new ArgumentException(
                "A Timeline owner operation requires at least one owner.",
                nameof(owners));
        HashSet<TKey> ownerKeys = [];
        // Preserve eager validation for bounded small caller-owned lists.
        // Large selection resolution belongs to cancellable Prepare, not the
        // UI command constructor; globally unique IDs then cannot validly
        // resolve in two different owners, and each owner checks duplicates.
        bool eagerIds = owners.Sum(owner => (long)(owner is null ? 0 : ids(owner)?.Count ?? 0)) <= 4096;
        HashSet<MidoraId>? selectedIds = eagerIds ? [] : null;
        T[] result = new T[owners.Count];
        int index = 0;
        foreach (T owner in owners)
        {
            if (owner is null) throw new ArgumentException("An owner selection cannot be null.", nameof(owners));
            if (!ownerKeys.Add(ownerKey(owner)))
                throw new ArgumentException("A Timeline owner is listed more than once.", nameof(owners));
            IReadOnlyCollection<MidoraId> selected = ids(owner);
            ArgumentNullException.ThrowIfNull(selected);
            if (selected.Count == 0)
                throw new ArgumentException("Every Timeline owner selection must be non-empty.", nameof(owners));
            if (selectedIds is not null)
                foreach (MidoraId id in selected)
                    if (id == default || !selectedIds.Add(id))
                        throw new ArgumentException("Timeline owner selections must contain distinct valid object IDs.", nameof(owners));
            // ID membership and duplicates are checked while preparing each
            // owner, by the bounded formal-ordinal selection resolver. Do not
            // build a second, selection-sized HashSet on the UI command path.
            result[index++] = freeze(owner);
        }
        return result;
    }

    private static LogicalNoteOwnerSelection[] FreezeLogicalOwners(
        IReadOnlyCollection<LogicalNoteOwnerSelection> owners,
        Func<LogicalNoteOwnerSelection, IReadOnlyCollection<MidoraId>> ids) =>
        FreezeOwners(
            owners,
            static value => value.SegmentId,
            ids,
            static value => value with { NoteIds = FreezeOwnerIds(value.NoteIds) })
        .OrderBy(static value => value.SegmentId)
        .ToArray();

    private static DirectMidiNoteOwnerSelection[] FreezeDirectOwners(
        IReadOnlyCollection<DirectMidiNoteOwnerSelection> owners,
        Func<DirectMidiNoteOwnerSelection, IReadOnlyCollection<MidoraId>> ids) =>
        FreezeOwners(
            owners,
            static value => value.SegmentId,
            ids,
            static value => value with { NoteIds = FreezeOwnerIds(value.NoteIds) })
        .OrderBy(static value => value.SegmentId)
        .ToArray();

    private static TemplateNoteOwnerSelection[] FreezeTemplateOwners(
        IReadOnlyCollection<TemplateNoteOwnerSelection> owners,
        Func<TemplateNoteOwnerSelection, IReadOnlyCollection<MidoraId>> ids) =>
        FreezeOwners(
            owners,
            static value => (value.EventInstrumentId, value.SubVoiceId),
            ids,
            static value => value with { NoteIds = FreezeOwnerIds(value.NoteIds) })
        .OrderBy(static value => value.EventInstrumentId)
        .ThenBy(static value => value.SubVoiceId)
        .ToArray();

    private static LogicalParameterPointOwnerSelection[] FreezeLogicalParameterOwners(
        IReadOnlyCollection<LogicalParameterPointOwnerSelection> owners) =>
        FreezeOwners(
            owners,
            static value => value.SegmentId,
            static value => value.PointIds,
            static value => value with { PointIds = FreezeOwnerIds(value.PointIds) })
        .OrderBy(static value => value.SegmentId)
        .ToArray();

    private static DirectMidiEventOwnerSelection[] FreezeDirectEventOwners(
        IReadOnlyCollection<DirectMidiEventOwnerSelection> owners) =>
        FreezeOwners(
            owners,
            static value => value.SegmentId,
            static value => value.EventIds,
            static value => value with { EventIds = FreezeOwnerIds(value.EventIds) })
        .OrderBy(static value => value.SegmentId)
        .ToArray();

    private static TemplateEventOwnerSelection[] FreezeTemplateEventOwners(
        IReadOnlyCollection<TemplateEventOwnerSelection> owners) =>
        FreezeOwners(
            owners,
            static value => (value.EventInstrumentId, value.SubVoiceId),
            static value => value.EventIds,
            static value => value with { EventIds = FreezeOwnerIds(value.EventIds) })
        .OrderBy(static value => value.EventInstrumentId)
        .ThenBy(static value => value.SubVoiceId)
        .ToArray();

    private static IReadOnlyCollection<MidoraId> FreezeOwnerIds(IReadOnlyCollection<MidoraId> ids)
    {
        if (ids is CompactMidoraIdList or BoundedImmutableValueSource<MidoraId>)
            return CompactMidoraIdList.Freeze((IReadOnlyList<MidoraId>)ids);
        if (ids.Count <= 4096)
            return CompactMidoraIdList.Freeze(ids as IReadOnlyList<MidoraId> ?? ids.ToArray());
        // Formal UI callers already supply immutable selection roots. Unknown
        // large programmatic sources are read only during cancellable Prepare;
        // do not enumerate and materialize them on the UI constructor path.
        return ids;
    }

    private static ProjectChangeSet MergeTimelineChanges(IEnumerable<ProjectChangeSet> values)
    {
        ProjectChangeSet[] source = values.ToArray();
        ProjectChangeSet result = new()
        {
            AffectsEverything = source.Any(static value => value.AffectsEverything),
            AffectsConductor = source.Any(static value => value.AffectsConductor),
            AffectsAudioPcmCacheGeneration = source.Any(
                static value => value.AffectsAudioPcmCacheGeneration)
        };
        foreach (ProjectChangeSet value in source)
        {
            result.TrackIds.UnionWith(value.TrackIds);
            result.EventInstrumentIds.UnionWith(value.EventInstrumentIds);
            result.EventInstrumentUsageIds.UnionWith(value.EventInstrumentUsageIds);
            result.MidiChannelRootIds.UnionWith(value.MidiChannelRootIds);
            result.PureMidiTrackIds.UnionWith(value.PureMidiTrackIds);
            result.PresentationTrackIds.UnionWith(value.PresentationTrackIds);
            result.PresentationEventInstrumentIds.UnionWith(
                value.PresentationEventInstrumentIds);
            result.TimelineOwnerChanges.AddRange(value.TimelineOwnerChanges);
        }
        return result;
    }

    private sealed class PreparedMultiOwnerTimelineEdit
        : IPreparedTimelineSelectionEdit, IPreparedProjectEditPublicationGate, IDisposable
    {
        private readonly MidoraProject _project;
        private readonly SelectionPublisher _publishResult;
        private readonly long? _expectedNextStableId;
        private readonly long? _replacementNextStableId;
        private bool _isApplied;
        private bool _allocatorPublished;
        private bool _rollbackAfterRejectedApply;

        public PreparedMultiOwnerTimelineEdit(
            MidoraProject project,
            IPreparedProjectEdit[] edits,
            ProjectChangeSet changes,
            SelectionPublisher publishResult,
            PreparedTimelineSelection preparedSelection,
            long? expectedNextStableId,
            long? replacementNextStableId)
        {
            _project = project;
            Edits = edits;
            Changes = changes;
            _publishResult = publishResult;
            PreparedSelection = preparedSelection;
            _expectedNextStableId = expectedNextStableId;
            _replacementNextStableId = replacementNextStableId;
        }

        internal IReadOnlyList<IPreparedProjectEdit> Edits { get; }
        public bool HasChanges => Edits.Any(static value => value.HasChanges);
        public ProjectChangeSet Changes { get; }
        public PreparedTimelineSelection PreparedSelection { get; }

        public void ValidateForPublication(MidoraProject project)
        {
            ValidateProject(project);
            foreach (IPreparedProjectEdit edit in Edits)
            {
                if (edit is IPreparedProjectEditPublicationGate gate)
                    gate.ValidateForPublication(project);
            }
            if (!_allocatorPublished
                && _expectedNextStableId is long expectedNextStableId
                && project.NextStableId != expectedNextStableId)
            {
                throw new InvalidOperationException(
                    "The Project Stable ID allocator changed before atomic publication.");
            }
        }

        public void Apply(MidoraProject project)
        {
            ValidateProject(project);
            if (_isApplied)
                throw new InvalidOperationException("The multi-owner Timeline edit is already applied.");
            _rollbackAfterRejectedApply = false;
            int applied = 0;
            int attempted = -1;
            try
            {
                // Validate every touched owner before the first child can swap
                // a root or publish a selection. This includes planned no-op
                // children, whose source revision still belongs to the atomic
                // multi-owner transaction.
                ValidateForPublication(project);
                for (; applied < Edits.Count; applied++)
                {
                    attempted = applied;
                    Edits[applied].Apply(project);
                }
            }
            catch (Exception editError)
            {
                try
                {
                    // The child whose Apply rejected the publication also owns
                    // the exact rejected-Apply rollback contract.  Including it
                    // here handles both a pre-swap revision rejection and any
                    // future child that can fail after beginning publication.
                    int rollbackFrom = attempted;
                    for (int index = rollbackFrom; index >= 0; index--)
                        Edits[index].Undo(project);
                    _rollbackAfterRejectedApply = true;
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException(
                        "The multi-owner Timeline edit failed and rollback could not restore every owner.",
                        editError,
                        rollbackError);
                }
                throw;
            }
            if (!_allocatorPublished
                && _replacementNextStableId is long replacementNextStableId)
            {
                project.AdvanceNextStableId(replacementNextStableId);
                _allocatorPublished = true;
            }
            _isApplied = true;
            _ = _publishResult(PreparedSelection.ResultSelectionIds);
        }

        public void Undo(MidoraProject project)
        {
            ValidateProject(project);
            if (!_isApplied)
            {
                // ProjectCompilationSession invokes the rollback delegate after
                // a rejected Apply.  Apply already restored every earlier owner,
                // so that second rollback is a verified no-op.
                if (_rollbackAfterRejectedApply)
                {
                    _rollbackAfterRejectedApply = false;
                    return;
                }
                throw new InvalidOperationException("The multi-owner Timeline edit is not applied.");
            }
            for (int index = Edits.Count - 1; index >= 0; index--) Edits[index].Undo(project);
            _isApplied = false;
            _ = _publishResult(PreparedSelection.OriginalSelectionIds);
        }

        public void Dispose()
        {
            foreach (IPreparedProjectEdit edit in Edits)
                if (edit is IDisposable disposable) disposable.Dispose();
        }

        private void ValidateProject(MidoraProject project)
        {
            if (!ReferenceEquals(project, _project))
                throw new InvalidOperationException(
                    "The multi-owner Timeline edit belongs to another Project.");
        }
    }

    private sealed class DetachedStableIdAllocator
    {
        private long _next;

        public DetachedStableIdAllocator(MidoraProject project)
        {
            ArgumentNullException.ThrowIfNull(project);
            ExpectedNextStableId = project.NextStableId;
            _next = ExpectedNextStableId;
        }

        public long ExpectedNextStableId { get; }
        public long ReplacementNextStableId => _next;

        public MidoraId ReserveNext()
        {
            long current = _next;
            _next = checked(_next + 1);
            return MidoraId.FromSequence(current);
        }

        public DetachedStableIdReservation Reserve(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0)
                return new(null, null, Array.Empty<MidoraId>());
            long end;
            try
            {
                end = checked(_next + count);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException(
                    "The Project stable ID counter is exhausted.",
                    exception);
            }

            MidoraId[] ids = new MidoraId[count];
            for (int index = 0; index < ids.Length; index++)
                ids[index] = MidoraId.FromSequence(checked(_next + index));
            _next = end;
            // The aggregate prepared edit owns the allocator publication.
            return new(null, null, ids);
        }
    }
}
