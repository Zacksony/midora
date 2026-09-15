using Midora.Domain;

namespace Midora.Application;

/// <summary>
/// Prepares a bounded sequence of edits against the result of the preceding edit,
/// then exposes the whole sequence as one Project-history transaction.
/// </summary>
/// <remarks>
/// Factories operate only on the private mirror. Stable IDs allocated there are
/// reserved by the final publication gate, never by mutating the live Project.
/// </remarks>
public sealed class SequentialProjectEditCommand : IProgressReportingProjectEditCommand
{
    private readonly IReadOnlyList<Func<MidoraProject, IProjectEditCommand>> _factories;

    public SequentialProjectEditCommand(
        string name,
        IEnumerable<Func<MidoraProject, IProjectEditCommand>> factories)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Project edit command name is required.", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(factories);
        Name = name.Trim();
        _factories = factories.ToArray();
        if (_factories.Count == 0 || _factories.Any(static value => value is null))
        {
            throw new ArgumentException(
                "At least one valid Project edit factory is required.",
                nameof(factories));
        }
    }

    public string Name { get; }

    public IPreparedProjectEdit Prepare(MidoraProject project) =>
        Prepare(project, BulkEditPreparationContext.Current?.Token ?? default, null);

    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken cancellationToken) =>
        Prepare(project, cancellationToken, null);

    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken cancellationToken,
        IProgress<TimelineEditPreparationProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(project);
        using BulkEditPreparationContext scope = BulkEditPreparationContext.Enter(cancellationToken, progress, project: project);
        IDisposable? metadataBudget = BoundedProjectDirectoryBudget.Reserve(project, scope);
        MidoraProject? draft = null;
        List<IPreparedProjectEdit> prepared = [];
        try
        {
            draft = ProjectCompilationSnapshot.Create(project, cancellationToken);
            int completed = 0;
            foreach (Func<MidoraProject, IProjectEditCommand> factory in _factories)
            {
                scope.Checkpoint(completed, _factories.Count);
                IProgress<TimelineEditPreparationProgress>? childProgress = progress is null ? null
                    : new BulkEditProgressRange(progress, (double)completed / _factories.Count, 1d / _factories.Count);
                using var childScope = BulkEditPreparationContext.Enter(cancellationToken, childProgress, project: project);
                IProjectEditCommand command = factory(draft)
                    ?? throw new InvalidOperationException(
                        "A sequential Project edit factory returned no command.");
                IPreparedProjectEdit edit = command switch
                {
                    IProgressReportingProjectEditCommand reporting => reporting.Prepare(draft, cancellationToken, childProgress),
                    ICancellableProjectEditCommand cancellable => cancellable.Prepare(draft, cancellationToken),
                    _ => command.Prepare(draft)
                }
                    ?? throw new InvalidOperationException(
                        "A sequential Project edit command returned no prepared edit.");
                prepared.Add(edit);
                edit.Apply(draft);
                completed++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ProjectChangeSet changes = MergeChanges(prepared.Select(value => value.Changes));
            // Legacy scoped property edits defer collision decisions until all
            // fields are set. Otherwise changing Tick before Key could discard
            // a note which is not colliding at its final Tick/Key at all.
            var sequence = new Prepared(prepared.ToArray(), changes);
            IPreparedProjectEdit scoped = ExactTimelineCollisionPolicy.CombineScopes(sequence, prepared);
            if (!ReferenceEquals(sequence, scoped))
            {
                UndoPrepared(draft, prepared);
                cancellationToken.ThrowIfCancellationRequested();
                IPreparedProjectEdit resolved = ExactTimelineCollisionPolicy.Wrap(draft, scoped);
                resolved.Apply(draft);
                prepared.Clear();
                prepared.Add(resolved);
            }
            InstrumentChangeMaintenance.Reconcile(draft,
                InstrumentChangeMaintenance.Capture(project, changes), cancellationToken);
            IPreparedProjectEdit result = DetachedProjectRootPreparedEdit.Create(project, draft,
                prepared.Any(static edit => edit.HasChanges), changes, prepared, metadataBudget,
                normalizeSelection: _factories.Count > 1);
            metadataBudget = null;
            return result;
        }
        catch
        {
            DisposePrepared(prepared);
            draft?.Dispose();
            throw;
        }
        finally { metadataBudget?.Dispose(); }
    }

    private static void UndoPrepared(
        MidoraProject project,
        IReadOnlyList<IPreparedProjectEdit> prepared)
    {
        for (int index = prepared.Count - 1; index >= 0; index--)
        {
            prepared[index].Undo(project);
        }
    }

    private static void DisposePrepared(IEnumerable<IPreparedProjectEdit> prepared)
    {
        foreach (IPreparedProjectEdit edit in prepared)
            if (edit is IDisposable disposable) disposable.Dispose();
    }

    private static ProjectChangeSet MergeChanges(IEnumerable<ProjectChangeSet> values)
    {
        ProjectChangeSet[] source = values.ToArray();
        ProjectChangeSet result = new()
        {
            AffectsEverything = source.Any(value => value.AffectsEverything),
            AffectsConductor = source.Any(value => value.AffectsConductor),
            AffectsAudioPcmCacheGeneration =
                source.Any(value => value.AffectsAudioPcmCacheGeneration)
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

    private sealed class Prepared(
        IReadOnlyList<IPreparedProjectEdit> edits,
        ProjectChangeSet changes) : IPreparedProjectEdit, IPreparedTimelineSelectionEdit, IDisposable
    {
        public bool HasChanges => edits.Any(value => value.HasChanges);
        public ProjectChangeSet Changes { get; } = changes;
        public bool HasPreparedSelection => edits.Any(static e => e is IPreparedTimelineSelectionEdit { HasPreparedSelection: true });
        public PreparedTimelineSelection PreparedSelection
        {
            get
            {
                var selections = edits.OfType<IPreparedTimelineSelectionEdit>()
                    .Where(static e => e.HasPreparedSelection).Select(static e => e.PreparedSelection).ToArray();
                return new(CompactMidoraIdList.FreezeConcatenated(selections.Select(static s => s.OriginalSelectionIds)),
                    CompactMidoraIdList.FreezeConcatenated(selections.Select(static s => s.ResultSelectionIds)));
            }
        }

        public void Apply(MidoraProject project)
        {
            int applied = 0;
            try
            {
                for (; applied < edits.Count; applied++) edits[applied].Apply(project);
            }
            catch
            {
                for (int index = applied - 1; index >= 0; index--) edits[index].Undo(project);
                throw;
            }
        }

        public void Undo(MidoraProject project)
        {
            for (int index = edits.Count - 1; index >= 0; index--) edits[index].Undo(project);
        }

        public void Dispose() => DisposePrepared(edits);
    }
}
