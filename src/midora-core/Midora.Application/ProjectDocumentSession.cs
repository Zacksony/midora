using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application;

public enum ProjectDocumentOrigin
{
    Unsaved,
    Persisted
}

public sealed record ProjectHistoryEntryInfo(
    string Name,
    long BeforeStateId,
    long AfterStateId);

public sealed record ProjectEditExecution(
    bool Changed,
    CanonicalCompiledResult CompilationResult);

public sealed class PreparedProjectHistoryTransition
{
    internal PreparedProjectHistoryTransition(ProjectDocumentSession document, long revision,
        bool redo, string name, long targetStateId)
    { Document = document; Revision = revision; IsRedo = redo; Name = name; TargetStateId = targetStateId; }
    internal ProjectDocumentSession Document { get; }
    internal long Revision { get; }
    public bool IsRedo { get; }
    public string Name { get; }
    public long TargetStateId { get; }
}

public sealed class ProjectContentChangedEventArgs : EventArgs
{
    public ProjectContentChangedEventArgs(ProjectChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        AffectsEverything = changes.AffectsEverything;
        AffectsConductor = changes.AffectsConductor;
        AffectsAudioPcmCacheGeneration = changes.AffectsAudioPcmCacheGeneration;
        TrackIds = Array.AsReadOnly(changes.TrackIds.Order().ToArray());
        EventInstrumentIds = Array.AsReadOnly(changes.EventInstrumentIds.Order().ToArray());
        EventInstrumentUsageIds = Array.AsReadOnly(changes.EventInstrumentUsageIds.Order().ToArray());
        MidiChannelRootIds = Array.AsReadOnly(changes.MidiChannelRootIds.Order().ToArray());
        PureMidiTrackIds = Array.AsReadOnly(changes.PureMidiTrackIds.Order().ToArray());
        PresentationTrackIds = Array.AsReadOnly(changes.PresentationTrackIds.Order().ToArray());
        PresentationEventInstrumentIds = Array.AsReadOnly(
            changes.PresentationEventInstrumentIds.Order().ToArray());
        TimelineOwnerChanges = Array.AsReadOnly(changes.TimelineOwnerChanges
            .OrderBy(static value => value.OwnerId)
            .ThenBy(static value => value.OwnerKind)
            .ToArray());
    }

    public bool AffectsEverything { get; }
    public bool AffectsConductor { get; }
    public bool AffectsAudioPcmCacheGeneration { get; }
    public IReadOnlyList<MidoraId> TrackIds { get; }
    public IReadOnlyList<MidoraId> EventInstrumentIds { get; }
    public IReadOnlyList<MidoraId> EventInstrumentUsageIds { get; }
    public IReadOnlyList<MidoraId> MidiChannelRootIds { get; }
    public IReadOnlyList<MidoraId> PureMidiTrackIds { get; }
    public IReadOnlyList<MidoraId> PresentationTrackIds { get; }
    public IReadOnlyList<MidoraId> PresentationEventInstrumentIds { get; }
    public IReadOnlyList<ProjectTimelineOwnerChangeSet> TimelineOwnerChanges { get; }
    public bool IsEmpty => !AffectsEverything
        && !AffectsConductor
        && !AffectsAudioPcmCacheGeneration
        && TrackIds.Count == 0
        && EventInstrumentIds.Count == 0
        && EventInstrumentUsageIds.Count == 0
        && MidiChannelRootIds.Count == 0
        && PureMidiTrackIds.Count == 0
        && PresentationTrackIds.Count == 0
        && PresentationEventInstrumentIds.Count == 0
        && TimelineOwnerChanges.Count == 0;
}

public interface IProjectEditCommand
{
    string Name { get; }
    IPreparedProjectEdit Prepare(MidoraProject project);
}

/// <summary>
/// Optional preparation contract for commands whose detached planning can be
/// expensive. The cancellation token is observed only before publication;
/// Apply and Undo remain atomic and non-cancellable.
/// </summary>
public interface ICancellableProjectEditCommand : IProjectEditCommand
{
    IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken cancellationToken);
}

public sealed class StagedProjectEdit : IDisposable
{
    private IPreparedProjectEdit? _prepared;

    internal StagedProjectEdit(
        object owner,
        string name,
        long expectedStateId,
        long expectedPublicationRevision,
        IPreparedProjectEdit prepared,
        PreparedTimelineSelection? preparedSelection)
    {
        Owner = owner;
        Name = name;
        ExpectedStateId = expectedStateId;
        ExpectedPublicationRevision = expectedPublicationRevision;
        _prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
        PreparedSelection = preparedSelection;
    }

    internal object Owner { get; }
    internal long ExpectedStateId { get; }
    internal long ExpectedPublicationRevision { get; }
    internal IPreparedProjectEdit Prepared => _prepared
        ?? throw new ObjectDisposedException(
            nameof(StagedProjectEdit),
            "The staged Project edit was already published or abandoned.");
    public string Name { get; }
    public PreparedTimelineSelection? PreparedSelection { get; }
    internal PresentationCloneCapture? PresentationClone { get; init; }

    internal void TransferToHistory()
    {
        if (_prepared is null)
        {
            throw new ObjectDisposedException(
                nameof(StagedProjectEdit),
                "The staged Project edit was already published or abandoned.");
        }
        _prepared = null;
    }

    public void Dispose()
    {
        IPreparedProjectEdit? prepared = Interlocked.Exchange(ref _prepared, null);
        if (prepared is IDisposable disposable) disposable.Dispose();
    }
}

public interface IPreparedProjectEdit
{
    bool HasChanges { get; }
    ProjectChangeSet Changes { get; }
    void Apply(MidoraProject project);
    void Undo(MidoraProject project);
}

public sealed class ProjectPropertyEditCommand<T> : IProjectEditCommand
{
    private readonly Func<MidoraProject, T> _read;
    private readonly Action<MidoraProject, T> _write;
    private readonly T _newValue;
    private readonly ProjectChangeSet _changes;
    private readonly IEqualityComparer<T> _comparer;

    public ProjectPropertyEditCommand(
        string name,
        Func<MidoraProject, T> read,
        Action<MidoraProject, T> write,
        T newValue,
        ProjectChangeSet changes,
        IEqualityComparer<T>? comparer = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Project edit command name is required.", nameof(name));
        }
        Name = name.Trim();
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _newValue = newValue;
        _changes = CloneChanges(changes ?? throw new ArgumentNullException(nameof(changes)));
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public string Name { get; }

    public IPreparedProjectEdit Prepare(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        T oldValue = _read(project);
        return new Prepared(
            _write,
            oldValue,
            _newValue,
            !_comparer.Equals(oldValue, _newValue),
            _changes);
    }

    private sealed class Prepared(
        Action<MidoraProject, T> write,
        T oldValue,
        T newValue,
        bool hasChanges,
        ProjectChangeSet changes) : IPreparedProjectEdit
    {
        public bool HasChanges { get; } = hasChanges;
        public ProjectChangeSet Changes { get; } = changes;
        public void Apply(MidoraProject project) => write(project, newValue);
        public void Undo(MidoraProject project) => write(project, oldValue);
    }

    private static ProjectChangeSet CloneChanges(ProjectChangeSet source)
    {
        ProjectChangeSet result = new()
        {
            AffectsEverything = source.AffectsEverything,
            AffectsConductor = source.AffectsConductor,
            AffectsAudioPcmCacheGeneration = source.AffectsAudioPcmCacheGeneration
        };
        result.TrackIds.UnionWith(source.TrackIds);
        result.EventInstrumentIds.UnionWith(source.EventInstrumentIds);
        result.EventInstrumentUsageIds.UnionWith(source.EventInstrumentUsageIds);
        result.MidiChannelRootIds.UnionWith(source.MidiChannelRootIds);
        result.PureMidiTrackIds.UnionWith(source.PureMidiTrackIds);
        result.PresentationTrackIds.UnionWith(source.PresentationTrackIds);
        result.PresentationEventInstrumentIds.UnionWith(
            source.PresentationEventInstrumentIds);
        result.TimelineOwnerChanges.AddRange(source.TimelineOwnerChanges);
        return result;
    }
}

public sealed class ProjectDocumentSession : IDisposable
{
    private readonly object _stagedEditOwner = new();
    private readonly object _clipboardSessionIdentity = new();
    private readonly object _sync = new();
    private readonly ProjectCompilationSession _compilation;
    private List<HistoryEntry> _entries = [];
    private readonly HashSet<string> _externalDirtyReasons = new(StringComparer.Ordinal);
    private int _cursor;
    private long _currentStateId;
    private long _nextStateId = 1;
    private long _publicationRevision;
    private long _baselineStateId;
    private bool _hasPersistentOrigin;
    private bool _notifying;

    public ProjectDocumentSession(
        ProjectCompilationSession compilation,
        ProjectDocumentOrigin origin = ProjectDocumentOrigin.Unsaved)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _hasPersistentOrigin = origin switch
        {
            ProjectDocumentOrigin.Unsaved => false,
            ProjectDocumentOrigin.Persisted => true,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        _baselineStateId = _currentStateId;
    }

    public MidoraProject Project => _compilation.Project;
    public ProjectCompilationSession Compilation => _compilation;
    internal object ClipboardSessionIdentity => _clipboardSessionIdentity;

    public bool HasPersistentOrigin
    {
        get
        {
            lock (_sync)
            {
                return _hasPersistentOrigin;
            }
        }
    }

    public bool IsModified
    {
        get
        {
            lock (_sync)
            {
                return IsModifiedCore;
            }
        }
    }

    public bool NeedsSaveBeforeClose
    {
        get
        {
            lock (_sync)
            {
                return !_hasPersistentOrigin || IsModifiedCore;
            }
        }
    }

    public bool CanUndo
    {
        get
        {
            lock (_sync)
            {
                return _cursor != 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_sync)
            {
                return _cursor != _entries.Count;
            }
        }
    }

    public string? UndoName
    {
        get
        {
            lock (_sync)
            {
                return _cursor == 0 ? null : _entries[_cursor - 1].Name;
            }
        }
    }

    public string? RedoName
    {
        get
        {
            lock (_sync)
            {
                return _cursor == _entries.Count ? null : _entries[_cursor].Name;
            }
        }
    }

    public IReadOnlyList<ProjectHistoryEntryInfo> History
    {
        get
        {
            lock (_sync)
            {
                return _entries
                    .Select(entry => new ProjectHistoryEntryInfo(
                        entry.Name,
                        entry.BeforeStateId,
                        entry.AfterStateId))
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<string> ExternalDirtyReasons
    {
        get
        {
            lock (_sync)
            {
                return _externalDirtyReasons.Order(StringComparer.Ordinal).ToArray();
            }
        }
    }

    public event EventHandler? HistoryChanged;
    public event EventHandler<ProjectContentChangedEventArgs>? ContentChanged;
    internal event Action<IReadOnlyDictionary<MidoraId, MidoraId>>? PresentationObjectsCloned;

    public ProjectEditExecution Execute(IProjectEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            ThrowIfNotifying();
            string commandName = ValidateCommandName(command.Name, nameof(command));
            var presentationClone = (command as ProjectPresentationCloneCommand)?.Capture(Project);
            using BulkEditPreparationContext preparationContext = BulkEditPreparationContext.Enter(
                BulkEditPreparationContext.Current?.Token ?? default, project: Project);
            using BoundedEditResourceLease resourceLease = preparationContext.Resources.BeginResourceLease();
            IPreparedProjectEdit? sourcePrepared = null;
            FrozenPreparedProjectEdit? prepared = null;
            try
            {
                sourcePrepared = PrepareSourceEdit(command, Project, preparationContext.Token, null);
                prepared = FreezePreparedEdit(Project, sourcePrepared);
                sourcePrepared = null;
                prepared.AttachResources(resourceLease.Complete(preparationContext.Token));
                ProjectEditExecution result = CommitPrepared(commandName, prepared);
                if (result.Changed) prepared = null;
                if (result.Changed && presentationClone is not null)
                    PresentationObjectsCloned?.Invoke(presentationClone.Resolve(Project));
                return result;
            }
            finally
            {
                prepared?.Dispose();
                DisposePrepared(sourcePrepared);
            }
        }
    }

    /// <summary>
    /// Builds an immutable edit plan without publishing it. Callers must keep
    /// ordinary Project editing locked while this method is running. The
    /// history state and monotonic publication revision are checked both after
    /// preparation and at publication.
    /// </summary>
    public StagedProjectEdit PrepareEdit(
        IProjectEditCommand command,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        string commandName = ValidateCommandName(command.Name, nameof(command));
        long expectedStateId;
        long expectedPublicationRevision;
        MidoraProject project;
        lock (_sync)
        {
            ThrowIfNotifying();
            expectedStateId = _currentStateId;
            expectedPublicationRevision = _publicationRevision;
            project = Project;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var presentationClone = (command as ProjectPresentationCloneCommand)?.Capture(project);
        var preparationProgress = progress is null ? null : new BulkEditProgressRange(progress, 0, 0.95);
        using BulkEditPreparationContext preparationContext =
            BulkEditPreparationContext.Enter(cancellationToken, preparationProgress, project: project);
        using BoundedEditResourceLease resourceLease = preparationContext.Resources.BeginResourceLease();
        IPreparedProjectEdit? sourcePrepared = null;
        FrozenPreparedProjectEdit? frozen = null;
        try
        {
            sourcePrepared = PrepareSourceEdit(command, project, cancellationToken, preparationProgress);
            PreparedTimelineSelection? preparedSelection =
                sourcePrepared is IPreparedTimelineSelectionEdit { HasPreparedSelection: true } selectionEdit
                    ? selectionEdit.PreparedSelection : null;
            if (command is ITimelineSelectionResultEditCommand && preparedSelection is null)
                throw new InvalidOperationException(
                    "A Timeline selection command did not expose its frozen selection result.");
            frozen = FreezePreparedEdit(project, sourcePrepared);
            sourcePrepared = null;
            // Spill retained result/history pages before taking the publication
            // gate lock. Slow storage cannot block UI state reads, and Cancel
            // still leaves Project, allocator, selection and history untouched.
            frozen.AttachResources(resourceLease.Complete(cancellationToken,
                progress is null ? null : new BulkEditWorkProgressRange(progress, 0.95, 0.04)));

            // Ready refers to a fully built and flushed detached plan, never
            // merely to the end of the selected-value transformation loop.
            progress?.Report(new(TimelineEditPreparationPhase.Ready, 1, 1));
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                ThrowIfNotifying();
                if (!ReferenceEquals(project, Project)
                    || expectedStateId != _currentStateId
                    || expectedPublicationRevision != _publicationRevision)
                {
                    throw new InvalidOperationException(
                        "The Project changed while the edit was being prepared. No changes were applied.");
                }
                FrozenPreparedProjectEdit prepared = frozen;
                frozen = null;
                return new(
                    _stagedEditOwner,
                    commandName,
                    expectedStateId,
                    expectedPublicationRevision,
                    prepared,
                    preparedSelection) { PresentationClone = presentationClone };
            }
        }
        finally
        {
            frozen?.Dispose();
            DisposePrepared(sourcePrepared);
        }
    }

    private static IPreparedProjectEdit PrepareSourceEdit(IProjectEditCommand command,
        MidoraProject project, CancellationToken cancellationToken,
        IProgress<TimelineEditPreparationProgress>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IPreparedProjectEdit prepared = command switch
        {
            IProgressReportingProjectEditCommand reporting => reporting.Prepare(project, cancellationToken, progress),
            ICancellableProjectEditCommand cancellable => cancellable.Prepare(project, cancellationToken),
            _ => command.Prepare(project)
        } ?? throw new InvalidOperationException("A Project edit command returned no prepared edit.");
        if (!cancellationToken.IsCancellationRequested) return prepared;
        DisposePrepared(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Unreachable cancellation state.");
    }

    public ProjectEditExecution ExecutePrepared(StagedProjectEdit staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        lock (_sync)
        {
            ThrowIfNotifying();
            if (!ReferenceEquals(staged.Owner, _stagedEditOwner))
                throw new ArgumentException("The staged edit belongs to another Project document.", nameof(staged));
            if (staged.ExpectedStateId != _currentStateId
                || staged.ExpectedPublicationRevision != _publicationRevision)
            {
                staged.Dispose();
                throw new InvalidOperationException(
                    "The Project changed after the edit was prepared. No changes were applied.");
            }
            IPreparedProjectEdit prepared = staged.Prepared;
            try
            {
                ProjectEditExecution result = CommitPrepared(staged.Name, prepared);
                if (result.Changed) staged.TransferToHistory();
                else staged.Dispose();
                if (result.Changed && staged.PresentationClone is { } clone)
                    PresentationObjectsCloned?.Invoke(clone.Resolve(Project));
                return result;
            }
            catch
            {
                staged.Dispose();
                throw;
            }
        }
    }

    private ProjectEditExecution CommitPrepared(string commandName, IPreparedProjectEdit prepared)
    {
        if (!prepared.HasChanges) return new(false, _compilation.LastAttempt);
        if (_nextStateId == long.MaxValue)
            throw new InvalidOperationException("The Project history state counter is exhausted.");
        long nextPublicationRevision = GetNextPublicationRevision();

        CanonicalCompiledResult result = _compilation.ApplyReversibleEdit(
            prepared.Apply,
            prepared.Undo,
            prepared.Changes);
        if (_cursor != _entries.Count)
        {
            for (int index = _cursor; index < _entries.Count; index++)
            {
                if (_entries[index].Prepared is IDisposable disposable)
                    disposable.Dispose();
            }
            _entries.RemoveRange(_cursor, _entries.Count - _cursor);
        }
        long nextStateId = _nextStateId++;
        _entries.Add(new(commandName, _currentStateId, nextStateId, prepared));
        _cursor++;
        _currentStateId = nextStateId;
        _publicationRevision = nextPublicationRevision;
        NotifyEditChanged(prepared.Changes);
        return new(true, result);
    }

    private static string ValidateCommandName(string? name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A Project edit command name is required.", parameterName);
        return name.Trim();
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        lock (_sync)
        {
            // Detach first: failed or reentrant cleanup cannot replay entries,
            // and no second full history array is required at close.
            List<HistoryEntry> entries = _entries;
            _entries = [];
            _cursor = 0;
            foreach (HistoryEntry entry in entries)
            {
                try { DisposePrepared(entry.Prepared); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException("Project history cleanup failed.", failures);
    }

    private static void DisposePrepared(IPreparedProjectEdit? prepared)
    {
        if (prepared is IDisposable disposable) disposable.Dispose();
    }

    public PreparedProjectHistoryTransition PrepareHistoryTransition(bool redo,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfNotifying();
            int index = redo ? _cursor : _cursor - 1;
            if ((uint)index >= (uint)_entries.Count)
                throw new InvalidOperationException(redo ? "There is no Project edit to redo." : "There is no Project edit to undo.");
            HistoryEntry entry = _entries[index];
            cancellationToken.ThrowIfCancellationRequested();
            return new(this, _publicationRevision, redo, entry.Name,
                redo ? entry.AfterStateId : entry.BeforeStateId);
        }
    }

    public CanonicalCompiledResult PublishHistoryTransition(PreparedProjectHistoryTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_sync)
        {
            if (!ReferenceEquals(transition.Document, this) || transition.Revision != _publicationRevision)
                throw new InvalidOperationException("Project history changed while preparing Undo or Redo.");
            return transition.IsRedo ? Redo() : Undo();
        }
    }

    public CanonicalCompiledResult Undo()
    {
        lock (_sync)
        {
            ThrowIfNotifying();
            if (_cursor == 0)
            {
                throw new InvalidOperationException("There is no Project edit to undo.");
            }
            long nextPublicationRevision = GetNextPublicationRevision();
            HistoryEntry entry = _entries[_cursor - 1];
            CanonicalCompiledResult result = _compilation.ApplyReversibleEdit(
                entry.Prepared.Undo,
                entry.Prepared.Apply,
                entry.Prepared.Changes);
            _cursor--;
            _currentStateId = entry.BeforeStateId;
            _publicationRevision = nextPublicationRevision;
            NotifyEditChanged(entry.Prepared.Changes);
            return result;
        }
    }

    public CanonicalCompiledResult Redo()
    {
        lock (_sync)
        {
            ThrowIfNotifying();
            if (_cursor == _entries.Count)
            {
                throw new InvalidOperationException("There is no Project edit to redo.");
            }
            long nextPublicationRevision = GetNextPublicationRevision();
            HistoryEntry entry = _entries[_cursor];
            CanonicalCompiledResult result = _compilation.ApplyReversibleEdit(
                entry.Prepared.Apply,
                entry.Prepared.Undo,
                entry.Prepared.Changes);
            _cursor++;
            _currentStateId = entry.AfterStateId;
            _publicationRevision = nextPublicationRevision;
            NotifyEditChanged(entry.Prepared.Changes);
            return result;
        }
    }

    public void MarkSaveSucceeded()
    {
        lock (_sync)
        {
            ThrowIfNotifying();
            bool changed = !_hasPersistentOrigin
                || _baselineStateId != _currentStateId
                || _externalDirtyReasons.Count != 0;
            _hasPersistentOrigin = true;
            _baselineStateId = _currentStateId;
            _externalDirtyReasons.Clear();
            if (changed)
            {
                NotifyHistoryChanged(compilationChanged: false);
            }
        }
    }

    public void MarkExternallyModified(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An external modification reason is required.", nameof(reason));
        }
        lock (_sync)
        {
            ThrowIfNotifying();
            if (_externalDirtyReasons.Add(reason.Trim()))
            {
                NotifyHistoryChanged(compilationChanged: false);
            }
        }
    }

    private bool IsModifiedCore =>
        _externalDirtyReasons.Count != 0
        || _baselineStateId != _currentStateId;

    private void NotifyEditChanged(ProjectChangeSet changes)
    {
        NotifyHistoryChanged(compilationChanged: true, changes);
    }

    public long CurrentStateId
    {
        get
        {
            lock (_sync)
            {
                return _currentStateId;
            }
        }
    }

    private void NotifyHistoryChanged(
        bool compilationChanged,
        ProjectChangeSet? changes = null)
    {
        _notifying = true;
        try
        {
            if (compilationChanged)
            {
                _compilation.NotifyCompilationChanged();
                ContentChanged?.Invoke(this, new ProjectContentChangedEventArgs(
                    changes ?? ProjectChangeSet.Everything));
            }
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _notifying = false;
        }
    }

    private void ThrowIfNotifying()
    {
        if (_notifying)
        {
            throw new InvalidOperationException(
                "Project History cannot be mutated reentrantly from a change notification.");
        }
    }

    private static FrozenPreparedProjectEdit FreezePreparedEdit(
        MidoraProject project,
        IPreparedProjectEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(prepared.Changes);
        ProjectChangeSet changes = new()
        {
            AffectsEverything = prepared.Changes.AffectsEverything,
            AffectsConductor = prepared.Changes.AffectsConductor,
            AffectsAudioPcmCacheGeneration =
                prepared.Changes.AffectsAudioPcmCacheGeneration
        };
        changes.TrackIds.UnionWith(prepared.Changes.TrackIds);
        changes.EventInstrumentIds.UnionWith(prepared.Changes.EventInstrumentIds);
        changes.EventInstrumentUsageIds.UnionWith(prepared.Changes.EventInstrumentUsageIds);
        changes.MidiChannelRootIds.UnionWith(prepared.Changes.MidiChannelRootIds);
        changes.PureMidiTrackIds.UnionWith(prepared.Changes.PureMidiTrackIds);
        changes.PresentationTrackIds.UnionWith(prepared.Changes.PresentationTrackIds);
        changes.PresentationEventInstrumentIds.UnionWith(
            prepared.Changes.PresentationEventInstrumentIds);
        changes.TimelineOwnerChanges.AddRange(prepared.Changes.TimelineOwnerChanges);
        return new(
            InstrumentChangeMaintenance.Wrap(project, ExactTimelineCollisionPolicy.Wrap(project, prepared)),
            changes);
    }

    private sealed record HistoryEntry(
        string Name,
        long BeforeStateId,
        long AfterStateId,
        IPreparedProjectEdit Prepared);

    private sealed class FrozenPreparedProjectEdit(
        IPreparedProjectEdit source,
        ProjectChangeSet changes) : IPreparedProjectEdit, IDisposable
    {
        private IPreparedProjectEdit? _source = source;
        private BoundedEditPublicationResources? _resources;

        public bool HasChanges { get; } = source.HasChanges;
        public ProjectChangeSet Changes { get; } = changes;
        public void Apply(MidoraProject project)
        {
            Current.Apply(project);
            _resources?.MarkPublished();
        }
        public void Undo(MidoraProject project) => Current.Undo(project);
        public void AttachResources(BoundedEditPublicationResources resources) => _resources = resources;

        public void Dispose()
        {
            IPreparedProjectEdit? current = Interlocked.Exchange(ref _source, null);
            try { if (current is IDisposable disposable) disposable.Dispose(); }
            finally { Interlocked.Exchange(ref _resources, null)?.Dispose(); }
        }

        private IPreparedProjectEdit Current => _source
            ?? throw new ObjectDisposedException(nameof(FrozenPreparedProjectEdit));
    }

    /// <summary>
    /// Monotonically identifies successful Project state publications in this
    /// document session. Unlike <see cref="CurrentStateId"/>, Undo and Redo
    /// advance this value so a staged edit cannot pass its publication gate
    /// after an intervening edit returns the history cursor to the same state.
    /// </summary>
    public long PublicationRevision
    {
        get
        {
            lock (_sync)
            {
                return _publicationRevision;
            }
        }
    }

    private long GetNextPublicationRevision()
    {
        if (_publicationRevision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The Project publication revision counter is exhausted.");
        }
        return _publicationRevision + 1;
    }
}
