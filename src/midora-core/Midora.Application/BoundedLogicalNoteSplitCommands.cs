using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static IPreparedProjectEdit PrepareBoundedLogicalSplit(MidoraProject project, SegmentLocation location,
        IReadOnlyCollection<MidoraId> ids, NoteSplitOptions options, SelectionPublisher publisher,
        CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress, DetachedStableIdAllocator? allocator)
    {
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var source = location.Segment.Notes.CreateQuerySnapshot();
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, static _ => true, scope.Resources, token);
        long left = selected.Min(v => v.Value.StartTick);
        long right = selected.Max(v => checked(v.Value.StartTick + v.Value.LengthTicks));
        using var knives = CreateBoundedSplitKnives(left, checked(right - left), options, scope);
        long firstId = project.NextStableId, nextId = firstId;
        var plan = BoundedLogicalNoteSplitPlanning.Split(source, selected, knives, options.MaximumResultObjects,
            static v => new(v.StartTick, v.Note), static v => checked(v.StartTick + v.LengthTicks),
            static (v, tick, length) => v with { StartTick = tick, LengthTicks = length },
            static v => v.Id, static (v, id) => v with { Id = id },
            key => source.EnumerateExactStart(key.Tick, key.Key), Allocate, scope.Resources, token);
        try
        {
            var result = ProjectTimelineOwnerRootClone.CloneLogicalSegment(project, location.Segment, token);
            result.Notes.Clear(); result.Notes.AdoptSplicedSnapshot(project, source, plan.Splices, plan.Values, token);
            var receipt = TrackChange(location.Track.Id);
            ProjectTimelineOwnerChangeSetBuilder.AddLogicalNotes(receipt, location.Segment, result,
                plan.Splices.Select(v => source.GetByOrdinal(v.Ordinal).Id).Concat(plan.Values.Select(v => v.Id)));
            IPreparedProjectEdit edit = !plan.HasChanges
                ? ProjectTimelineOwnerRootReplacement.PrepareLogicalSegmentRevisionGate(project, location.Track, location.Segment, TrackChange(location.Track.Id), stamp)
                : ProjectTimelineOwnerRootReplacement.PrepareLogicalSegment(project, location.Track, location.Segment, result,
                    receipt, allocator is null ? firstId : null, allocator is null ? nextId : null, stamp);
            return PublishBoundedNoteSelection(edit, selected.Select(v => v.Value.Id), plan.Values.Select(v => v.Id), publisher, scope);
        }
        catch { plan.Dispose(); throw; }
        MidoraId Allocate()
        {
            if (allocator is not null) return allocator.ReserveNext();
            MidoraId result = MidoraId.FromSequence(nextId); nextId = checked(nextId + 1); return result;
        }
    }

    private static IPreparedProjectEdit PrepareBoundedTemplateSplit(MidoraProject project, EventInstrument instrument, SubVoice voice,
        IReadOnlyCollection<MidoraId> ids, NoteSplitOptions options, SelectionPublisher publisher,
        CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress, DetachedStableIdAllocator? allocator)
    {
        using var scope = BulkEditPreparationContext.Enter(token, progress, project: project);
        var source = voice.Events.CreateQuerySnapshot();
        var stamp = ProjectTimelineOwnerSourceStamp.Capture(instrument, voice);
        using var selected = BoundedLogicalNotePlanning.Select(source, ids, static v => v.Kind == TemplateEventKind.Note, scope.Resources, token);
        long left = selected.Min(v => v.Value.Tick);
        long right = selected.Max(v => checked(v.Value.Tick + v.Value.LengthTicks));
        using var knives = CreateBoundedSplitKnives(left, checked(right - left), options, scope);
        long firstId = project.NextStableId, nextId = firstId;
        var plan = BoundedLogicalNoteSplitPlanning.Split(source, selected, knives, options.MaximumResultObjects,
            static v => new(v.Tick, v.Number), static v => checked(v.Tick + v.LengthTicks),
            static (v, tick, length) => v with { Tick = tick, LengthTicks = length },
            static v => v.Id, static (v, id) => v with { Id = id },
            key => source.EnumerateNoteExactStart(key.Tick, key.Key), Allocate, scope.Resources, token);
        try
        {
            var result = ProjectTimelineOwnerRootClone.CloneSubVoice(project, voice, token);
            result.Events.Clear(); result.Events.AdoptSplicedSnapshot(project, source, plan.Splices, plan.Values, token);
            var receipt = EventInstrumentChange(instrument.Id);
            ProjectTimelineOwnerChangeSetBuilder.AddSubVoiceEvents(receipt, voice, result,
                plan.Splices.Select(v => source.GetByOrdinal(v.Ordinal).Id).Concat(plan.Values.Select(v => v.Id)));
            IPreparedProjectEdit edit = !plan.HasChanges
                ? ProjectTimelineOwnerRootReplacement.PrepareSubVoiceRevisionGate(project, instrument, voice, EventInstrumentChange(instrument.Id), stamp)
                : ProjectTimelineOwnerRootReplacement.PrepareSubVoice(project, instrument, voice, result,
                    receipt, allocator is null ? firstId : null, allocator is null ? nextId : null, stamp);
            return PublishBoundedNoteSelection(edit, selected.Select(v => v.Value.Id), plan.Values.Select(v => v.Id), publisher, scope);
        }
        catch { plan.Dispose(); throw; }
        MidoraId Allocate()
        {
            if (allocator is not null) return allocator.ReserveNext();
            MidoraId result = MidoraId.FromSequence(nextId); nextId = checked(nextId + 1); return result;
        }
    }

    private static BoundedImmutableValueSource<long> CreateBoundedSplitKnives(long left, long span,
        NoteSplitOptions options, BulkEditPreparationContext scope)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentOutOfRangeException(nameof(options.Mode));
        if (options.MaximumResultObjects is < 1 or > NoteSplitOptions.MaximumSupportedResultObjects)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumResultObjects));
        if (options.Mode == NoteSplitMode.FixedPieceLength && options.FixedPieceLengthTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(options.FixedPieceLengthTicks));
        if (options.Mode == NoteSplitMode.MaximumPieceCount && options.MaximumPieceCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumPieceCount));
        if (options.Mode == NoteSplitMode.Expression)
        {
            if (options.MaximumCuts is < 1 or > NoteSplitOptions.MaximumSupportedCuts)
                throw new ArgumentOutOfRangeException(nameof(options.MaximumCuts));
            if (options.ExpressionProgram is null) throw new ArgumentException("Expression Note Split requires a compiled expression program.", nameof(options));
        }
        var store = new BoundedEditRecordStore<long>(scope.Resources);
        try
        {
            if (span > 1 && options.Mode == NoteSplitMode.FixedPieceLength)
            {
                long count = (span - 1) / options.FixedPieceLengthTicks;
                ValidateAutomaticSplitCutCount(count, options.Mode);
                for (int i = 1; i <= count; i++) store.Add(checked(left + (long)((Int128)i * options.FixedPieceLengthTicks)), scope.Token);
            }
            else if (span > 1 && options.Mode == NoteSplitMode.MaximumPieceCount)
            {
                long count = Math.Min((long)options.MaximumPieceCount, span);
                ValidateAutomaticSplitCutCount(count - 1, options.Mode);
                for (int i = 1; i < count; i++) store.Add(checked(left + (long)((Int128)i * span / count)), scope.Token);
            }
            else if (span > 1)
            {
                long relative = 0;
                for (int i = 0; i < options.MaximumCuts; i++)
                {
                    scope.Token.ThrowIfCancellationRequested();
                    double evaluated = options.ExpressionProgram!.Evaluate(i, relative);
                    if (!double.IsFinite(evaluated)) throw new InvalidOperationException("The Note Split expression returned a non-finite length.");
                    double rounded = Math.Round(evaluated, MidpointRounding.AwayFromZero);
                    long length = rounded >= long.MaxValue ? long.MaxValue : rounded <= 1 ? 1 : checked((long)rounded);
                    if (relative > long.MaxValue - length) break;
                    relative += length;
                    if (relative >= span) break;
                    store.Add(checked(left + relative), scope.Token);
                }
            }
            store.Seal();
            return new(store);
        }
        catch { store.Dispose(); throw; }
    }
}
