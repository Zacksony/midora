using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Application;
using Midora.Compiler;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void Raise([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ProjectTreeNodeKind
{
    Conductor,
    InstrumentLibrary,
    EventInstrument,
    DamagedEventInstrument,
    LogicalTracks,
    LogicalTrack,
    DamagedLogicalTrack,
    ProjectSettings,
    Diagnostics
}

public sealed class ProjectTreeNode(
    ProjectTreeNodeKind kind,
    string title,
    MidoraId? objectId = null,
    string subtitle = "") : ObservableObject
{
    private string _title = title;
    private string _editText = title;
    private bool _isRenaming;

    public ProjectTreeNodeKind Kind { get; } = kind;
    public MidoraId? ObjectId { get; } = objectId;
    public string Subtitle { get; } = subtitle;
    public bool IsDamaged => Kind is ProjectTreeNodeKind.DamagedEventInstrument
        or ProjectTreeNodeKind.DamagedLogicalTrack;
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }
    public string EditText { get => _editText; set => Set(ref _editText, value); }
    public bool IsRenaming { get => _isRenaming; set => Set(ref _isRenaming, value); }
    public ObservableCollection<ProjectTreeNode> Children { get; } = [];
}

public sealed record DiagnosticRow(
    string Severity,
    string Category,
    string Code,
    string Message,
    string Source,
    bool IsCurrent,
    SourceReference SourceReference)
{
    public string Status => IsCurrent
        ? "Active"
        : string.Equals(Category, "Runtime", StringComparison.OrdinalIgnoreCase)
            ? "Runtime History"
            : string.Equals(Category, "Compile", StringComparison.OrdinalIgnoreCase)
                ? "Prior Result"
                : "Resolved";
}

public enum DesktopTaskLockLevel
{
    ProjectEdit = 2,
    MainWindow = 3,
    FullApplication = 4
}

public sealed class DesktopTaskViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private string _status = "Running";
    private string _detail = string.Empty;
    private double? _progress;
    private bool _isRunning = true;
    private bool _isCancellationAvailable = true;
    private DateTimeOffset? _completedAt;

    public DesktopTaskViewModel(string name, bool canCancel, DesktopTaskLockLevel lockLevel)
    {
        Name = name;
        CanCancel = canCancel;
        LockLevel = lockLevel;
        StartedAt = DateTimeOffset.Now;
    }

    public string Name { get; }
    public DesktopTaskLockLevel LockLevel { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? CompletedAt { get => _completedAt; private set => Set(ref _completedAt, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public double? Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) Raise(nameof(CanRequestCancel)); } }
    public bool CanCancel { get; }
    public bool CanRequestCancel => CanCancel && IsCancellationAvailable && IsRunning && !_cancellation.IsCancellationRequested;
    public bool IsCancellationAvailable
    {
        get => _isCancellationAvailable;
        private set
        {
            if (Set(ref _isCancellationAvailable, value)) Raise(nameof(CanRequestCancel));
        }
    }
    public CancellationToken CancellationToken => _cancellation.Token;

    public void Report(string detail, double? progress = null)
    {
        if (!IsRunning) return;
        Detail = detail ?? string.Empty;
        Progress = progress is null ? null : Math.Clamp(progress.Value, 0, 1);
    }

    public void RequestCancel()
    {
        if (!CanRequestCancel) return;
        _cancellation.Cancel();
        Status = "Cancelling";
        Raise(nameof(CanRequestCancel));
    }

    public void SetCancellationAvailable(bool available) => IsCancellationAvailable = available;

    internal void SealCancellationBeforePublication()
    {
        // RequestCancel and this transition run on the UI dispatcher. Disable
        // new requests first, then observe any request that won the race before
        // a prepared result enters its non-cancellable publication phase.
        SetCancellationAvailable(false);
        CancellationToken.ThrowIfCancellationRequested();
    }

    public void Complete(string status, string detail = "")
    {
        Status = status;
        Detail = detail;
        Progress = status == "Succeeded" ? 1 : Progress;
        IsRunning = false;
        CompletedAt = DateTimeOffset.Now;
    }

    public void Dispose() => _cancellation.Dispose();
}

public enum PropertyFieldValueState
{
    SameValue,
    Mixed,
    Unavailable
}

public sealed record PropertyChoiceOption(string Value, string Label);

public sealed class PropertyField(
    string key,
    string label,
    string value,
    bool isEditable = true,
    PropertyFieldValueState valueState = PropertyFieldValueState.SameValue,
    IReadOnlyList<string>? options = null,
    bool isBoolean = false,
    IReadOnlyList<PropertyChoiceOption>? choices = null) : ObservableObject
{
    private string _value = value;
    private bool _booleanValue = bool.TryParse(value, out bool parsed) && parsed;
    private PropertyFieldValueState _valueState = valueState;

    public string Key { get; } = key;
    public string Label { get; } = label;
    public bool IsEditable { get; } = isEditable;
    public string OriginalValue { get; } = value;
    public PropertyFieldValueState OriginalValueState { get; } = valueState;
    public PropertyFieldValueState ValueState
    {
        get => _valueState;
        private set
        {
            if (!Set(ref _valueState, value)) return;
            Raise(nameof(IsMixed));
            Raise(nameof(IsUnavailable));
            Raise(nameof(IsInputEnabled));
            Raise(nameof(CanActivateMixed));
            Raise(nameof(HasPendingChange));
            Raise(nameof(CanReset));
        }
    }
    public bool IsMixed => ValueState == PropertyFieldValueState.Mixed;
    public bool IsUnavailable => ValueState == PropertyFieldValueState.Unavailable;
    public IReadOnlyList<string> Options { get; } = options ?? [];
    public IReadOnlyList<PropertyChoiceOption> Choices { get; } = choices
        ?? (options ?? []).Select(option => new PropertyChoiceOption(option, option)).ToArray();
    public bool IsChoice => Choices.Count > 0;
    public bool IsBoolean { get; } = isBoolean;
    public bool IsInputEnabled => IsEditable
        && ValueState == PropertyFieldValueState.SameValue;
    public bool CanActivateMixed => IsEditable && IsMixed;
    public bool HasPendingChange => IsEditable
        && (ValueState != OriginalValueState
            || !string.Equals(Value, OriginalValue, StringComparison.Ordinal));
    public bool CanReset => HasPendingChange;
    public string Value
    {
        get => _value;
        set
        {
            value ??= string.Empty;
            if (!Set(ref _value, value)) return;
            bool parsed = bool.TryParse(value, out bool booleanValue) && booleanValue;
            if (_booleanValue != parsed)
            {
                _booleanValue = parsed;
                Raise(nameof(BooleanValue));
            }
            Raise(nameof(HasPendingChange));
            Raise(nameof(CanReset));
        }
    }
    public bool BooleanValue
    {
        get => _booleanValue;
        set
        {
            if (!Set(ref _booleanValue, value)) return;
            Value = value ? bool.TrueString : bool.FalseString;
        }
    }

    public void ActivateMixedEdit()
    {
        if (!CanActivateMixed) return;
        ValueState = PropertyFieldValueState.SameValue;
        if (IsChoice)
        {
            Value = Choices[0].Value;
        }
        else if (IsBoolean)
        {
            BooleanValue = false;
        }
        else
        {
            Value = string.Empty;
        }
    }

    public void Reset()
    {
        if (!IsEditable) return;
        ValueState = OriginalValueState;
        Value = OriginalValue;
        Raise(nameof(HasPendingChange));
        Raise(nameof(CanReset));
    }
}

public sealed class ObjectPropertiesViewModel : ObservableObject
{
    private string _title = "No selection";
    private string _context = "Select an object in the active Workspace.";
    private string? _errorText;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Context { get => _context; private set => Set(ref _context, value); }
    public string? ErrorText
    {
        get => _errorText;
        set
        {
            if (Set(ref _errorText, value)) Raise(nameof(HasError));
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public ObservableCollection<PropertyField> Fields { get; } = [];
    public bool HasEditableFields => Fields.Any(item => item.IsEditable);
    public bool HasPendingChanges => Fields.Any(item => item.HasPendingChange);
    public bool HasFields => Fields.Count != 0;
    public string CopyText => string.Join(
        Environment.NewLine,
        new[] { Title, Context }
            .Concat(Fields.Select(item => $"{item.Label}: {item.Value}")));

    public void Replace(string title, string context, IEnumerable<PropertyField> fields)
    {
        Title = title;
        Context = context;
        ErrorText = null;
        Fields.Clear();
        foreach (PropertyField field in fields) Fields.Add(field);
        Raise(nameof(HasEditableFields));
        Raise(nameof(HasPendingChanges));
        Raise(nameof(HasFields));
        Raise(nameof(CopyText));
    }
}

public enum WorkspaceTabIconKind
{
    None,
    Arrangement,
    EventInstrument,
    Settings,
    LogicalTrack,
    PureMidiTrack,
    Conductor,
    Diagnostics
}

public abstract class WorkspaceViewModel(
    WorkspaceKey key,
    string header) : ObservableObject, IDisposable
{
    private bool _isDisposed;
    private bool _isPresentationSuspended;
    public bool IsDisposed => _isDisposed;
    public bool IsPresentationSuspended => _isPresentationSuspended;
    internal EditorWorkspaceStateBinding? EditorState { get; set; }
    internal event Action? LaneViewStateCaptureRequested;
    internal void CaptureLaneViewState() => LaneViewStateCaptureRequested?.Invoke();
    internal Dictionary<MidoraId, LaneTabSession> LocalLaneSessions { get; } = [];
    private TimelineValueTraceShape _valueTraceShape;
    public virtual TimelineValueTraceShape ValueTraceShape
    {
        get => _valueTraceShape;
        set => Set(ref _valueTraceShape, value);
    }

    public void SuspendPresentation()
    {
        if (IsDisposed || IsPresentationSuspended) return;
        CaptureLaneViewState();
        _isPresentationSuspended = true;
        EditorState?.Suspend();
        CancelBackgroundPresentationWork();
        OnPresentationSuspended();
        Raise(nameof(IsPresentationSuspended));
    }

    public void ResumePresentation()
    {
        if (IsDisposed || !IsPresentationSuspended) return;
        _isPresentationSuspended = false;
        EditorState?.ApplyLatestProfile();
        LastOnionRevision = (-1, -1, null);
        OnPresentationResumed();
        Raise(nameof(IsPresentationSuspended));
    }

    protected virtual void OnPresentationSuspended() { }
    protected virtual void OnPresentationResumed() => RefreshSelectionPresentation();
    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        if (IsDisposed) return;
        EditorState?.Dispose();
        EditorState = null;
        LaneViewStateCaptureRequested = null;
        LocalLaneSessions.Clear();
        _isDisposed = true;
        _isPresentationSuspended = true;
        CancelOwnedSelectionPresentationWork();
        CancelBackgroundPresentationWork();
        DisposeCore();
        ObjectList.SetFactory(null);
        Selection.Clear();
        SelectionSnapshot = TimelineSelectionSnapshot.CreateUnavailable(Selection.Revision);
        _selectionPrefetchCancellation.Dispose();
        Raise(nameof(IsDisposed));
        Raise(nameof(IsPresentationSuspended));
        GC.SuppressFinalize(this);
    }
    private TimelineOnionSnapshot? _onionSnapshot;
    private bool _isOnionEnabled;
    private bool _canConfigureOnion;
    public bool IsOnionEnabled { get => _isOnionEnabled; internal set => Set(ref _isOnionEnabled, value); }
    public bool CanConfigureOnion { get => _canConfigureOnion; internal set => Set(ref _canConfigureOnion, value); }
    internal (long Document, long Presentation, MidoraId? Voice) LastOnionRevision { get; set; } = (-1, -1, null);
    public TimelineOnionSnapshot? OnionSnapshot
    {
        get => _onionSnapshot;
        internal set => Set(ref _onionSnapshot, value);
    }
    private const int SynchronousSelectionMetricsLimit = 4_096;
    private const int DeferredSelectionMetricsDelayMilliseconds = 100;
    private string _header = header;
    private int? _activeLane;
    private TimelineSelectionSnapshot _selectionSnapshot = new(0, [], null);
    private CancellationTokenSource _selectionPrefetchCancellation = new();
    private readonly SemaphoreSlim _selectionMetricsGate = new(1, 1);
    private long _selectionPrefetchGeneration;
    private long _selectionPresentationScope;
    private WorkspaceTabIconKind _tabIconKind = key.Kind switch
    {
        WorkspaceKind.Arrangement or WorkspaceKind.AllTracks => WorkspaceTabIconKind.Arrangement,
        WorkspaceKind.EventInstrumentLibrary or WorkspaceKind.EventInstrumentEditor =>
            WorkspaceTabIconKind.EventInstrument,
        WorkspaceKind.ProjectSettings => WorkspaceTabIconKind.Settings,
        WorkspaceKind.Diagnostics => WorkspaceTabIconKind.Diagnostics,
        WorkspaceKind.ConductorTrack => WorkspaceTabIconKind.Conductor,
        WorkspaceKind.SegmentEditor => WorkspaceTabIconKind.LogicalTrack,
        _ => WorkspaceTabIconKind.None
    };

    public WorkspaceKey Key { get; } = key;
    public WorkspaceKind Kind => Key.Kind;
    public MidoraId? ObjectId => Key.ObjectId;
    public bool CanClose => Kind != WorkspaceKind.Arrangement;
    public bool CanReorder => Kind != WorkspaceKind.Arrangement;
    public string Header
    {
        get => _header;
        protected set => Set(ref _header, value);
    }
    public WorkspaceTabIconKind TabIconKind
    {
        get => _tabIconKind;
        protected set => Set(ref _tabIconKind, value);
    }
    public WorkspaceSelection Selection { get; } = new();
    public TimelineObjectListState ObjectList { get; } = new();
    // Document publication and render/selection revisions are different axes.
    // Only the session stamps this after rebuilding from the current Project.
    internal long PresentationDocumentRevision { get; set; } = -1;
    public TimelineSelectionSnapshot SelectionSnapshot
    {
        get => _selectionSnapshot;
        protected set => Set(ref _selectionSnapshot, value);
    }
    public int? ActiveLane
    {
        get => _activeLane;
        set => Set(ref _activeLane, value is null ? null : Math.Max(0, value.Value));
    }

    public abstract void Rebuild(MidoraProject project, long revision);

    public virtual void RefreshSelectionPresentation()
    {
        if (!IsDisposed)
            SelectionSnapshot = TimelineSelectionSnapshot.FromWorkspaceSelection(Selection);
    }

    /// <summary>
    /// Publishes metrics accumulated by an exact background selection query.
    /// This is deliberately separate from <see cref="RefreshSelectionPresentation"/>:
    /// resolving a million IDs again after the range query would defeat paging
    /// and can retain gigabytes of optional presentation state.
    /// </summary>
    public virtual void PublishMaterializedSelection(
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics> metrics,
        bool metricsAreComplete,
        TimelineSelectionRenderIndex? renderIndex = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (IsDisposed) return;
        _selectionPrefetchCancellation.Cancel();
        _selectionPrefetchGeneration = checked(_selectionPrefetchGeneration + 1);
        SelectionSnapshot = TimelineSelectionSnapshot.FromWorkspaceSelection(
            Selection,
            metrics: metrics,
            metricsAreComplete: metricsAreComplete,
            renderIndex: renderIndex);
    }

    /// <summary>
    /// Completes aggregate metrics for a selection that has already been
    /// published.  The formal ID set remains immediately usable while this
    /// optional, revision-bound scan runs in the background.
    /// </summary>
    protected void ScheduleMaterializedSelectionMetricsIfNeeded(
        bool metricsAreComplete,
        IReadOnlyList<TimelineRenderSnapshot?> snapshots,
        Action? afterPublish = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (IsDisposed || IsPresentationSuspended || Selection.IdSet.Count == 0
            || metricsAreComplete && SelectionSnapshot.HasRenderIndex)
        {
            return;
        }
        ScheduleDeferredSelectionMetrics(
            snapshots,
            afterPublish,
            Selection.IdSet,
            Selection.Revision,
            _selectionPresentationScope,
            Dispatcher.CurrentDispatcher);
    }

    protected void BeginPresentationRebuild()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _selectionPresentationScope = checked(_selectionPresentationScope + 1);
        _selectionPrefetchCancellation.Cancel();
    }

    /// <summary>
    /// Invalidates optional selection-presentation work when this Workspace is
    /// removed from the live session. The immutable formal selection remains
    /// untouched, but no orphaned metrics scan may retain paged snapshots or
    /// publish back into a closed Workspace.
    /// </summary>
    public virtual void CancelBackgroundPresentationWork() => CancelOwnedSelectionPresentationWork();

    private void CancelOwnedSelectionPresentationWork()
    {
        OnionSnapshot = null;
        LastOnionRevision = (-1, -1, null);
        ObjectList.SetActive(false);
        _selectionPresentationScope = checked(_selectionPresentationScope + 1);
        _selectionPrefetchGeneration = checked(_selectionPrefetchGeneration + 1);
        if (!_selectionPrefetchCancellation.IsCancellationRequested)
            _selectionPrefetchCancellation.Cancel();
    }

    protected bool TryRefreshSelectionPresentation(
        IReadOnlyList<TimelineRenderSnapshot?> snapshots,
        Action? afterPublish = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (IsDisposed) return false;
        IReadOnlySet<MidoraId> ids = Selection.IdSet;
        long revision = Selection.Revision;
        long scope = _selectionPresentationScope;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        if (ids.Count > SynchronousSelectionMetricsLimit)
        {
            // Selection IDs are the formal interaction state. Aggregate position
            // metrics are only an optimization; resolving a large paged selection
            // during every content refresh would put cold page I/O on the UI path.
            SelectionSnapshot = TimelineSelectionSnapshot.FromWorkspaceSelection(Selection);
            afterPublish?.Invoke();
            ScheduleDeferredSelectionMetrics(
                snapshots,
                afterPublish,
                ids,
                revision,
                scope,
                dispatcher);
            return true;
        }
        List<TimelineRenderItem> resolved = new(ids.Count);
        HashSet<(MidoraId Id, TimelineItemKind Kind)> resolvedItems = [];
        List<TimelineRenderSnapshot> pending = [];
        foreach (TimelineRenderSnapshot? snapshot in snapshots)
        {
            if (snapshot is null || ids.Count == 0) continue;
            int firstAdded = resolved.Count;
            if (!snapshot.TryQueryByIdsCached(ids, resolved))
            {
                pending.Add(snapshot);
                continue;
            }
            for (int index = firstAdded; index < resolved.Count; index++)
            {
                if (!resolvedItems.Add((resolved[index].Id, resolved[index].Kind)))
                    resolved.RemoveAt(index--);
            }
        }

        if (pending.Count == 0)
        {
            _selectionPrefetchCancellation.Cancel();
            SelectionSnapshot = TimelineSelectionSnapshot.FromWorkspaceSelection(
                Selection,
                resolved);
            afterPublish?.Invoke();
            return true;
        }

        // Pending is not Empty. Preserve the immutable IDs immediately but do
        // not fabricate position metrics until all required cold pages exist.
        SelectionSnapshot = TimelineSelectionSnapshot.FromWorkspaceSelection(Selection);
        if (IsPresentationSuspended) return false;
        _selectionPrefetchCancellation.Cancel();
        _selectionPrefetchCancellation.Dispose();
        _selectionPrefetchCancellation = new();
        CancellationToken cancellationToken = _selectionPrefetchCancellation.Token;
        long generation = checked(++_selectionPrefetchGeneration);
        TimelineRenderSnapshot?[] capturedSnapshots = snapshots.ToArray();
        _ = Task.Run(
            () =>
            {
                foreach (TimelineRenderSnapshot snapshot in pending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    snapshot.PrefetchIds(ids, cancellationToken);
                }
            },
            cancellationToken).ContinueWith(
                task =>
                {
                    if (task.IsCanceled || cancellationToken.IsCancellationRequested) return;
                    if (task.IsFaulted)
                    {
                        System.Diagnostics.Trace.TraceError(
                            $"Selection ID prefetch failed: {task.Exception}");
                        return;
                    }
                    WorkspacePresentationDispatch.Post(dispatcher, cancellationToken,
                        () =>
                        {
                            if (IsDisposed || IsPresentationSuspended || cancellationToken.IsCancellationRequested
                                || generation != _selectionPrefetchGeneration
                                || revision != Selection.Revision
                                || scope != _selectionPresentationScope)
                            {
                                return;
                            }
                            TryRefreshSelectionPresentation(
                                capturedSnapshots,
                                afterPublish);
                        });
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        return false;
    }

    private void ScheduleDeferredSelectionMetrics(
        IReadOnlyList<TimelineRenderSnapshot?> snapshots,
        Action? afterPublish,
        IReadOnlySet<MidoraId> ids,
        long revision,
        long scope,
        Dispatcher dispatcher)
    {
        if (IsDisposed || IsPresentationSuspended) return;
        _selectionPrefetchCancellation.Cancel();
        _selectionPrefetchCancellation.Dispose();
        _selectionPrefetchCancellation = new();
        CancellationToken cancellationToken = _selectionPrefetchCancellation.Token;
        long generation = checked(++_selectionPrefetchGeneration);
        TimelineRenderSnapshot[] capturedSnapshots = snapshots
            .OfType<TimelineRenderSnapshot>()
            .ToArray();
        _ = Task.Run(
            async () =>
            {
                // A selection refresh is commonly followed immediately by a
                // gesture or command.  Give that foreground work a chance to
                // cancel this optional presentation aggregate before it starts
                // touching cold pages or competing for CPU.
                await Task.Delay(
                    DeferredSelectionMetricsDelayMilliseconds,
                    cancellationToken).ConfigureAwait(false);
                await _selectionMetricsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics> metrics =
                        new Dictionary<TimelineItemKind, TimelineSelectionMetrics>();
                    List<TimelineSelectionRenderIndex> renderIndexes = [];
                    foreach (TimelineRenderSnapshot snapshot in capturedSnapshots)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        TimelineSelectionPresentationMaterialization materialization =
                            snapshot.MaterializeSelectionPresentation(ids, cancellationToken);
                        metrics = TimelineRenderSnapshot.MergeSelectionMetrics(
                            metrics,
                            materialization.Metrics);
                        if (materialization.RenderIndex.Count != 0)
                            renderIndexes.Add(materialization.RenderIndex);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    return new TimelineSelectionPresentationMaterialization(
                        metrics,
                        TimelineSelectionRenderIndex.Merge(renderIndexes));
                }
                finally
                {
                    _selectionMetricsGate.Release();
                }
            },
            cancellationToken).ContinueWith(
                task =>
                {
                    if (task.IsCanceled || cancellationToken.IsCancellationRequested) return;
                    if (task.IsFaulted)
                    {
                        System.Diagnostics.Trace.TraceError(
                            $"Deferred selection metrics failed: {task.Exception}");
                        return;
                    }
                    WorkspacePresentationDispatch.Post(dispatcher, cancellationToken,
                        () =>
                        {
                            if (IsDisposed || IsPresentationSuspended || cancellationToken.IsCancellationRequested
                                || generation != _selectionPrefetchGeneration
                                || revision != Selection.Revision
                                || scope != _selectionPresentationScope)
                            {
                                return;
                            }
                            SelectionSnapshot = TimelineSelectionSnapshot.FromWorkspaceSelection(
                                Selection,
                                metrics: task.Result.Metrics,
                                metricsAreComplete: true,
                                renderIndex: task.Result.RenderIndex);
                            afterPublish?.Invoke();
                        });
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }
}

public enum TimelineWorkspaceMode
{
    Arrangement,
    Segment,
    Conductor
}

public readonly record struct TimelineSubdivision(
    int Numerator,
    int Denominator,
    string Label,
    bool IsBar = false)
{
    public string ShortLabel => IsBar ? "Bar" : $"{Numerator}/{Denominator}";

    public long ToTicks(int ticksPerQuarterNote, int barNumerator = 4, int barDenominator = 4)
    {
        if (ticksPerQuarterNote <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        if (IsBar)
        {
            if (barNumerator <= 0 || barDenominator <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(barNumerator));
            }
            return ProjectTimelineGrid.ResolveWholeNoteFractionStep(
                ticksPerQuarterNote,
                barNumerator,
                barDenominator);
        }
        if (Numerator <= 0 || Denominator <= 0)
        {
            throw new InvalidOperationException("Timeline subdivisions must be positive.");
        }
        return ProjectTimelineGrid.ResolveWholeNoteFractionStep(
            ticksPerQuarterNote,
            Numerator,
            Denominator);
    }

    public override string ToString() => ShortLabel;

    public static bool TryParse(string? text, out TimelineSubdivision value)
    {
        string normalized = text?.Trim() ?? string.Empty;
        if (string.Equals(normalized, "Bar", StringComparison.OrdinalIgnoreCase))
        {
            value = Presets[0];
            return true;
        }
        int slash = normalized.IndexOf('/');
        int denominatorEnd = slash + 1;
        while (denominatorEnd < normalized.Length && char.IsAsciiDigit(normalized[denominatorEnd]))
        {
            denominatorEnd++;
        }
        if (slash > 0
            && int.TryParse(normalized[..slash], out int numerator)
            && denominatorEnd > slash + 1
            && int.TryParse(normalized[(slash + 1)..denominatorEnd], out int denominator)
            && numerator > 0
            && denominator > 0)
        {
            value = new(numerator, denominator, $"{numerator}/{denominator}");
            return true;
        }
        if (int.TryParse(normalized, out int customDenominator) && customDenominator > 0)
        {
            value = new(1, customDenominator, $"1/{customDenominator}");
            return true;
        }
        value = default;
        return false;
    }

    public static IReadOnlyList<TimelineSubdivision> Presets { get; } =
    [
        new(1, 1, "Bar", IsBar: true),
        new(1, 1, "1/1 · Whole"),
        new(1, 2, "1/2 · Half"),
        new(1, 3, "1/3 · Half triplet"),
        new(1, 4, "1/4 · Quarter"),
        new(1, 6, "1/6 · Quarter triplet"),
        new(3, 16, "3/16 · Dotted eighth"),
        new(1, 8, "1/8 · Eighth"),
        new(1, 12, "1/12 · Eighth triplet"),
        new(3, 32, "3/32 · Dotted sixteenth"),
        new(1, 16, "1/16 · Sixteenth"),
        new(1, 24, "1/24 · Sixteenth triplet"),
        new(3, 64, "3/64 · Dotted thirty-second"),
        new(1, 32, "1/32 · Thirty-second"),
        new(1, 48, "1/48 · Thirty-second triplet"),
        new(1, 64, "1/64 · Sixty-fourth"),
        new(1, 128, "1/128"),
        new(1, 256, "1/256")
    ];
}

public sealed class TimelineEditorSettings : ObservableObject
{
    private TimelineSubdivision _displaySubdivision = new(1, 1, "Bar", IsBar: true);
    private TimelineSubdivision _operationSubdivision = new(1, 16, "1/16 · Sixteenth");
    private bool _snapEnabled = true;
    private bool _gridVisible = true;
    private long _defaultLengthTicks = 768;
    private int _defaultVelocity = 100;
    private int _ticksPerQuarterNote = 768;
    private int _barNumerator = 4;
    private int _barDenominator = 4;
    private ProjectTimeSignatureMap? _timeSignatureMap;

    public IReadOnlyList<TimelineSubdivision> SubdivisionPresets => TimelineSubdivision.Presets;
    public TimelineSubdivision DisplaySubdivision
    {
        get => _displaySubdivision;
        set
        {
            if (!Set(ref _displaySubdivision, value)) return;
            Raise(nameof(DisplayGridStepTicks));
            Raise(nameof(DisplayGridUsesBars));
            Raise(nameof(DisplayGridLabel));
            Raise(nameof(DisplaySubdivisionText));
        }
    }
    public TimelineSubdivision OperationSubdivision
    {
        get => _operationSubdivision;
        set
        {
            if (!Set(ref _operationSubdivision, value)) return;
            Raise(nameof(OperationStepTicks));
            Raise(nameof(EffectiveOperationStepTicks));
            Raise(nameof(EffectiveOperationUsesBars));
            Raise(nameof(OperationGridLabel));
            Raise(nameof(OperationSubdivisionText));
        }
    }
    public bool SnapEnabled
    {
        get => _snapEnabled;
        set
        {
            if (!Set(ref _snapEnabled, value)) return;
            Raise(nameof(EffectiveOperationStepTicks));
            Raise(nameof(EffectiveOperationUsesBars));
        }
    }
    public bool GridVisible { get => _gridVisible; set => Set(ref _gridVisible, value); }
    public long DefaultLengthTicks
    {
        get => _defaultLengthTicks;
        set => Set(ref _defaultLengthTicks, Math.Max(1, value));
    }
    public int DefaultVelocity
    {
        get => _defaultVelocity;
        set => Set(ref _defaultVelocity, Math.Clamp(value, 1, 127));
    }
    public long DisplayGridStepTicks => DisplaySubdivision.ToTicks(
        _ticksPerQuarterNote, _barNumerator, _barDenominator);
    public long OperationStepTicks => OperationSubdivision.ToTicks(
        _ticksPerQuarterNote, _barNumerator, _barDenominator);
    public long EffectiveOperationStepTicks => SnapEnabled ? OperationStepTicks : 1;
    public int TicksPerQuarterNote => _ticksPerQuarterNote;
    public bool DisplayGridUsesBars => DisplaySubdivision.IsBar;
    public bool EffectiveOperationUsesBars => SnapEnabled && OperationSubdivision.IsBar;
    public ProjectTimeSignatureMap? TimeSignatureMap => _timeSignatureMap;
    public string DisplayGridLabel => $"Grid {DisplaySubdivision.Label}";
    public string OperationGridLabel => $"Step {OperationSubdivision.Label}";
    public string DisplaySubdivisionText
    {
        get => DisplaySubdivision.IsBar
            ? "Bar"
            : $"{DisplaySubdivision.Numerator}/{DisplaySubdivision.Denominator}";
        set
        {
            if (TimelineSubdivision.TryParse(value, out TimelineSubdivision parsed))
            {
                DisplaySubdivision = parsed;
            }
        }
    }
    public string OperationSubdivisionText
    {
        get => OperationSubdivision.IsBar
            ? "Bar"
            : $"{OperationSubdivision.Numerator}/{OperationSubdivision.Denominator}";
        set
        {
            if (TimelineSubdivision.TryParse(value, out TimelineSubdivision parsed))
            {
                OperationSubdivision = parsed;
            }
        }
    }

    public void ConfigureProject(MidoraProject project, long referenceTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        _ticksPerQuarterNote = project.TicksPerQuarterNote;
        _timeSignatureMap = ProjectTimeSignatureMap.GetOrCreate(project);
        project.Conductor.TimeSignatures.CaptureQuerySnapshot()
            .TryGetAtOrBeforeTick(Math.Max(0, referenceTick), out TimeSignatureChange? signature);
        _barNumerator = signature?.Numerator ?? 4;
        _barDenominator = signature?.Denominator ?? 4;
        Raise(nameof(DisplayGridStepTicks));
        Raise(nameof(OperationStepTicks));
        Raise(nameof(EffectiveOperationStepTicks));
        Raise(nameof(TicksPerQuarterNote));
        Raise(nameof(TimeSignatureMap));
    }

    public long SnapAbsolute(long tick, int movementDirection = 0) =>
        TimelineGridQuantization.SnapAbsolute(
            Math.Max(0, tick),
            EffectiveOperationStepTicks,
            EffectiveOperationUsesBars,
            TimeSignatureMap,
            movementDirection);

    public long SnapDelta(long delta, long targetTick) =>
        TimelineGridQuantization.SnapDelta(
            delta,
            Math.Max(0, targetTick),
            EffectiveOperationStepTicks,
            EffectiveOperationUsesBars,
            TimeSignatureMap);

    public void Reset(bool arrangement, int ticksPerQuarterNote = 768)
    {
        _ticksPerQuarterNote = Math.Max(1, ticksPerQuarterNote);
        _timeSignatureMap = null;
        DisplaySubdivision = TimelineSubdivision.Presets.Single(item => item.IsBar);
        OperationSubdivision = TimelineSubdivision.Presets.Single(item =>
            !item.IsBar
            && item.Numerator == 1
            && item.Denominator == (arrangement ? 8 : 16));
        SnapEnabled = true;
        GridVisible = true;
        DefaultLengthTicks = _ticksPerQuarterNote;
        DefaultVelocity = 100;
        ConfigureBarDefaults();
        Raise(nameof(TicksPerQuarterNote));
        Raise(nameof(TimeSignatureMap));
    }

    private void ConfigureBarDefaults()
    {
        _barNumerator = 4;
        _barDenominator = 4;
        Raise(nameof(DisplayGridStepTicks));
        Raise(nameof(OperationStepTicks));
        Raise(nameof(EffectiveOperationStepTicks));
    }
}

public sealed record ConductorEventRow(
    MidoraId Id,
    long Tick,
    string Type,
    string Value);

public sealed record ParameterLaneOption(
    MidoraId ParameterId,
    MidoraId? LaneId,
    string Label,
    bool IsBroken = false,
    DirectMidiEventLaneTarget? DirectMidiTarget = null,
    bool IsOpaqueMidiLane = false)
{
    public bool IsDirectMidiLane => DirectMidiTarget is not null || IsOpaqueMidiLane;
}

public readonly record struct DirectMidiEventLaneTarget(
    DirectMidiChannelEventKind Kind,
    int Data1)
{
    public bool UsesData1AsSelector => Kind is DirectMidiChannelEventKind.ControlChange
        or DirectMidiChannelEventKind.PolyphonicKeyPressure
        or DirectMidiChannelEventKind.NoteOn
        or DirectMidiChannelEventKind.NoteOff;
}

public sealed class TrackSelectionRow(
    MidoraId id,
    string name,
    bool isSelected,
    bool isPureMidi = false) : ObservableObject
{
    private bool _isSelected = isSelected;
    public MidoraId Id { get; } = id;
    public string Name { get; } = name;
    public bool IsPureMidi { get; } = isPureMidi;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed record EventInstrumentBrowserRow(
    MidoraId Id,
    string Name,
    int UsageCount,
    int TrackCount,
    uint ColorArgb)
{
    public string ColorText => $"#{ColorArgb:X8}";
    public string Detail => UsageCount == 0
        ? "Unused"
        : $"{UsageCount} usage{(UsageCount == 1 ? string.Empty : "s")} · "
            + $"{TrackCount} track{(TrackCount == 1 ? string.Empty : "s")}";
}

internal static class TimelineLowerEditorLayout
{
    public const double DefaultHeight = 190;
    public const double MinimumHeight = 110;
    public const double MaximumHeight = 520;
}

public sealed partial class TimelineWorkspaceViewModel : WorkspaceViewModel, IPlaybackTimelineWorkspace
{
    private const int MaterializedSegmentPreviewThreshold = 4096;
    private readonly Dictionary<MidoraId, SegmentPreviewCacheEntry> _segmentPreviewCache = [];
    private readonly Dictionary<MidoraId, DirectMidiEventTargetCacheEntry>
        _directMidiEventTargetCache = [];
    private readonly HashSet<MidoraId> _mutedTrackIds = [];
    private readonly HashSet<MidoraId> _soloTrackIds = [];
    private readonly HashSet<MidoraId> _mutedSharedGroupIds = [];
    private readonly HashSet<MidoraId> _soloSharedGroupIds = [];
    private readonly HashSet<DirectMidiEventLaneTarget> _directMidiLaneTargets = [];
    private TimelineRenderSnapshot? _snapshot;
    private TimelineRenderSnapshot? _rulerSnapshot;
    private TimelineRenderSnapshot? _parameterSnapshot;
    private TimelineRenderSnapshot? _velocitySnapshot;
    private int _activeParameterLaneIndex;
    private MidoraId? _explicitParameterLaneId;
    private long _explicitParameterLaneSelectionRevision = -1;
    private string _context = string.Empty;
    private long _startTick;
    private long _tickSpan;
    private int _firstLane;
    private double _laneHeight;
    private long? _rangeStartTick;
    private long? _rangeEndTick;
    private long? _timeRangeStartTick;
    private long? _timeRangeEndTick;
    private long _timelineExtentEndTick = 3072;
    private long? _editCursorTick;
    private long? _playbackCursorTick;
    private long _projectTickOffset;
    private bool _viewportInitialized;
    private TimelineToolMode _toolMode = TimelineToolMode.Select;
    private double _activeValueMinimum;
    private double _activeValueMaximum = 127;
    private double _activeValueDisplayOffset;
    private bool _activeValueIntegral = true;
    private bool _activeValueDragEnabled = true;
    private bool _isLowerEditorVisible = true;
    private double _lowerEditorHeight = TimelineLowerEditorLayout.DefaultHeight;
    private bool _isEventInstrumentPaneVisible;
    private MidoraId? _selectedArrangementTrackId;
    private bool _isConductorTrackSelected;
    private ConductorEventRow? _selectedConductorEvent;
    private GridLength _conductorBottomEditorRowHeight = new(1, GridUnitType.Star);
    private CancellationTokenSource _midiTargetDiscoveryCancellation = new();
    private long _midiTargetDiscoveryGeneration;
    private string? _laneCountError;
    private MidiSegment? _midiTargetDiscoverySegment;
    private DirectMidiChannelEventQuerySnapshot? _midiTargetDiscoverySnapshot;

    public TimelineWorkspaceViewModel(
        WorkspaceKey key,
        string header,
        TimelineWorkspaceMode mode,
        TimelineEditorSettings? editorSettings = null)
        : base(key, header)
    {
        Mode = mode;
        EditorSettings = editorSettings ?? new TimelineEditorSettings();
        EditorSettings.PropertyChanged += OnEditorSettingsPropertyChanged;
        _laneHeight = mode switch
        {
            TimelineWorkspaceMode.Arrangement => 56,
            TimelineWorkspaceMode.Conductor => 27,
            _ => PianoEditorDefaults.LaneHeight
        };
        _firstLane = mode == TimelineWorkspaceMode.Segment ? PianoEditorDefaults.SegmentFirstLane : 0;
        _tickSpan = PianoEditorDefaults.TickSpan;
    }

    private void OnEditorSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (IsDisposed) return;
        if (args.PropertyName is nameof(TimelineEditorSettings.DisplayGridStepTicks)
            or nameof(TimelineEditorSettings.DisplayGridLabel)
            or nameof(TimelineEditorSettings.EffectiveOperationStepTicks)
            or nameof(TimelineEditorSettings.GridVisible))
        {
            Raise(nameof(GridStepTicks));
            Raise(nameof(OperationStepTicks));
            Raise(nameof(GridLabel));
            Raise(nameof(GridVisible));
        }
    }

    protected override void OnPresentationSuspended()
    {
        SuspendConductorPresentation();
        base.OnPresentationSuspended();
    }

    protected override void OnPresentationResumed()
    {
        ResumeConductorPresentation();
        if (_midiTargetDiscoverySegment is { } segment && _midiTargetDiscoverySnapshot is { } snapshot)
            ScheduleMidiTargetDiscovery(segment, snapshot);
        base.OnPresentationResumed();
    }

    protected override void DisposeCore()
    {
        EditorSettings.PropertyChanged -= OnEditorSettingsPropertyChanged;
        _midiTargetDiscoveryCancellation.Dispose();
        _midiTargetDiscoverySegment = null;
        _midiTargetDiscoverySnapshot = null;
        _segmentPreviewCache.Clear();
        _directMidiEventTargetCache.Clear();
        Snapshot = null;
        RulerSnapshot = null;
        ParameterSnapshot = null;
        VelocitySnapshot = null;
        EventInstrumentBrowser.Clear();
        ParameterLaneOptions.Clear();
        DisposeConductorPresentation();
        base.DisposeCore();
    }

    public TimelineWorkspaceMode Mode { get; }
    public TimelineEditorSettings EditorSettings { get; }
    public TimelineEditorSettings LaneEditorSettings { get; } = new();
    public bool IsSegment => Mode == TimelineWorkspaceMode.Segment;
    private bool _hasInstrumentChangesLane;
    public bool HasInstrumentChangesLane { get => _hasInstrumentChangesLane; private set => Set(ref _hasInstrumentChangesLane, value); }
    public bool IsConductor => Mode == TimelineWorkspaceMode.Conductor;
    public bool IsArrangement => Mode == TimelineWorkspaceMode.Arrangement;
    public bool IsLowerEditorVisible
    {
        get => IsSegment && _isLowerEditorVisible;
        set
        {
            if (!value && _isLowerEditorVisible) CaptureLaneViewState();
            if (!IsSegment || !Set(ref _isLowerEditorVisible, value)) return;
            Raise(nameof(BottomEditorRowHeight));
            Raise(nameof(BottomEditorMinimumHeight));
        }
    }
    public GridLength BottomEditorRowHeight
    {
        get => IsConductor
            ? _conductorBottomEditorRowHeight
            : IsLowerEditorVisible
                ? new GridLength(_lowerEditorHeight)
                : new GridLength(0);
        set
        {
            if (!(IsSegment || IsConductor) || !double.IsFinite(value.Value))
            {
                return;
            }
            if (IsConductor)
            {
                GridLength normalized;
                if (value.IsStar && value.Value > 0)
                {
                    normalized = value;
                }
                else if (value.IsAbsolute)
                {
                    normalized = new GridLength(Math.Clamp(
                        value.Value,
                        BottomEditorMinimumHeight,
                        BottomEditorMaximumHeight));
                }
                else
                {
                    return;
                }
                if (_conductorBottomEditorRowHeight == normalized) return;
                _conductorBottomEditorRowHeight = normalized;
                Raise();
                return;
            }
            if (!value.IsAbsolute) return;
            if (!IsLowerEditorVisible) return;
            double height = Math.Clamp(
                value.Value,
                BottomEditorMinimumHeight,
                BottomEditorMaximumHeight);
            if (!Set(ref _lowerEditorHeight, height, nameof(BottomEditorRowHeight))) return;
        }
    }
    public double BottomEditorMinimumHeight => IsConductor
        ? 110
        : IsLowerEditorVisible
            ? TimelineLowerEditorLayout.MinimumHeight
            : 0;
    public double BottomEditorMaximumHeight => IsConductor
        ? 720
        : TimelineLowerEditorLayout.MaximumHeight;

    internal double LastLowerEditorHeight => _lowerEditorHeight;
    internal void RestoreLowerEditorHeight(double height)
    {
        if (Set(ref _lowerEditorHeight, height, nameof(BottomEditorRowHeight))) Raise(nameof(BottomEditorMinimumHeight));
    }

    public ConductorEventRow? SelectedConductorEvent
    {
        get => _selectedConductorEvent;
        set
        {
            if (ReferenceEquals(_selectedConductorEvent, value)) return;
            _selectedConductorEvent = value;
            Raise();
        }
    }
    public override void RefreshSelectionPresentation()
    {
        TryRefreshSelectionPresentation(
            // Note snapshots also produce the Velocity metrics/render alias.
            // Resolving the same million selected note IDs through the
            // velocity projection would decode the identical pages twice.
            [Snapshot, ParameterSnapshot, RulerSnapshot, ConductorTempoSnapshot],
            IsConductor
                ? RefreshConductorPrimary
                : null);
    }

    public override void PublishMaterializedSelection(
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics> metrics,
        bool metricsAreComplete,
        TimelineSelectionRenderIndex? renderIndex = null)
    {
        base.PublishMaterializedSelection(metrics, metricsAreComplete, renderIndex);
        if (IsConductor)
        {
            RefreshConductorPrimary();
        }
        ScheduleMaterializedSelectionMetricsIfNeeded(
            metricsAreComplete,
            [Snapshot, ParameterSnapshot, RulerSnapshot, ConductorTempoSnapshot],
            IsConductor
                ? RefreshConductorPrimary
                : null);
    }
    public ObservableCollection<EventInstrumentBrowserRow> EventInstrumentBrowser { get; } = [];
    public bool IsEventInstrumentPaneVisible
    {
        get => IsArrangement && _isEventInstrumentPaneVisible;
        set
        {
            if (!IsArrangement || !Set(ref _isEventInstrumentPaneVisible, value)) return;
            Raise(nameof(EventInstrumentPaneWidth));
        }
    }
    public GridLength EventInstrumentPaneWidth => IsEventInstrumentPaneVisible
        ? new GridLength(238)
        : new GridLength(0);
    public MidoraId? SelectedArrangementTrackId
    {
        get => IsArrangement ? _selectedArrangementTrackId : null;
        set
        {
            if (!IsArrangement) return;
            Set(ref _selectedArrangementTrackId, value);
        }
    }
    public bool IsConductorTrackSelected
    {
        get => IsArrangement && _isConductorTrackSelected;
        set
        {
            if (!IsArrangement) return;
            Set(ref _isConductorTrackSelected, value);
        }
    }
    public TimelineSurfaceMode SurfaceMode => Mode switch
    {
        TimelineWorkspaceMode.Arrangement => TimelineSurfaceMode.Arrangement,
        TimelineWorkspaceMode.Segment => TimelineSurfaceMode.PianoRoll,
        TimelineWorkspaceMode.Conductor => TimelineSurfaceMode.Conductor,
        _ => TimelineSurfaceMode.General
    };
    public TimelineToolMode ToolMode
    {
        get => _toolMode;
        set
        {
            if (!Set(ref _toolMode, value)) return;
            Raise(nameof(IsSelectTool));
            Raise(nameof(IsDrawTool));
            Raise(nameof(IsEraseTool));
            Raise(nameof(IsSplitTool));
        }
    }
    public bool IsSelectTool => ToolMode == TimelineToolMode.Select;
    public bool IsDrawTool => ToolMode == TimelineToolMode.Draw;
    public bool IsEraseTool => ToolMode == TimelineToolMode.Erase;
    public bool IsSplitTool => ToolMode == TimelineToolMode.Split;
    public bool GridVisible
    {
        get => EditorSettings.GridVisible;
        set => EditorSettings.GridVisible = value;
    }
    public string GridLabel => EditorSettings.DisplayGridLabel;
    public TimelineRenderSnapshot? Snapshot
    {
        get => _snapshot;
        private set => Set(ref _snapshot, value);
    }
    public TimelineRenderSnapshot? RulerSnapshot
    {
        get => _rulerSnapshot;
        private set => Set(ref _rulerSnapshot, value);
    }
    public TimelineRenderSnapshot? ParameterSnapshot
    {
        get => _parameterSnapshot;
        private set => Set(ref _parameterSnapshot, value);
    }
    public TimelineRenderSnapshot? VelocitySnapshot
    {
        get => _velocitySnapshot;
        private set => Set(ref _velocitySnapshot, value);
    }
    public ObservableCollection<ParameterLaneOption> ParameterLaneOptions { get; } = [];
    public int ActiveParameterLaneIndex
    {
        get => _activeParameterLaneIndex;
        set => Set(ref _activeParameterLaneIndex, Math.Max(-1, value));
    }
    public void PreferCurrentParameterLaneOnNextRebuild()
    {
        _explicitParameterLaneId = GetActiveParameterLaneOption()?.ParameterId;
        _explicitParameterLaneSelectionRevision = Selection.Revision;
    }
    public ParameterLaneOption? GetActiveParameterLaneOption() =>
        ActiveParameterLaneIndex >= 0 && ActiveParameterLaneIndex < ParameterLaneOptions.Count
            ? ParameterLaneOptions[ActiveParameterLaneIndex]
            : null;
    public void AddDirectMidiLaneTarget(DirectMidiEventLaneTarget target)
    {
        if (_directMidiLaneTargets.Add(target)) EditorState?.RememberExplicitMidiTargets(_directMidiLaneTargets);
    }
    internal void RestoreExplicitMidiLaneTargets(IEnumerable<DirectMidiEventLaneTarget> targets)
    { _directMidiLaneTargets.Clear(); _directMidiLaneTargets.UnionWith(targets); }

    internal int? GetMidiLaneCount(MidoraId owner, long generation, DirectMidiEventLaneTarget target) =>
        _directMidiEventTargetCache.TryGetValue(owner, out var cached) && cached.Generation == generation
            ? cached.Index.Counts.GetValueOrDefault(target) : null;
    internal string? LaneCountError => _laneCountError;
    internal bool IsLaneDirectoryComplete => _midiTargetDiscoverySegment is not { } segment
        || _midiTargetDiscoverySnapshot is not { } snapshot
        || (_directMidiEventTargetCache.TryGetValue(segment.Id, out var cached) && cached.Generation == snapshot.Generation);

    public bool ActiveValueDragEnabled
    {
        get => _activeValueDragEnabled;
        private set => Set(ref _activeValueDragEnabled, value);
    }

    public double ActiveValueMinimum
    {
        get => _activeValueMinimum;
        private set { if (Set(ref _activeValueMinimum, value)) Raise(nameof(ActiveValueAxisMinimum)); }
    }
    public double ActiveValueMaximum
    {
        get => _activeValueMaximum;
        private set { if (Set(ref _activeValueMaximum, value)) Raise(nameof(ActiveValueAxisMaximum)); }
    }
    // The CC editor/tools use the friendly domain. Direct Pitch Bend retains
    // its separate axis-only signed projection; its tool scalar stays 0..16383.
    public double ActiveEditingValueMinimum => ActiveValueMinimum + (GetActiveParameterLaneOption()?.DirectMidiTarget is { } target
        ? MidiEditingValueDomain.Offset(target.Kind, target.Data1) : 0);
    public double ActiveEditingValueMaximum => ActiveValueMaximum + (GetActiveParameterLaneOption()?.DirectMidiTarget is { } target
        ? MidiEditingValueDomain.Offset(target.Kind, target.Data1) : 0);
    public double ActiveValueAxisMinimum => ActiveValueMinimum + _activeValueDisplayOffset;
    public double ActiveValueAxisMaximum => ActiveValueMaximum + _activeValueDisplayOffset;
    private double ActiveValueDisplayOffset
    {
        set
        {
            if (!Set(ref _activeValueDisplayOffset, value)) return;
            Raise(nameof(ActiveValueAxisMinimum));
            Raise(nameof(ActiveValueAxisMaximum));
        }
    }
    public bool ActiveValueIntegral
    {
        get => _activeValueIntegral;
        private set => Set(ref _activeValueIntegral, value);
    }
    public string Context
    {
        get => _context;
        private set => Set(ref _context, value);
    }
    public long StartTick
    {
        get => _startTick;
        set => Set(ref _startTick, Math.Max(0, value));
    }
    public long TickSpan
    {
        get => _tickSpan;
        set => Set(ref _tickSpan, Math.Max(16, value));
    }
    public int FirstLane
    {
        get => _firstLane;
        set => Set(ref _firstLane, Math.Max(0, value));
    }
    public double LaneHeight
    {
        get => _laneHeight;
        set => Set(
            ref _laneHeight,
            Math.Clamp(
                value,
                0.25,
                TimelineSurface.MaximumPianoLaneHeight));
    }
    public long GridStepTicks => EditorSettings.DisplayGridStepTicks;
    public long OperationStepTicks => EditorSettings.EffectiveOperationStepTicks;
    public long ProjectTickOffset
    {
        get => _projectTickOffset;
        private set => Set(ref _projectTickOffset, value);
    }
    public long? RangeStartTick
    {
        get => _rangeStartTick;
        private set => Set(ref _rangeStartTick, value);
    }
    public long? RangeEndTick
    {
        get => _rangeEndTick;
        private set => Set(ref _rangeEndTick, value);
    }
    public long? TimeRangeStartTick
    {
        get => _timeRangeStartTick;
        private set => Set(ref _timeRangeStartTick, value);
    }
    public long? TimeRangeEndTick
    {
        get => _timeRangeEndTick;
        private set => Set(ref _timeRangeEndTick, value);
    }
    public bool HasTimeRange => TimeRangeStartTick.HasValue && TimeRangeEndTick > TimeRangeStartTick;
    public long TimelineExtentEndTick
    {
        get => _timelineExtentEndTick;
        private set => Set(ref _timelineExtentEndTick, Math.Max(1, value));
    }
    public long? EditCursorTick
    {
        get => _editCursorTick;
        set => Set(ref _editCursorTick, value is null ? null : Math.Max(0, value.Value));
    }
    public long? PlaybackCursorTick
    {
        get => _playbackCursorTick;
        private set => Set(ref _playbackCursorTick, value);
    }

    public void SetTrackMonitoringStates(
        IEnumerable<MidoraId> mutedTrackIds,
        IEnumerable<MidoraId> soloTrackIds,
        IEnumerable<MidoraId>? mutedSharedGroupIds = null,
        IEnumerable<MidoraId>? soloSharedGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(mutedTrackIds);
        ArgumentNullException.ThrowIfNull(soloTrackIds);
        _mutedTrackIds.Clear();
        _mutedTrackIds.UnionWith(mutedTrackIds);
        _soloTrackIds.Clear();
        _soloTrackIds.UnionWith(soloTrackIds);
        _mutedSharedGroupIds.Clear();
        if (mutedSharedGroupIds is not null)
            _mutedSharedGroupIds.UnionWith(mutedSharedGroupIds);
        _soloSharedGroupIds.Clear();
        if (soloSharedGroupIds is not null)
            _soloSharedGroupIds.UnionWith(soloSharedGroupIds);
    }

    public ArrangementLaneDescriptor? GetArrangementLane(int lane)
    {
        if (Snapshot is null || (uint)lane >= (uint)Snapshot.ArrangementLanes.Count) return null;
        return Snapshot.ArrangementLanes[lane];
    }

    public void ResetViewport()
    {
        StartTick = 0;
        TickSpan = 3072;
        FirstLane = Mode == TimelineWorkspaceMode.Segment ? 48 : 0;
        LaneHeight = Mode switch
        {
            TimelineWorkspaceMode.Arrangement => 56,
            TimelineWorkspaceMode.Conductor => 27,
            _ => 15
        };
    }

    public void CenterViewportOnTick(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        long halfSpan = TickSpan / 2;
        StartTick = tick > halfSpan ? tick - halfSpan : 0;
    }

    public long ToProjectTick(MidoraProject project, long timelineTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (timelineTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineTick));
        }
        if (Mode != TimelineWorkspaceMode.Segment)
        {
            return timelineTick;
        }
        (long ProjectStartTick, long ContentOffsetTick, long ContentEndTick) segment =
            FindSegment(project, ObjectId) is { } logical
                ? (logical.Segment.ProjectStartTick, logical.Segment.ContentOffsetTick, logical.Segment.ContentEndTick)
                : FindMidiSegment(project, ObjectId) is { } midi
                    ? (midi.Segment.ProjectStartTick, midi.Segment.ContentOffsetTick, midi.Segment.ContentEndTick)
                    : throw new InvalidOperationException("The Segment no longer exists.");
        long localTick = Math.Clamp(
            timelineTick,
            segment.ContentOffsetTick,
            segment.ContentEndTick);
        return checked(segment.ProjectStartTick + (localTick - segment.ContentOffsetTick));
    }

    public TickRange ToProjectRange(MidoraProject project, long timelineStartTick, long timelineEndTick)
    {
        if (timelineEndTick <= timelineStartTick)
        {
            throw new ArgumentOutOfRangeException(nameof(timelineEndTick));
        }
        long startTick = ToProjectTick(project, timelineStartTick);
        long endTick = ToProjectTick(project, timelineEndTick);
        if (endTick <= startTick)
        {
            throw new InvalidOperationException(
                "The selected time range does not intersect the active Segment range.");
        }
        return new(startTick, endTick);
    }

    public void UpdatePlaybackCursor(MidoraProject project, long projectTick)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (Mode != TimelineWorkspaceMode.Segment)
        {
            PlaybackCursorTick = Math.Max(0, projectTick);
            return;
        }
        (long ProjectStartTick, long LengthTicks, long ContentOffsetTick)? located =
            FindSegment(project, ObjectId) is { } logical
                ? (logical.Segment.ProjectStartTick, logical.Segment.LengthTicks, logical.Segment.ContentOffsetTick)
                : FindMidiSegment(project, ObjectId) is { } midi
                    ? (midi.Segment.ProjectStartTick, midi.Segment.LengthTicks, midi.Segment.ContentOffsetTick)
                    : null;
        if (located is null)
        {
            PlaybackCursorTick = null;
            return;
        }
        long projectEndTick = checked(located.Value.ProjectStartTick + located.Value.LengthTicks);
        PlaybackCursorTick = projectTick < located.Value.ProjectStartTick || projectTick > projectEndTick
            ? null
            : checked(located.Value.ContentOffsetTick + (projectTick - located.Value.ProjectStartTick));
    }

    public void SetTimeRange(long startTick, long endTick)
    {
        if (startTick < 0 || endTick <= startTick)
        {
            throw new ArgumentOutOfRangeException(nameof(endTick));
        }
        TimeRangeStartTick = startTick;
        TimeRangeEndTick = endTick;
        Raise(nameof(HasTimeRange));
    }

    public void ClearTimeRange()
    {
        TimeRangeStartTick = null;
        TimeRangeEndTick = null;
        Raise(nameof(HasTimeRange));
    }

    public bool SetTimeRangeFromObjectSelection()
    {
        if (Snapshot is null || Selection.Ids.Count == 0) return false;
        List<TimelineRenderItem> selected = new(Selection.Ids.Count);
        foreach (MidoraId id in Selection.Ids)
        {
            if (Snapshot.TryGetItem(id, out TimelineRenderItem item)
                && (item.State & TimelineItemState.HitTestDisabled) == 0)
            {
                selected.Add(item);
            }
        }
        if (selected.Count == 0) return false;
        SetTimeRange(selected.Min(item => item.StartTick), selected.Max(item => item.EndTick));
        return true;
    }

    public bool SetObjectSelectionFromTimeRange() =>
        SetObjectSelectionFromTimeRange(Snapshot, null);

    public bool SetObjectSelectionFromTimeRange(
        TimelineRenderSnapshot? sourceSnapshot,
        WorkspaceTimelineSelectionSource? source)
    {
        if (sourceSnapshot is null
            || TimeRangeStartTick is not long start
            || TimeRangeEndTick is not long end)
        {
            return false;
        }
        List<TimelineRenderItem> candidates = [];
        int laneCount = Math.Max(1, sourceSnapshot.LaneLabels.Count);
        sourceSnapshot.QueryInto(start, end, 0, laneCount, candidates);
        MidoraId[] ids = candidates
            .Where(item => (item.State & TimelineItemState.HitTestDisabled) == 0)
            .Select(item => item.Id)
            .Distinct()
            .ToArray();
        if (source is WorkspaceTimelineSelectionSource timelineSource)
        {
            Selection.ApplyRange(
                ids,
                WorkspaceSelectionRangeMode.Replace,
                timelineSource);
        }
        else
        {
            Selection.ApplyRange(ids, WorkspaceSelectionRangeMode.Replace);
        }
        return ids.Length != 0;
    }

    public override void Rebuild(MidoraProject project, long revision)
    {
        using var editorStateUpdate = EditorState?.BeginRebuild();
        BeginPresentationRebuild();
        _midiTargetDiscoveryCancellation.Cancel();
        _midiTargetDiscoveryCancellation.Dispose();
        _midiTargetDiscoveryCancellation = new();
        _midiTargetDiscoveryGeneration = checked(_midiTargetDiscoveryGeneration + 1);
        _laneCountError = null;
        _midiTargetDiscoverySegment = null;
        _midiTargetDiscoverySnapshot = null;
        ProjectTickOffset = Mode == TimelineWorkspaceMode.Segment
            ? FindSegment(project, ObjectId) is { } logical
                ? checked(logical.Segment.ProjectStartTick - logical.Segment.ContentOffsetTick)
                : FindMidiSegment(project, ObjectId) is { } midi
                    ? checked(midi.Segment.ProjectStartTick - midi.Segment.ContentOffsetTick)
                    : 0
            : 0;
        HasInstrumentChangesLane = IsSegment && FindMidiSegment(project, ObjectId) is not null;
        if (!_viewportInitialized)
        {
            TickSpan = Math.Max(TickSpan, PianoEditorDefaults.SegmentTickSpan(project.TicksPerQuarterNote));
            _viewportInitialized = true;
        }
        if (IsSegment)
        {
            MidoraId? trackId = FindSegment(project, ObjectId)?.Track.Id ?? FindMidiSegment(project, ObjectId)?.Track.Id;
            if (trackId is { } owner) EditorState?.PrepareSegment(project, owner);
        }
        long referenceTick = ProjectTickOffset > 0 && StartTick > long.MaxValue - ProjectTickOffset
            ? long.MaxValue : Math.Max(0, StartTick + ProjectTickOffset);
        EditorSettings.ConfigureProject(project, referenceTick);
        LaneEditorSettings.ConfigureProject(project, referenceTick);
        switch (Mode)
        {
            case TimelineWorkspaceMode.Arrangement:
                RebuildArrangement(project, revision);
                break;
            case TimelineWorkspaceMode.Segment:
                RebuildSegment(project, revision);
                ObjectList.SetFactory(() => FindSegment(project, ObjectId) is { } logical
                    ? TimelineObjectListSource.CreateLogical(project, logical.Segment)
                    : FindMidiSegment(project, ObjectId) is { } midi
                        ? TimelineObjectListSource.CreateMidi(project, midi.Segment) : null);
                break;
            case TimelineWorkspaceMode.Conductor:
                RebuildConductor(project, revision);
                break;
            default:
                throw new InvalidOperationException("Unknown timeline workspace mode.");
        }
        long contentEnd = Math.Max(
            Math.Max(Snapshot?.MaximumEndTick ?? 0, ConductorTempoSnapshot?.MaximumEndTick ?? 0),
            RangeEndTick ?? 0);
        long minimumExtent = StartTick <= long.MaxValue - TickSpan
            ? StartTick + TickSpan
            : long.MaxValue;
        long padding = Math.Max(1, checked((long)project.TicksPerQuarterNote * 4));
        long paddedContent = contentEnd <= long.MaxValue - padding ? contentEnd + padding : long.MaxValue;
        TimelineExtentEndTick = Math.Max(minimumExtent, paddedContent);
        Raise(nameof(GridStepTicks));
        Raise(nameof(OperationStepTicks));
        Raise(nameof(GridLabel));
    }

    private void RebuildArrangement(MidoraProject project, long revision)
    {
        PruneSelection(id =>
            ProjectSegmentIndex.FindLogical(project, id) is not null
            || ProjectSegmentIndex.FindMidi(project, id) is not null);
        RangeStartTick = null;
        RangeEndTick = null;
        List<TimelineRenderItem> items = [];
        Dictionary<MidoraId, TimelineSegmentPreview> previews = [];
        HashSet<MidoraId> liveSegmentIds = [];
        List<string> labels = [];
        List<string> secondaryLabels = [];
        List<TimelineLaneState> laneStates = [];
        List<uint> laneColors = [];
        List<ArrangementLaneDescriptor> lanes = [];

        void AddLane(
            ArrangementLaneKind kind,
            MidoraId? objectId,
            MidoraId? ownerId,
            string label,
            string secondary,
            uint color,
            TimelineLaneState state,
            bool canContainSegments,
            MidoraId? sharedGroupId = null,
            bool isSharedGroup = false,
            bool isSharedGroupStart = false,
            bool isSharedGroupEnd = false,
            int sharedGroupMemberCount = 0)
        {
            int lane = labels.Count;
            labels.Add(label);
            secondaryLabels.Add(secondary);
            laneColors.Add(color);
            laneStates.Add(state);
            lanes.Add(new(
                lane,
                kind,
                objectId,
                ownerId,
                0,
                true,
                false,
                canContainSegments)
            {
                SharedGroupId = sharedGroupId,
                IsSharedGroup = isSharedGroup,
                IsSharedGroupStart = isSharedGroupStart,
                IsSharedGroupEnd = isSharedGroupEnd,
                SharedGroupMemberCount = sharedGroupMemberCount
            });
        }

        AddLane(
            ArrangementLaneKind.Conductor,
            null,
            null,
            "Conductor",
            string.Empty,
            0,
            TimelineLaneState.None,
            false);
        ConductorTimelineProjection conductor = new(project.Conductor, revision, 0, 240);
        if (project.Conductor.EndMarker is { } conductorEnd)
            items.Add(new(conductorEnd.Id, TimelineItemKind.ProjectEndMarker, conductorEnd.Tick,
                conductorEnd.Tick == long.MaxValue ? long.MaxValue : conductorEnd.Tick + 1,
                0, 0, 4, TimelineItemState.HitTestDisabled) { Label = "END" });

        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, EventInstrumentUsage> usages = project.EventInstrumentUsages
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, MidiChannelRoot> roots = project.MidiChannelRoots
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, LogicalTrack> logicalTracks = project.Tracks
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, PureMidiTrack> midiTracks = project.PureMidiTracks
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, DamagedProjectObject> damagedInstruments = project.DamagedEventInstruments
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, DamagedProjectObject> damagedRoots = project.DamagedMidiChannelRoots
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, DamagedProjectObject> damagedLogicalTracks = project.DamagedLogicalTracks
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<MidoraId, DamagedProjectObject> damagedMidiTracks = project.DamagedPureMidiTracks
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.First());

        Dictionary<MidoraId, int> usageMemberCounts = project.Tracks
            .Where(value => value.EventInstrumentUsageId.HasValue)
            .GroupBy(value => value.EventInstrumentUsageId!.Value)
            .ToDictionary(value => value.Key, value => value.Count());
        Dictionary<MidoraId, int> rootMemberCounts = project.PureMidiTracks
            .GroupBy(value => value.MidiChannelRootId)
            .ToDictionary(value => value.Key, value => value.Count());
        ArrangementTrackReference[] order = project.TracksInArrangementOrder().ToArray();

        (MidoraId? Id, bool Shared, int Count) GroupInfo(ArrangementTrackReference reference)
        {
            if (reference.Kind == ArrangementTrackKind.LogicalTrack
                && logicalTracks.TryGetValue(reference.TrackId, out LogicalTrack? logical)
                && logical.EventInstrumentUsageId is MidoraId usageId)
            {
                int count = usageMemberCounts.GetValueOrDefault(usageId);
                return (usageId, count > 1, count);
            }
            if (reference.Kind == ArrangementTrackKind.PureMidiTrack
                && midiTracks.TryGetValue(reference.TrackId, out PureMidiTrack? midi)
                && roots.TryGetValue(midi.MidiChannelRootId, out MidiChannelRoot? root))
            {
                int count = rootMemberCounts.GetValueOrDefault(root.Id);
                return (root.Id, root.RoutingMode == MidiChannelRootRoutingMode.Auto && count > 1, count);
            }
            return (null, false, 0);
        }

        for (int orderIndex = 0; orderIndex < order.Length; orderIndex++)
        {
            ArrangementTrackReference reference = order[orderIndex];
            (MidoraId? groupId, bool shared, int groupCount) = GroupInfo(reference);
            bool groupStart = shared && (orderIndex == 0 || GroupInfo(order[orderIndex - 1]).Id != groupId);
            bool groupEnd = shared && (orderIndex == order.Length - 1 || GroupInfo(order[orderIndex + 1]).Id != groupId);

            if (reference.Kind == ArrangementTrackKind.LogicalTrack
                && logicalTracks.TryGetValue(reference.TrackId, out LogicalTrack? track))
            {
                EventInstrumentUsage? usage = track.EventInstrumentUsageId is MidoraId usageId
                    && usages.TryGetValue(usageId, out EventInstrumentUsage? foundUsage)
                        ? foundUsage
                        : null;
                EventInstrument? instrument = usage is not null
                    && instruments.TryGetValue(usage.EventInstrumentId, out EventInstrument? foundInstrument)
                        ? foundInstrument
                        : project.FindEventInstrumentDefinition(track);
                uint accentColor = ToOpaqueArgb(
                    ProjectTrackColorPolicy.ResolveDisplayColor(project, track));
                string definitionName = instrument is null
                    ? "Unbound"
                    : string.IsNullOrWhiteSpace(instrument.Name)
                        ? "Unnamed Event Instrument"
                        : instrument.Name;
                string secondary = instrument is null
                    ? "Unbound · Empty track only"
                    : definitionName;
                int lane = labels.Count;
                AddLane(
                    ArrangementLaneKind.LogicalTrack,
                    track.Id,
                    instrument?.Id,
                    string.IsNullOrWhiteSpace(track.Name) ? "Logical Track" : track.Name,
                    secondary,
                    accentColor,
                    TrackMonitoringState(track.Id),
                    true,
                    groupId,
                    shared,
                    groupStart,
                    groupEnd,
                    groupCount);
                foreach (Segment segment in track.Segments)
                {
                    liveSegmentIds.Add(segment.Id);
                    items.Add(Item(
                        segment.Id,
                        TimelineItemKind.Segment,
                        segment.ProjectStartTick,
                        checked(segment.ProjectStartTick + segment.LengthTicks),
                        lane,
                        z: 0) with
                    { AccentColor = accentColor });
                    previews[segment.Id] = GetOrCreateSegmentPreview(segment, instrument);
                }
                continue;
            }

            if (reference.Kind == ArrangementTrackKind.PureMidiTrack
                && midiTracks.TryGetValue(reference.TrackId, out PureMidiTrack? midiTrack))
            {
                roots.TryGetValue(midiTrack.MidiChannelRootId, out MidiChannelRoot? root);
                uint accentColor = ToOpaqueArgb(
                    ProjectTrackColorPolicy.ResolveDisplayColor(midiTrack));
                string route = root is null
                    ? "Missing MIDI Channel Root"
                    : root.RoutingMode == MidiChannelRootRoutingMode.Auto
                        ? $"Auto {root.ChannelMode}"
                        : $"P.{root.FixedZeroBasedPort + 1} Ch.{root.FixedZeroBasedChannel + 1} {root.ChannelMode}";
                string secondary = route;
                int lane = labels.Count;
                AddLane(
                    ArrangementLaneKind.PureMidiTrack,
                    midiTrack.Id,
                    root?.Id,
                    string.IsNullOrWhiteSpace(midiTrack.Name) ? "Unnamed MIDI Track" : midiTrack.Name,
                    secondary,
                    accentColor,
                    TrackMonitoringState(midiTrack.Id),
                    true,
                    groupId,
                    shared,
                    groupStart,
                    groupEnd,
                    groupCount);
                foreach (MidiSegment segment in midiTrack.Segments)
                {
                    liveSegmentIds.Add(segment.Id);
                    items.Add(Item(
                        segment.Id,
                        TimelineItemKind.Segment,
                        segment.ProjectStartTick,
                        checked(segment.ProjectStartTick + segment.LengthTicks),
                        lane,
                        z: 0) with
                    { AccentColor = accentColor });
                    previews[segment.Id] = GetOrCreateSegmentPreview(segment);
                }
                continue;
            }

            DamagedProjectObject? damaged = reference.Kind == ArrangementTrackKind.LogicalTrack
                ? damagedLogicalTracks.GetValueOrDefault(reference.TrackId)
                : damagedMidiTracks.GetValueOrDefault(reference.TrackId);
            if (damaged is not null)
            {
                AddLane(
                    reference.Kind == ArrangementTrackKind.LogicalTrack
                        ? ArrangementLaneKind.DamagedLogicalTrack
                        : ArrangementLaneKind.DamagedPureMidiTrack,
                    damaged.Id,
                    null,
                    $"[Damaged] {DisplayDamagedName(damaged)}",
                    damaged.Error,
                    0xffe9414d,
                    TimelineLaneState.None,
                    false);
            }
        }

        SynchronizeEventInstrumentBrowser(project);
        foreach (MidoraId staleId in _segmentPreviewCache.Keys.Where(id => !liveSegmentIds.Contains(id)).ToArray())
        {
            _segmentPreviewCache.Remove(staleId);
        }
        if (SelectedArrangementTrackId is MidoraId selectedTrackId
            && !lanes.Any(value => value.ObjectId == selectedTrackId
                && value.Kind is ArrangementLaneKind.LogicalTrack
                    or ArrangementLaneKind.PureMidiTrack
                    or ArrangementLaneKind.DamagedLogicalTrack
                    or ArrangementLaneKind.DamagedPureMidiTrack))
        {
            SelectedArrangementTrackId = null;
        }
        Context = $"{order.Length} tracks · {items.Count(item => item.Kind == TimelineItemKind.Segment)} segments";
        Snapshot = new(
            revision,
            "arrangement",
            items,
            labels,
            laneStates,
            previews,
            secondaryLabels,
            laneColors,
            lanes,
            conductorPreviewSource: conductor.ArrangementSource);
        RulerSnapshot = conductor.RulerSnapshot;

        TimelineLaneState TrackMonitoringState(MidoraId trackId) =>
            (_mutedTrackIds.Contains(trackId) ? TimelineLaneState.Muted : TimelineLaneState.None)
            | (_soloTrackIds.Contains(trackId) ? TimelineLaneState.Solo : TimelineLaneState.None);
    }

    private TimelineSegmentPreview GetOrCreateSegmentPreview(
        Segment segment,
        EventInstrument? instrument)
    {
        Dictionary<MidoraId, LogicalParameterDefinition> definitions = instrument?.LogicalParameters
            .ToDictionary(static value => value.Id)
            ?? [];
        ulong parameterFingerprint = 0;
        foreach (LogicalParameterLane lane in segment.ParameterLanes)
        {
            LogicalParameterDefinition? definition = definitions.GetValueOrDefault(lane.ParameterId);
            (double minimum, double maximum) = ParameterDisplayRange(definition);
            parameterFingerprint = TimelineContentFingerprint.Combine(
                parameterFingerprint,
                TimelineContentFingerprint.Combine(
                    unchecked((ulong)lane.Id.Value),
                    TimelineContentFingerprint.Combine(
                        unchecked((ulong)lane.ParameterId.Value),
                        TimelineContentFingerprint.Combine(
                            unchecked((ulong)lane.Points.Generation),
                            TimelineContentFingerprint.Combine(
                                unchecked((ulong)BitConverter.DoubleToInt64Bits(minimum)),
                                unchecked((ulong)BitConverter.DoubleToInt64Bits(maximum)))))));
        }
        if (_segmentPreviewCache.TryGetValue(segment.Id, out SegmentPreviewCacheEntry? cached)
            && cached.ContentOffsetTick == segment.ContentOffsetTick
            && cached.LengthTicks == segment.LengthTicks
            && cached.NoteGeneration == segment.Notes.Generation
            && cached.ParameterFingerprint == parameterFingerprint)
        {
            return cached.Preview;
        }

        LogicalNoteQuerySnapshot noteSnapshot = segment.Notes.CreateQuerySnapshot();
        LogicalSegmentPreviewEventLane[] eventLanes = segment.ParameterLanes
            .Select(lane =>
            {
                (double minimum, double maximum) = ParameterDisplayRange(
                    definitions.GetValueOrDefault(lane.ParameterId));
                return new LogicalSegmentPreviewEventLane(
                    lane.Points.CreateQuerySnapshot(),
                    minimum,
                    maximum);
            })
            .ToArray();
        LogicalSegmentPreviewSource source = new(
            segment,
            noteSnapshot,
            eventLanes);
        TimelineSegmentPreview preview;
        long eventCount = eventLanes.Sum(static lane => (long)lane.Snapshot.Count);
        if (noteSnapshot.Count + eventCount <= MaterializedSegmentPreviewThreshold)
        {
            List<TimelineSegmentPreviewNote> notes = [];
            List<TimelineSegmentPreviewEvent> events = [];
            source.QueryNotes(0, Math.BitIncrement(1d), notes);
            source.QueryEvents(0, Math.BitIncrement(1d), events);
            preview = new(segment.Id, notes, events);
        }
        else
        {
            preview = new(segment.Id, source);
        }
        _segmentPreviewCache[segment.Id] = new(
            segment.ContentOffsetTick,
            segment.LengthTicks,
            [],
            [],
            preview,
            NoteGeneration: segment.Notes.Generation,
            ParameterFingerprint: parameterFingerprint);
        return preview;

        static (double Minimum, double Maximum) ParameterDisplayRange(
            LogicalParameterDefinition? definition)
        {
            if (definition is null) return (0, 0);
            double minimum = definition.DisplayMinimum;
            double maximum = definition.DisplayMaximum;
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
            {
                minimum = definition.Minimum;
                maximum = definition.Maximum;
            }
            return maximum > minimum ? (minimum, maximum) : (0, 0);
        }
    }

    private void SynchronizeEventInstrumentBrowser(MidoraProject project)
    {
        Dictionary<MidoraId, MidoraId> definitionByUsage = project.EventInstrumentUsages
            .ToDictionary(static value => value.Id, static value => value.EventInstrumentId);
        Dictionary<MidoraId, int> usageCountByDefinition = project.EventInstrumentUsages
            .GroupBy(static value => value.EventInstrumentId)
            .ToDictionary(static group => group.Key, static group => group.Count());
        Dictionary<MidoraId, int> trackCountByDefinition = [];
        foreach (LogicalTrack track in project.Tracks)
        {
            if (track.EventInstrumentUsageId is not MidoraId usageId
                || !definitionByUsage.TryGetValue(usageId, out MidoraId definitionId))
            {
                continue;
            }
            trackCountByDefinition[definitionId] =
                trackCountByDefinition.GetValueOrDefault(definitionId) + 1;
        }

        EventInstrumentBrowserRow[] rows = project.EventInstruments
            .Select(instrument => new EventInstrumentBrowserRow(
                instrument.Id,
                string.IsNullOrWhiteSpace(instrument.Name)
                    ? "Unnamed Event Instrument"
                    : instrument.Name,
                usageCountByDefinition.GetValueOrDefault(instrument.Id),
                trackCountByDefinition.GetValueOrDefault(instrument.Id),
                ToOpaqueArgb(instrument.Color)))
            .ToArray();
        if (EventInstrumentBrowser.SequenceEqual(rows)) return;

        // The browser is structural UI. Content-only edits must not emit a
        // Clear/Add notification storm that recreates every WPF container.
        EventInstrumentBrowser.Clear();
        foreach (EventInstrumentBrowserRow row in rows) EventInstrumentBrowser.Add(row);
    }

    private TimelineSegmentPreview GetOrCreateSegmentPreview(MidiSegment segment)
    {
        string fingerprint = segment.PagedContentFingerprint ?? string.Empty;
        if (_segmentPreviewCache.TryGetValue(segment.Id, out SegmentPreviewCacheEntry? pagedCached)
            && pagedCached.ContentOffsetTick == segment.ContentOffsetTick
            && pagedCached.LengthTicks == segment.LengthTicks
            && string.Equals(pagedCached.PagedContentFingerprint, fingerprint, StringComparison.Ordinal)
            && pagedCached.NoteGeneration == segment.Notes.Generation
            && pagedCached.ChannelEventGeneration == segment.ChannelEvents.Generation
            && pagedCached.OpaqueEventGeneration == segment.OpaqueEvents.Generation)
        {
            return pagedCached.Preview;
        }

        PagedMidiSegmentPreviewSource source = new(segment);
        TimelineSegmentPreview preview;
        long previewItemCount = (long)segment.Notes.Count
            + segment.ChannelEvents.Count
            + segment.OpaqueEvents.Count;
        if (previewItemCount <= MaterializedSegmentPreviewThreshold)
        {
            List<TimelineSegmentPreviewNote> notes = [];
            List<TimelineSegmentPreviewEvent> events = [];
            source.QueryNotes(0, Math.BitIncrement(1d), notes);
            source.QueryEvents(0, Math.BitIncrement(1d), events);
            preview = new(segment.Id, notes, events);
        }
        else
        {
            preview = new(segment.Id, source);
        }
        _segmentPreviewCache[segment.Id] = new(
            segment.ContentOffsetTick,
            segment.LengthTicks,
            [],
            [],
            preview,
            fingerprint,
            segment.Notes.Generation,
            segment.ChannelEvents.Generation,
            segment.OpaqueEvents.Generation);
        return preview;
    }

    private static double NormalizeDirectEventPreviewValue(SegmentPreviewEventSource value) =>
        double.IsFinite(value.NormalizedValue)
            ? Math.Clamp(value.NormalizedValue, 0, 1)
            : value.Kind switch
        {
            (int)DirectMidiChannelEventKind.PolyphonicKeyPressure => Math.Clamp(value.Data2 / 127d, 0, 1),
            (int)DirectMidiChannelEventKind.ControlChange => Math.Clamp(value.Data2 / 127d, 0, 1),
            (int)DirectMidiChannelEventKind.ProgramChange => Math.Clamp(value.Data1 / 127d, 0, 1),
            (int)DirectMidiChannelEventKind.ChannelPressure => Math.Clamp(value.Data1 / 127d, 0, 1),
            (int)DirectMidiChannelEventKind.PitchBend => Math.Clamp(((value.Data2 << 7) | value.Data1) / 16383d, 0, 1),
            _ => 1
        };

    private readonly record struct SegmentPreviewNoteSource(long StartTick, long LengthTicks, int Pitch);
    private readonly record struct SegmentPreviewEventSource(
        long Tick,
        int Kind,
        int Data1,
        int Data2,
        long Order,
        double NormalizedValue);

    private static string DisplayDamagedName(DamagedProjectObject value) =>
        string.IsNullOrWhiteSpace(value.NameSnapshot)
            ? "Unnamed damaged object"
            : value.NameSnapshot;

    private static uint ToOpaqueArgb(MidoraColor color) =>
        0xff000000u | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;

    private sealed record SegmentPreviewCacheEntry(
        long ContentOffsetTick,
        long LengthTicks,
        SegmentPreviewNoteSource[] Source,
        SegmentPreviewEventSource[] EventSource,
        TimelineSegmentPreview Preview,
        string? PagedContentFingerprint = null,
        long NoteGeneration = 0,
        long ChannelEventGeneration = 0,
        long OpaqueEventGeneration = 0,
        ulong ParameterFingerprint = 0);

    private void RebuildSegment(MidoraProject project, long revision)
    {
        RulerSnapshot = null;
        (LogicalTrack Track, Segment Segment)? located = FindSegment(project, ObjectId);
        if (located is null)
        {
            if (FindMidiSegment(project, ObjectId) is { } midi)
            {
                RebuildMidiSegment(project, revision, midi.Track, midi.Segment);
                return;
            }
            SetMissingSegmentSnapshots(revision);
            return;
        }
        LogicalTrack track = located.Value.Track;
        Segment segment = located.Value.Segment;
        TabIconKind = WorkspaceTabIconKind.LogicalTrack;
        PruneSelection(id =>
            segment.Notes.TryGetById(id, out _)
            || segment.ParameterLanes.Any(lane =>
                lane.Id == id || lane.Points.TryGetById(id, out _)));
        RangeStartTick = segment.ContentOffsetTick;
        RangeEndTick = segment.ContentEndTick;
        Header = $"Segment: {TrackDisplayName(project, track)} @ {segment.ProjectStartTick}";
        Context = $"Segment local ticks · Project start {segment.ProjectStartTick} · active {segment.ContentOffsetTick}–{segment.ContentEndTick}";
        IReadOnlySet<MidoraId> selectedIds = Selection.IdSet;
        LogicalNoteQuerySnapshot noteSnapshot = segment.Notes.CreateQuerySnapshot();
        PagedLogicalNoteTimelineItemSource noteSource = new(
            segment,
            LogicalNoteTimelineProjection.Notes,
            selectedIds,
            Selection.Primary,
            noteSnapshot);
        (TimelineRenderItem[] noteItems, ITimelineRenderItemSource? retainedNoteSource) =
            TimelinePresentationPaging.Adapt(noteSource);
        PagedLogicalNoteTimelineItemSource velocitySource = new(
            segment,
            LogicalNoteTimelineProjection.Velocities,
            selectedIds,
            Selection.Primary,
            noteSnapshot);
        (TimelineRenderItem[] velocityItems, ITimelineRenderItemSource? retainedVelocitySource) =
            TimelinePresentationPaging.Adapt(velocitySource);
        Snapshot = new(
            revision,
            $"segment:{segment.Id.Value}",
            noteItems,
            Enumerable.Range(0, 128).Select(lane => MidiNoteName(127 - lane)).ToArray(),
            itemSource: retainedNoteSource,
            overviewSource: new LogicalSegmentOverviewSource(
                noteSnapshot,
                segment.ParameterLanes.Select(static lane => lane.Points.CreateQuerySnapshot())));
        VelocitySnapshot = new(
            revision,
            $"segment-velocities:{segment.Id.Value}",
            velocityItems,
            ["Velocity"],
            itemSource: retainedVelocitySource);

        EventInstrument? instrument = project.FindEventInstrumentDefinition(track);
        List<TimelineRenderItem> parameterItems = [];
        List<string> parameterLabels = [];
        TimelineStepSignalSource? stepSignal = null;
        ITimelineRenderItemSource? retainedParameterSource = null;
        MidoraId? previousParameterId = GetActiveParameterLaneOption()?.ParameterId;
        ParameterLaneOptions.Clear();
        if (instrument is not null)
        {
            foreach (LogicalParameterDefinition definition in instrument.LogicalParameters)
            {
                LogicalParameterLane? lane = segment.ParameterLanes.FirstOrDefault(
                    item => item.ParameterId == definition.Id);
                ParameterLaneOptions.Add(new(
                    definition.Id,
                    lane?.Id,
                    lane is null ? $"{definition.Name} · Empty" : definition.Name));
            }
        }
        foreach (LogicalParameterLane lane in segment.ParameterLanes.Where(lane =>
            instrument?.LogicalParameters.Any(definition => definition.Id == lane.ParameterId) != true))
        {
            ParameterLaneOptions.Add(new(
                lane.ParameterId,
                lane.Id,
                "Unavailable Logical Parameter",
                IsBroken: true));
        }
        MidoraId? selectedParameterId = Selection.Primary is MidoraId selected
            ? segment.ParameterLanes.FirstOrDefault(lane =>
                lane.Id == selected || lane.Points.TryGetById(selected, out _))?.ParameterId
            : null;
        if (_explicitParameterLaneSelectionRevision != Selection.Revision)
        {
            _explicitParameterLaneId = null;
            _explicitParameterLaneSelectionRevision = -1;
        }
        MidoraId? preferredParameterId = _explicitParameterLaneId
            ?? previousParameterId
            ?? selectedParameterId;
        int preferredIndex = preferredParameterId is MidoraId parameterId
            ? ParameterLaneOptions.ToList().FindIndex(option => option.ParameterId == parameterId)
            : -1;
        if (_explicitParameterLaneId is not null && preferredIndex < 0)
        {
            _explicitParameterLaneId = null;
            _explicitParameterLaneSelectionRevision = -1;
            preferredParameterId = selectedParameterId ?? previousParameterId;
            preferredIndex = preferredParameterId is MidoraId fallbackParameterId
                ? ParameterLaneOptions.ToList().FindIndex(option =>
                    option.ParameterId == fallbackParameterId)
                : -1;
        }
        ActiveParameterLaneIndex = ParameterLaneOptions.Count == 0
            ? -1
            : preferredIndex >= 0
                ? preferredIndex
                : Math.Clamp(ActiveParameterLaneIndex, 0, ParameterLaneOptions.Count - 1);
        ActiveValueMinimum = 0;
        ActiveValueMaximum = 127;
        ActiveValueIntegral = true;
        ActiveValueDisplayOffset = 0;
        ParameterLaneOption? activeOption = GetActiveParameterLaneOption();
        ActiveValueDragEnabled = true;
        if (activeOption is not null)
        {
            LogicalParameterDefinition? definition = instrument?.LogicalParameters
                .FirstOrDefault(item => item.Id == activeOption.ParameterId);
            if (definition is not null)
            {
                double minimum = definition.DisplayMinimum;
                double maximum = definition.DisplayMaximum;
                if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
                {
                    minimum = definition.Minimum;
                    maximum = definition.Maximum;
                }
                ActiveValueMinimum = minimum;
                ActiveValueMaximum = maximum;
                ActiveValueIntegral = definition.Type is LogicalParameterType.Integer or LogicalParameterType.Enum;
                ActiveValueDragEnabled = definition.Type != LogicalParameterType.Enum;
            }
            parameterLabels.Add(activeOption.Label);
            LogicalParameterLane? lane = activeOption.LaneId is MidoraId laneId
                ? segment.ParameterLanes.FirstOrDefault(item => item.Id == laneId)
                : null;
            if (lane is not null)
            {
                double minimum = definition?.DisplayMinimum ?? 0;
                double maximum = definition?.DisplayMaximum ?? 1;
                if (definition is not null
                    && (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum))
                {
                    minimum = definition.Minimum;
                    maximum = definition.Maximum;
                }
                CurvePointQuerySnapshot pointSnapshot = lane.Points.CreateQuerySnapshot();
                if (definition is not null)
                    stepSignal = EventStepSignalSources.Logical(lane.Id, pointSnapshot, minimum, maximum);
                PagedLogicalParameterTimelineItemSource parameterSource = new(
                    lane,
                    pointSnapshot,
                    minimum,
                    maximum,
                    definition is null,
                    segment.ContentOffsetTick,
                    segment.ContentEndTick,
                    selectedIds,
                    Selection.Primary);
                (TimelineRenderItem[] adaptedItems, retainedParameterSource) =
                    TimelinePresentationPaging.Adapt(parameterSource);
                parameterItems.AddRange(adaptedItems);
            }
        }
        ParameterSnapshot = new(
            revision,
            $"segment-parameters:{segment.Id.Value}",
            parameterItems,
            parameterLabels,
            itemSource: retainedParameterSource, stepSignalSource: stepSignal);
    }

    private void RebuildMidiSegment(
        MidoraProject project,
        long revision,
        PureMidiTrack track,
        MidiSegment segment)
    {
        RulerSnapshot = null;
        RangeStartTick = segment.ContentOffsetTick;
        RangeEndTick = segment.ContentEndTick;
        TabIconKind = WorkspaceTabIconKind.PureMidiTrack;
        Header = $"MIDI Segment: {(string.IsNullOrWhiteSpace(track.Name) ? "Unnamed MIDI Track" : track.Name)} @ {segment.ProjectStartTick}";
        Context = $"Direct MIDI · Project start {segment.ProjectStartTick} · active {segment.ContentOffsetTick}–{segment.ContentEndTick}";
        IReadOnlySet<MidoraId> selectedIds = Selection.IdSet;
        DirectMidiNoteQuerySnapshot noteSnapshot = segment.Notes.CreateQuerySnapshot();
        DirectMidiChannelEventQuerySnapshot channelEventSnapshot =
            segment.ChannelEvents.CreateQuerySnapshot();
        OpaqueMidiEventQuerySnapshot opaqueEventSnapshot =
            segment.OpaqueEvents.CreateQuerySnapshot();
        PagedDirectMidiTimelineItemSource noteSource = new(
            segment,
            DirectMidiTimelineProjection.Notes,
            selectedIds: selectedIds,
            primaryId: Selection.Primary,
            noteSnapshot: noteSnapshot);
        (TimelineRenderItem[] noteItems, ITimelineRenderItemSource? retainedNoteSource) =
            TimelinePresentationPaging.Adapt(noteSource);
        PagedDirectMidiTimelineItemSource velocitySource = new(
            segment,
            DirectMidiTimelineProjection.Velocities,
            selectedIds: selectedIds,
            primaryId: Selection.Primary,
            noteSnapshot: noteSnapshot);
        (TimelineRenderItem[] velocityItems, ITimelineRenderItemSource? retainedVelocitySource) =
            TimelinePresentationPaging.Adapt(velocitySource);
        Snapshot = new(
            revision,
            $"midi-segment:{segment.Id.Value}",
            noteItems,
            Enumerable.Range(0, 128).Select(lane => MidiNoteName(127 - lane)).ToArray(),
            itemSource: retainedNoteSource,
            overviewSource: new PureMidiSegmentOverviewSource(segment));
        VelocitySnapshot = new(
            revision,
            $"midi-segment-velocities:{segment.Id.Value}",
            velocityItems,
            ["Velocity"],
            itemSource: retainedVelocitySource);

        ParameterLaneOption? previousOption = GetActiveParameterLaneOption();
        DirectMidiEventLaneTarget? previousTarget = previousOption?.DirectMidiTarget;
        bool previousOpaque = previousOption?.IsOpaqueMidiLane == true;
        ParameterLaneOptions.Clear();
        DirectMidiEventLaneTarget[] discoveredTargets =
            _directMidiEventTargetCache.TryGetValue(
                segment.Id,
                out DirectMidiEventTargetCacheEntry? targetCache)
            && targetCache.Generation == channelEventSnapshot.Generation
                ? targetCache.Index.Targets.ToArray()
                : [];
        DirectMidiEventLaneTarget[] targets = discoveredTargets
            .Concat(_directMidiLaneTargets)
            .Concat(previousTarget is DirectMidiEventLaneTarget retained
                ? [retained]
                : [])
            .Distinct()
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.Data1)
            .ToArray();
        foreach (DirectMidiEventLaneTarget target in targets)
        {
            ParameterLaneOptions.Add(new(
                default,
                null,
                DirectMidiLaneLabel(target),
                DirectMidiTarget: target));
        }
        if (segment.OpaqueEvents.Count != 0)
        {
            ParameterLaneOptions.Add(new(
                default,
                null,
                "Imported Meta / SysEx",
                IsOpaqueMidiLane: true));
        }
        int preferredIndex = previousTarget is DirectMidiEventLaneTarget selectedTarget
            ? targets.ToList().IndexOf(selectedTarget)
            : previousOpaque && segment.OpaqueEvents.Count != 0
                ? ParameterLaneOptions.Count - 1
                : -1;
        ActiveParameterLaneIndex = ParameterLaneOptions.Count == 0
            ? -1
            : preferredIndex >= 0
                ? preferredIndex
                : Math.Clamp(ActiveParameterLaneIndex, 0, ParameterLaneOptions.Count - 1);
        ActiveValueMinimum = 0;
        ActiveValueMaximum = GetActiveParameterLaneOption()?.DirectMidiTarget is { Kind: DirectMidiChannelEventKind.PitchBend }
            ? 16383
            : 127;
        ActiveValueIntegral = true;
        DirectMidiEventLaneTarget? activeTarget = GetActiveParameterLaneOption()?.DirectMidiTarget;
        ActiveValueDisplayOffset = activeTarget is { Kind: DirectMidiChannelEventKind.PitchBend } ? -8192
            : activeTarget is { } displayTarget ? MidiEditingValueDomain.Offset(displayTarget.Kind, displayTarget.Data1) : 0;
        bool activeOpaque = GetActiveParameterLaneOption()?.IsOpaqueMidiLane == true;
        ITimelineRenderItemSource? activeSource = activeOpaque
            ? new PagedDirectMidiTimelineItemSource(
                segment,
                DirectMidiTimelineProjection.OpaqueEvents,
                selectedIds: selectedIds,
                primaryId: Selection.Primary,
                opaqueEventSnapshot: opaqueEventSnapshot)
            : activeTarget is DirectMidiEventLaneTarget targetValue
                ? new PagedDirectMidiTimelineItemSource(
                    segment,
                    DirectMidiTimelineProjection.ChannelEvents,
                    targetValue,
                    selectedIds,
                    Selection.Primary,
                    channelEventSnapshot: channelEventSnapshot)
                : null;
        (TimelineRenderItem[] activeItems, ITimelineRenderItemSource? retainedActiveSource) =
            TimelinePresentationPaging.Adapt(activeSource);
        ParameterSnapshot = new(
            revision,
            $"midi-segment-events:{segment.Id.Value}:{activeTarget}:{activeOpaque}",
            activeItems,
            activeOpaque
                ? ["Imported Meta / SysEx"]
                : activeTarget is null ? [] : [DirectMidiLaneLabel(activeTarget.Value)],
            itemSource: retainedActiveSource,
            stepSignalSource: activeOpaque
                ? EventStepSignalSources.Opaque(segment.Id, opaqueEventSnapshot)
                : activeTarget is { } signalTarget
                    ? EventStepSignalSources.Direct(segment.Id, channelEventSnapshot, signalTarget) : null);
        ScheduleMidiTargetDiscovery(segment, channelEventSnapshot);
    }

    private void ScheduleMidiTargetDiscovery(
        MidiSegment segment,
        DirectMidiChannelEventQuerySnapshot snapshot)
    {
        _midiTargetDiscoverySegment = segment;
        _midiTargetDiscoverySnapshot = snapshot;
        if (IsDisposed || IsPresentationSuspended) return;
        if (_directMidiEventTargetCache.TryGetValue(
                segment.Id,
                out DirectMidiEventTargetCacheEntry? cached)
            && cached.Generation == snapshot.Generation)
        {
            return;
        }

        CancellationToken cancellationToken = _midiTargetDiscoveryCancellation.Token;
        long requestGeneration = _midiTargetDiscoveryGeneration;
        MidoraId segmentId = segment.Id;
        long sourceGeneration = snapshot.Generation;
        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        _ = Task.Run(
            () =>
            {
                return new TimelineTargetSummary<DirectMidiEventLaneTarget>(
                    snapshot.GetTargetCounts(cancellationToken).ToDictionary(
                        static pair => new DirectMidiEventLaneTarget(pair.Key.Kind, pair.Key.Number), static pair => pair.Value),
                    Comparer<DirectMidiEventLaneTarget>.Create(static (left, right) =>
                    {
                        int kind = left.Kind.CompareTo(right.Kind);
                        return kind != 0 ? kind : left.Data1.CompareTo(right.Data1);
                    }));
            },
            cancellationToken).ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        _ = task.Exception; // Observe failure even when this revision was superseded.
                        WorkspacePresentationDispatch.Post(dispatcher, cancellationToken, () =>
                        {
                            if (IsDisposed || IsPresentationSuspended || requestGeneration != _midiTargetDiscoveryGeneration
                                || ObjectId != segmentId || segment.ChannelEvents.Generation != sourceGeneration) return;
                            _laneCountError = "Count unavailable; reopen this editor to retry.";
                            Raise("LaneTargetCounts");
                        });
                        return;
                    }
                    if (task.IsCanceled || cancellationToken.IsCancellationRequested) return;
                    WorkspacePresentationDispatch.Post(dispatcher, cancellationToken,
                        () =>
                        {
                            if (IsDisposed || IsPresentationSuspended || cancellationToken.IsCancellationRequested
                                || requestGeneration != _midiTargetDiscoveryGeneration
                                || ObjectId != segmentId
                                || segment.ChannelEvents.Generation != sourceGeneration)
                            {
                                return;
                            }
                            _directMidiEventTargetCache[segmentId] = new(
                                sourceGeneration,
                                task.Result);
                            ApplyDiscoveredMidiTargets(task.Result.Targets);
                            Raise("LaneTargetCounts");
                        });
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private void ApplyDiscoveredMidiTargets(
        IReadOnlyCollection<DirectMidiEventLaneTarget> discovered)
    {
        ParameterLaneOption? previous = GetActiveParameterLaneOption();
        DirectMidiEventLaneTarget? previousTarget = previous?.DirectMidiTarget;
        bool previousOpaque = previous?.IsOpaqueMidiLane == true;
        bool hasOpaque = ParameterLaneOptions.Any(static value => value.IsOpaqueMidiLane);
        DirectMidiEventLaneTarget[] targets = discovered
            .Concat(_directMidiLaneTargets)
            .Distinct()
            .OrderBy(static value => value.Kind)
            .ThenBy(static value => value.Data1)
            .ToArray();
        ParameterLaneOptions.Clear();
        foreach (DirectMidiEventLaneTarget target in targets)
        {
            ParameterLaneOptions.Add(new(
                default,
                null,
                DirectMidiLaneLabel(target),
                DirectMidiTarget: target));
        }
        if (hasOpaque)
        {
            ParameterLaneOptions.Add(new(
                default,
                null,
                "Imported Meta / SysEx",
                IsOpaqueMidiLane: true));
        }
        int preferredIndex = previousTarget is DirectMidiEventLaneTarget selectedTarget
            ? Array.IndexOf(targets, selectedTarget)
            : previousOpaque && hasOpaque
                ? ParameterLaneOptions.Count - 1
                : -1;
        ActiveParameterLaneIndex = ParameterLaneOptions.Count == 0
            ? -1
            : preferredIndex >= 0
                ? preferredIndex
                : Math.Clamp(ActiveParameterLaneIndex, 0, ParameterLaneOptions.Count - 1);
    }

    private sealed record DirectMidiEventTargetCacheEntry(
        long Generation,
        TimelineTargetSummary<DirectMidiEventLaneTarget> Index);

    private void SetMissingSegmentSnapshots(long revision)
    {
        Context = "The Segment no longer exists.";
        Snapshot = new(revision, $"segment:{ObjectId}", Array.Empty<TimelineRenderItem>());
        ParameterSnapshot = new(revision, $"segment-parameters:{ObjectId}", Array.Empty<TimelineRenderItem>());
        VelocitySnapshot = new(revision, $"segment-velocities:{ObjectId}", Array.Empty<TimelineRenderItem>());
    }

    internal static DirectMidiEventLaneTarget ToDirectMidiLaneTarget(DirectMidiChannelEvent value) => new(
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
            ? value.Data1
            : 0);

    internal static DirectMidiEventLaneTarget ToDirectMidiLaneTarget(DirectMidiChannelEventValue value) => new(
        value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange
            or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn
            or DirectMidiChannelEventKind.NoteOff
            ? value.Data1
            : 0);

    internal static string DirectMidiLaneLabel(DirectMidiEventLaneTarget target) => target.Kind switch
    {
        DirectMidiChannelEventKind.ControlChange => MidiControlChangeCatalog.Format(target.Data1),
        DirectMidiChannelEventKind.PolyphonicKeyPressure => $"Poly Pressure · Key {target.Data1}",
        DirectMidiChannelEventKind.ProgramChange => "Program Change",
        DirectMidiChannelEventKind.ChannelPressure => "Channel Pressure",
        DirectMidiChannelEventKind.PitchBend => "Pitch Bend",
        DirectMidiChannelEventKind.NoteOn => $"Raw Note On · {MidiNoteName(target.Data1)}",
        DirectMidiChannelEventKind.NoteOff => $"Raw Note Off · {MidiNoteName(target.Data1)}",
        _ => target.Kind.ToString()
    };

    internal static string OpaqueMidiEventLabel(OpaqueMidiEvent value) =>
        OpaqueMidiEventLabel(value.Kind, value.MetaType, value.Payload.Length);

    internal static string OpaqueMidiEventLabel(OpaqueMidiEventKind kind, byte metaType, int payloadLength) => kind switch
    {
        OpaqueMidiEventKind.Meta => $"Meta 0x{metaType:X2} · {payloadLength} bytes",
        OpaqueMidiEventKind.SystemExclusive => $"SysEx F0 · {payloadLength} bytes",
        OpaqueMidiEventKind.SystemExclusiveContinuation => $"SysEx F7 · {payloadLength} bytes",
        _ => kind.ToString()
    };

    internal static double NormalizeDirectMidiEventValue(DirectMidiChannelEvent value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PolyphonicKeyPressure => value.Data2 / 127d,
        DirectMidiChannelEventKind.ControlChange => value.Data2 / 127d,
        DirectMidiChannelEventKind.ProgramChange => value.Data1 / 127d,
        DirectMidiChannelEventKind.ChannelPressure => value.Data1 / 127d,
        DirectMidiChannelEventKind.PitchBend => ((value.Data2 << 7) | value.Data1) / 16383d,
        _ => value.Data2 / 127d
    };

    internal static double NormalizeDirectMidiEventValue(DirectMidiChannelEventValue value) => value.Kind switch
    {
        DirectMidiChannelEventKind.PolyphonicKeyPressure => value.Data2 / 127d,
        DirectMidiChannelEventKind.ControlChange => value.Data2 / 127d,
        DirectMidiChannelEventKind.ProgramChange => value.Data1 / 127d,
        DirectMidiChannelEventKind.ChannelPressure => value.Data1 / 127d,
        DirectMidiChannelEventKind.PitchBend => ((value.Data2 << 7) | value.Data1) / 16383d,
        _ => value.Data2 / 127d
    };

    private void RebuildConductor(MidoraProject project, long revision) =>
        RebuildConductorPaged(project, revision);

    private TimelineRenderItem Item(
        MidoraId id,
        TimelineItemKind kind,
        long start,
        long end,
        int lane,
        int z = 0,
        double value = 0,
        TimelineItemState extra = TimelineItemState.None)
    {
        TimelineItemState state = extra;
        if (Selection.Ids.Contains(id))
        {
            state |= TimelineItemState.Selected;
        }
        if (Selection.Primary == id)
        {
            state |= TimelineItemState.Primary;
        }
        return new(id, kind, start, end, lane, value, z, state);
    }

    private void PruneSelection(IEnumerable<MidoraId> validIds)
    {
        HashSet<MidoraId> valid = validIds.ToHashSet();
        Selection.RetainOnly(valid);
    }

    internal static (LogicalTrack Track, Segment Segment)? FindSegment(
        MidoraProject project,
        MidoraId? segmentId)
    {
        if (segmentId is null) return null;
        LogicalSegmentIndexEntry? result = ProjectSegmentIndex.FindLogical(project, segmentId.Value);
        return result is null ? null : (result.Value.Track, result.Value.Segment);
    }

    internal static (PureMidiTrack Track, MidiSegment Segment)? FindMidiSegment(
        MidoraProject project,
        MidoraId? segmentId)
    {
        if (segmentId is null) return null;
        MidiSegmentIndexEntry? result = ProjectSegmentIndex.FindMidi(project, segmentId.Value);
        return result is null ? null : (result.Value.Track, result.Value.Segment);
    }

    private void PruneSelection(Func<MidoraId, bool> isValid)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        HashSet<MidoraId> retained = Selection.Ids.Where(isValid).ToHashSet();
        Selection.RetainOnly(retained);
    }

    internal static MidoraId[] FindCreatedSegmentObjectIds(
        MidoraProject project,
        MidoraId segmentId,
        long firstNewStableId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (firstNewStableId <= 0 || firstNewStableId >= project.NextStableId)
        {
            return [];
        }

        if (FindSegment(project, segmentId) is { Segment: Segment logical })
        {
            MidoraId[] lanes = logical.ParameterLanes
                .Select(static item => item.Id)
                .Where(id => id.Value >= firstNewStableId)
                .Distinct()
                .ToArray();
            if (lanes.Length != 0) return lanes;
            return logical.Notes.Select(static item => item.Id)
                .Concat(logical.ParameterLanes
                    .SelectMany(static item => item.Points)
                    .Select(static item => item.Id))
                .Where(id => id.Value >= firstNewStableId)
                .Distinct()
                .ToArray();
        }

        if (FindMidiSegment(project, segmentId) is not { Segment: MidiSegment midi })
        {
            return [];
        }

        int candidateCount = checked((int)(project.NextStableId - firstNewStableId));
        MidoraId[] candidates = new MidoraId[candidateCount];
        for (int index = 0; index < candidates.Length; index++)
        {
            candidates[index] = new MidoraId(checked(firstNewStableId + index));
        }

        MidoraId[] preferred = midi.Notes.ResolveByIds(candidates)
            .Select(static match => match.Value.Id)
            .ToArray();
        if (preferred.Length != 0) return preferred;
        preferred = midi.ChannelEvents.ResolveByIds(candidates)
            .Select(static match => match.Value.Id)
            .ToArray();
        if (preferred.Length != 0) return preferred;
        return midi.OpaqueEvents.ResolveByIds(candidates)
            .Select(static match => match.Value.Id)
            .ToArray();
    }

    internal static string TrackDisplayName(MidoraProject project, LogicalTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.Name))
        {
            return track.Name;
        }
        int index = project.ArrangementTracks.IndexOf(
            new(ArrangementTrackKind.LogicalTrack, track.Id)) + 1;
        if (index == 0)
        {
            index = project.Tracks.IndexOf(track) + 1;
        }
        return $"Logical Track {index}";
    }

    internal static string BoundInstrumentDisplayName(MidoraProject project, LogicalTrack track)
    {
        MidoraId? instrumentId = project.ResolveEventInstrumentDefinitionId(track);
        if (instrumentId is not MidoraId definitionId)
        {
            return "Unbound";
        }
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(
            item => item.Id == definitionId);
        if (instrument is not null)
        {
            return string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name;
        }
        return string.IsNullOrWhiteSpace(track.LastBoundEventInstrumentName)
            ? "Missing Event Instrument"
            : $"Missing: {track.LastBoundEventInstrumentName}";
    }

    internal static string MidiNoteName(int note)
    {
        note = Math.Clamp(note, 0, 127);
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[note % 12]}{note / 12 - 1}";
    }

    internal static double NormalizeParameterValue(LogicalParameterDefinition? definition, double value)
    {
        if (definition is null) return 0.5;
        double minimum = definition.DisplayMinimum;
        double maximum = definition.DisplayMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = definition.Minimum;
            maximum = definition.Maximum;
        }
        if (maximum <= minimum) return 0.5;
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    internal static double DenormalizeParameterValue(LogicalParameterDefinition definition, double normalized)
    {
        double minimum = definition.DisplayMinimum;
        double maximum = definition.DisplayMaximum;
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum <= minimum)
        {
            minimum = definition.Minimum;
            maximum = definition.Maximum;
        }
        double value = minimum + Math.Clamp(normalized, 0, 1) * (maximum - minimum);
        if (definition.Type == LogicalParameterType.Double)
        {
            return value;
        }

        double integral = Math.Round(value, MidpointRounding.AwayFromZero);
        if (definition.Type != LogicalParameterType.Enum
            || !definition.UsesExplicitEnumValues
            || definition.EnumItems.Count == 0)
        {
            return integral;
        }
        return definition.EnumItems
            .OrderBy(item => Math.Abs(item.Value - integral))
            .ThenBy(item => item.Value)
            .First()
            .Value;
    }
}

public sealed record InstrumentListItem(
    MidoraId Id,
    string Name,
    int UsageCount,
    string Status);

public enum InstrumentLibrarySortMode
{
    Manual,
    Name,
    Usage
}

public sealed record SubVoiceListItem(MidoraId Id, string Name, int EventCount);
public sealed record LogicalParameterListItem(MidoraId Id, string Name, string Type, string Range);
public sealed record MappingFunctionListItem(MidoraId Id, string Name, int AbiVersion);
public sealed record ParameterMappingListItem(MidoraId Id, string Source, string Target, int StepCount);
public sealed record EnvelopeListItem(MidoraId Id, string Name, string Summary);
public sealed record MappingChainListItem(
    MidoraId Id,
    string Owner,
    int StepCount,
    bool CanDelete);
public sealed record MappingStepListItem(MidoraId Id, MidoraId ChainId, string Owner, int Index, string Summary);

public sealed record InstrumentRenderLane(
    MidoraId SubVoiceId,
    string Label,
    MidiValueTarget? Target = null,
    MidoraId? ValueCurveId = null,
    MidoraId? EventMappingChainId = null,
    TemplateEventMappingTarget? EventMappingTarget = null);

public sealed record InitialStateListItem(string Target, string Value, string Scope);

public enum InstrumentPreviewMode
{
    FullInstrument,
    SelectedSubVoice
}

public sealed class LibraryWorkspaceViewModel()
    : WorkspaceViewModel(
        WorkspaceKey.ForType(WorkspaceKind.EventInstrumentLibrary),
        "Event Instrument Library")
{
    private readonly List<InstrumentListItem> _allInstruments = [];
    private string _searchText = string.Empty;
    private InstrumentLibrarySortMode _sortMode;
    private InstrumentListItem? _selectedInstrument;

    public ObservableCollection<InstrumentListItem> Instruments { get; } = [];
    public Array SortModes { get; } = Enum.GetValues<InstrumentLibrarySortMode>();
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty)) ApplyView();
        }
    }
    public InstrumentLibrarySortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (Set(ref _sortMode, value)) ApplyView();
        }
    }
    public InstrumentListItem? SelectedInstrument
    {
        get => _selectedInstrument;
        set => Set(ref _selectedInstrument, value);
    }

    public override void Rebuild(MidoraProject project, long revision)
    {
        MidoraId? selectedId = SelectedInstrument?.Id;
        _allInstruments.Clear();
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            HashSet<MidoraId> usageIds = project.EventInstrumentUsages
                .Where(value => value.EventInstrumentId == instrument.Id)
                .Select(value => value.Id)
                .ToHashSet();
            int usage = project.Tracks.Count(track =>
                track.EventInstrumentUsageId is MidoraId usageId
                && usageIds.Contains(usageId));
            _allInstruments.Add(new(instrument.Id, instrument.Name, usage, "Current"));
        }
        ApplyView();
        SelectedInstrument = selectedId is MidoraId id
            ? Instruments.FirstOrDefault(item => item.Id == id)
            : null;
    }

    private void ApplyView()
    {
        IEnumerable<InstrumentListItem> query = _allInstruments;
        string search = SearchText.Trim();
        if (search.Length != 0)
        {
            query = query.Where(item => item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Status.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        }
        query = SortMode switch
        {
            InstrumentLibrarySortMode.Name => query.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            InstrumentLibrarySortMode.Usage => query.OrderByDescending(item => item.UsageCount)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query
        };
        Instruments.Clear();
        foreach (InstrumentListItem item in query) Instruments.Add(item);
    }
}

public sealed class InstrumentWorkspaceViewModel(
    MidoraId instrumentId,
    string header)
    : WorkspaceViewModel(
        WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, instrumentId),
        header)
{
    private readonly Dictionary<MidoraId, SubVoiceEditorSettings> _subVoiceEditorSettings = [];
    private readonly Dictionary<MidoraId, SubVoiceEventTargetCacheEntry> _subVoiceEventTargetCache = [];
    private CancellationTokenSource _subVoiceTargetDiscoveryCancellation = new();
    private long _subVoiceTargetDiscoveryGeneration;
    private MidoraProject? _subVoiceTargetDiscoveryProject;
    private long _subVoiceTargetDiscoveryRevision;
    private SubVoice? _subVoiceTargetDiscoveryVoice;
    private TemplateEventQuerySnapshot? _subVoiceTargetDiscoverySnapshot;
    private string _summary = string.Empty;
    private TimelineRenderSnapshot? _subVoiceSnapshot;
    private TimelineRenderSnapshot? _subVoiceNoteSnapshot;
    private TimelineRenderSnapshot? _subVoiceEventSnapshot;
    private TimelineRenderSnapshot? _subVoiceVelocitySnapshot;
    private int _activeRenderLaneIndex;
    private MidiValueTarget? _explicitRenderLaneTarget;
    private long _explicitRenderLaneSelectionRevision = -1;
    private MidoraId? _activeSubVoiceId;
    private string _activeSubVoiceName = "No SubVoice";
    private string _activeSubVoiceNameText = string.Empty;
    private string _activeSubVoiceRootNoteText = string.Empty;
    private string _activeSubVoiceContext = "Create or select a SubVoice to edit its timeline.";
    private bool _activeSubVoiceFollowsInstanceVelocity;
    private bool _requiresChannelIsolation;
    private ShortNoteLifecycle _shortLifecycle;
    private LongNoteLifecycle _longLifecycle;
    private OverlapPolicy _overlapPolicy;
    private OverlapScope _overlapScope;
    private string _loopStartText = string.Empty;
    private string _loopEndText = string.Empty;
    private string _instrumentNameText = string.Empty;
    private string _instrumentDescriptionText = string.Empty;
    private string _instrumentColorText = "#6B7280";
    private string _instrumentRootNoteText = "60";
    private string _instrumentTemplateLengthText = "1";
    private string _instrumentPreRollTicksText = "0";
    private long? _loopStartTick;
    private long? _loopEndTick;
    private long _templateLengthTicks = 1;
    private long _preRollTicks;
    private int _activeRootPitch = 60;
    private long _scenarioGateLengthTicks = 192;
    private int _scenarioPitch = 60;
    private int _scenarioVelocity = 100;
    private bool _scenarioHasHardBoundary;
    private long _scenarioHardBoundaryTick = 384;
    private TimelineToolMode _toolMode = TimelineToolMode.Select;
    private double _activeValueMinimum;
    private double _activeValueMaximum = 127;
    private bool _activeValueIntegral = true;
    private bool _isPreviewExpanded = true;
    private bool _isPreviewMuted;
    private bool _isPreviewSoloSelected;
    private InstrumentPreviewMode _previewMode;
    private long? _editCursorTick;
    private MidoraId? _selectedMappingStepId;
    private int _activeSectionIndex;
    private long _timelineStartTick;
    private long _timelineTickSpan = PianoEditorDefaults.TickSpan;
    private int _timelineFirstLane = PianoEditorDefaults.SubVoiceFirstLane;
    private double _timelineLaneHeight = PianoEditorDefaults.LaneHeight;
    private bool _isLowerEditorVisible = true;
    private double _lowerEditorHeight = TimelineLowerEditorLayout.DefaultHeight;
    private int _activeLowerEditorIndex;
    private double _velocityValueScrollOffset;
    private double _eventValueScrollOffset;
    private GridLength _leftPaneWidth = new(470);
    private double _leftPaneVerticalOffset;

    private TimelineEditorSettings _editorSettings = new();
    private TimelineEditorSettings _eventLaneEditorSettings = new();

    public TimelineEditorSettings EditorSettings
    {
        get => _editorSettings;
        private set => Set(ref _editorSettings, value);
    }

    public TimelineEditorSettings EventLaneEditorSettings
    {
        get => _eventLaneEditorSettings;
        private set => Set(ref _eventLaneEditorSettings, value);
    }

    public int ActiveSectionIndex
    {
        get => _activeSectionIndex;
        set => Set(ref _activeSectionIndex, Math.Clamp(value, 0, 1));
    }

    public GridLength LeftPaneWidth
    {
        get => _leftPaneWidth;
        set
        {
            if (value.GridUnitType != GridUnitType.Pixel || !double.IsFinite(value.Value)) return;
            Set(ref _leftPaneWidth, new GridLength(Math.Clamp(value.Value, 280, 760)));
        }
    }

    public double LeftPaneVerticalOffset
    {
        get => _leftPaneVerticalOffset;
        set => Set(ref _leftPaneVerticalOffset, Math.Max(0, value));
    }

    public long TimelineStartTick
    {
        get => _timelineStartTick;
        set => Set(ref _timelineStartTick, Math.Max(0, value));
    }

    public long TimelineTickSpan
    {
        get => _timelineTickSpan;
        set => Set(ref _timelineTickSpan, Math.Max(1, value));
    }

    public int TimelineFirstLane
    {
        get => _timelineFirstLane;
        set => Set(ref _timelineFirstLane, Math.Clamp(value, 0, 127));
    }

    public double TimelineLaneHeight
    {
        get => _timelineLaneHeight;
        set => Set(
            ref _timelineLaneHeight,
            Math.Clamp(
                value,
                0.25,
                TimelineSurface.MaximumPianoLaneHeight));
    }

    public bool IsLowerEditorVisible
    {
        get => _isLowerEditorVisible;
        set
        {
            if (!value && _isLowerEditorVisible) CaptureLaneViewState();
            if (!Set(ref _isLowerEditorVisible, value)) return;
            Raise(nameof(BottomEditorRowHeight));
            Raise(nameof(BottomEditorMinimumHeight));
        }
    }

    public double BottomEditorMinimumHeight => IsLowerEditorVisible
        ? TimelineLowerEditorLayout.MinimumHeight
        : 0;

    public double BottomEditorMaximumHeight => TimelineLowerEditorLayout.MaximumHeight;

    internal double LastLowerEditorHeight => _lowerEditorHeight;
    internal void RestoreLowerEditorHeight(double height) => Set(ref _lowerEditorHeight, height, nameof(BottomEditorRowHeight));

    public GridLength BottomEditorRowHeight
    {
        get => IsLowerEditorVisible
            ? new GridLength(_lowerEditorHeight)
            : new GridLength(0);
        set
        {
            if (!IsLowerEditorVisible || value.GridUnitType != GridUnitType.Pixel
                || !double.IsFinite(value.Value))
            {
                return;
            }
            double height = Math.Clamp(
                value.Value,
                TimelineLowerEditorLayout.MinimumHeight,
                TimelineLowerEditorLayout.MaximumHeight);
            Set(ref _lowerEditorHeight, height, nameof(BottomEditorRowHeight));
        }
    }

    public int ActiveLowerEditorIndex
    {
        get => _activeLowerEditorIndex;
        set => Set(ref _activeLowerEditorIndex, Math.Clamp(value, 0, 2));
    }

    public double VelocityValueScrollOffset
    {
        get => _velocityValueScrollOffset;
        set => Set(ref _velocityValueScrollOffset, Math.Max(0, value));
    }

    public double EventValueScrollOffset
    {
        get => _eventValueScrollOffset;
        set => Set(ref _eventValueScrollOffset, Math.Max(0, value));
    }

    public long? EditCursorTick
    {
        get => _editCursorTick;
        set => Set(ref _editCursorTick, value is null ? null : Math.Max(0, value.Value));
    }

    public MidoraId? SelectedMappingStepId
    {
        get => _selectedMappingStepId;
        set => Set(ref _selectedMappingStepId, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }
    public TimelineRenderSnapshot? SubVoiceSnapshot
    {
        get => _subVoiceSnapshot;
        private set => Set(ref _subVoiceSnapshot, value);
    }
    public TimelineRenderSnapshot? SubVoiceNoteSnapshot
    {
        get => _subVoiceNoteSnapshot;
        private set => Set(ref _subVoiceNoteSnapshot, value);
    }
    public TimelineRenderSnapshot? SubVoiceEventSnapshot
    {
        get => _subVoiceEventSnapshot;
        private set => Set(ref _subVoiceEventSnapshot, value);
    }
    public TimelineRenderSnapshot? SubVoiceVelocitySnapshot
    {
        get => _subVoiceVelocitySnapshot;
        private set => Set(ref _subVoiceVelocitySnapshot, value);
    }
    public int ActiveRenderLaneIndex
    {
        get => _activeRenderLaneIndex;
        set => Set(ref _activeRenderLaneIndex, Math.Max(-1, value));
    }
    public void PreferCurrentRenderLaneOnNextRebuild()
    {
        _explicitRenderLaneTarget = GetRenderLane(ActiveRenderLaneIndex)?.Target;
        _explicitRenderLaneSelectionRevision = Selection.Revision;
    }

    internal void PreferEventLaneOnNextRebuild(MidiValueTarget target)
    {
        // Creation is committed before the dispatcher rebuilds RenderLanes.
        // Record stable navigation intent without consulting that stale list.
        _explicitRenderLaneTarget = target;
        _explicitRenderLaneSelectionRevision = Selection.Revision;
    }

    public bool TryActivateEventLane(MidiValueTarget target)
    {
        int index = -1;
        for (int i = 0; i < RenderLanes.Count; i++)
            if (RenderLanes[i].Target == target) { index = i; break; }
        if (index < 0) return false;
        ActiveRenderLaneIndex = index;
        PreferCurrentRenderLaneOnNextRebuild();
        IsLowerEditorVisible = true;
        ActiveLowerEditorIndex = 2;
        return true;
    }
    public double ActiveValueMinimum
    {
        get => _activeValueMinimum;
        private set { if (Set(ref _activeValueMinimum, value)) Raise(nameof(ActiveValueAxisMinimum)); }
    }
    public double ActiveValueMaximum
    {
        get => _activeValueMaximum;
        private set { if (Set(ref _activeValueMaximum, value)) Raise(nameof(ActiveValueAxisMaximum)); }
    }
    private int _activeEditingValueOffset;
    public double ActiveValueAxisMinimum => ActiveValueMinimum + _activeEditingValueOffset;
    public double ActiveValueAxisMaximum => ActiveValueMaximum + _activeEditingValueOffset;
    public bool ActiveValueIntegral
    {
        get => _activeValueIntegral;
        private set => Set(ref _activeValueIntegral, value);
    }
    public MidoraId? ActiveSubVoiceId
    {
        get => _activeSubVoiceId;
        private set { if (Set(ref _activeSubVoiceId, value)) Raise(nameof(ValueTraceShape)); }
    }
    private readonly Dictionary<MidoraId, TimelineValueTraceShape> _subVoiceTraceShapes = [];
    public override TimelineValueTraceShape ValueTraceShape
    {
        get => ActiveSubVoiceId is { } id ? _subVoiceTraceShapes.GetValueOrDefault(id) : TimelineValueTraceShape.Free;
        set
        {
            if (ActiveSubVoiceId is not { } id || ValueTraceShape == value) return;
            _subVoiceTraceShapes[id] = value;
            Raise(nameof(ValueTraceShape));
        }
    }
    public string ActiveSubVoiceName
    {
        get => _activeSubVoiceName;
        private set => Set(ref _activeSubVoiceName, value);
    }
    public string ActiveSubVoiceNameText
    {
        get => _activeSubVoiceNameText;
        set => Set(ref _activeSubVoiceNameText, value ?? string.Empty);
    }
    public string ActiveSubVoiceRootNoteText
    {
        get => _activeSubVoiceRootNoteText;
        set => Set(ref _activeSubVoiceRootNoteText, value ?? string.Empty);
    }
    public string ActiveSubVoiceContext
    {
        get => _activeSubVoiceContext;
        private set => Set(ref _activeSubVoiceContext, value);
    }
    public bool ActiveSubVoiceFollowsInstanceVelocity
    {
        get => _activeSubVoiceFollowsInstanceVelocity;
        private set => Set(ref _activeSubVoiceFollowsInstanceVelocity, value);
    }
    public Array ShortLifecycleValues { get; } = Enum.GetValues<ShortNoteLifecycle>();
    public Array LongLifecycleValues { get; } = Enum.GetValues<LongNoteLifecycle>();
    public Array OverlapPolicyValues { get; } = Enum.GetValues<OverlapPolicy>();
    public Array OverlapScopeValues { get; } = Enum.GetValues<OverlapScope>();
    public bool RequiresChannelIsolation { get => _requiresChannelIsolation; private set => Set(ref _requiresChannelIsolation, value); }
    public ShortNoteLifecycle ShortLifecycle { get => _shortLifecycle; private set => Set(ref _shortLifecycle, value); }
    public LongNoteLifecycle LongLifecycle { get => _longLifecycle; private set => Set(ref _longLifecycle, value); }
    public OverlapPolicy OverlapPolicy { get => _overlapPolicy; private set => Set(ref _overlapPolicy, value); }
    public OverlapScope OverlapScope { get => _overlapScope; private set => Set(ref _overlapScope, value); }
    public string LoopStartText { get => _loopStartText; set => Set(ref _loopStartText, value ?? string.Empty); }
    public string LoopEndText { get => _loopEndText; set => Set(ref _loopEndText, value ?? string.Empty); }
    public string InstrumentNameText { get => _instrumentNameText; set => Set(ref _instrumentNameText, value ?? string.Empty); }
    public string InstrumentDescriptionText { get => _instrumentDescriptionText; set => Set(ref _instrumentDescriptionText, value ?? string.Empty); }
    public string InstrumentColorText { get => _instrumentColorText; set => Set(ref _instrumentColorText, value ?? string.Empty); }
    public string InstrumentRootNoteText { get => _instrumentRootNoteText; set => Set(ref _instrumentRootNoteText, value ?? string.Empty); }
    public string InstrumentTemplateLengthText { get => _instrumentTemplateLengthText; set => Set(ref _instrumentTemplateLengthText, value ?? string.Empty); }
    public string InstrumentPreRollTicksText { get => _instrumentPreRollTicksText; set => Set(ref _instrumentPreRollTicksText, value ?? string.Empty); }
    public long? LoopStartTick { get => _loopStartTick; private set => Set(ref _loopStartTick, value); }
    public long PreRollTicks { get => _preRollTicks; private set => Set(ref _preRollTicks, value); }
    public long? LoopEndTick { get => _loopEndTick; private set => Set(ref _loopEndTick, value); }
    public long TemplateLengthTicks { get => _templateLengthTicks; private set => Set(ref _templateLengthTicks, Math.Max(1, value)); }
    private long? _minimumTemplateLength;
    private long _templateEditRevision;
    public long? MinimumTemplateLength { get => _minimumTemplateLength; private set => Set(ref _minimumTemplateLength, value); }
    public long TemplateEditRevision { get => _templateEditRevision; private set => Set(ref _templateEditRevision, value); }
    public int ActiveRootPitch { get => _activeRootPitch; private set => Set(ref _activeRootPitch, Math.Clamp(value, 0, 127)); }
    public long ScenarioGateLengthTicks { get => _scenarioGateLengthTicks; set => Set(ref _scenarioGateLengthTicks, Math.Max(1, value)); }
    public int ScenarioPitch { get => _scenarioPitch; set => Set(ref _scenarioPitch, Math.Clamp(value, 0, 127)); }
    public int ScenarioVelocity { get => _scenarioVelocity; set => Set(ref _scenarioVelocity, Math.Clamp(value, 1, 127)); }
    public bool ScenarioHasHardBoundary { get => _scenarioHasHardBoundary; set => Set(ref _scenarioHasHardBoundary, value); }
    public long ScenarioHardBoundaryTick { get => _scenarioHardBoundaryTick; set => Set(ref _scenarioHardBoundaryTick, Math.Max(1, value)); }
    public TimelineToolMode ToolMode
    {
        get => _toolMode;
        set
        {
            if (!Set(ref _toolMode, value)) return;
            Raise(nameof(IsSelectTool));
            Raise(nameof(IsDrawTool));
            Raise(nameof(IsEraseTool));
        }
    }
    public bool IsSelectTool => ToolMode == TimelineToolMode.Select;
    public bool IsDrawTool => ToolMode == TimelineToolMode.Draw;
    public bool IsEraseTool => ToolMode == TimelineToolMode.Erase;
    public bool IsPreviewExpanded { get => _isPreviewExpanded; set => Set(ref _isPreviewExpanded, value); }
    public bool IsPreviewMuted { get => _isPreviewMuted; set => Set(ref _isPreviewMuted, value); }
    public bool IsPreviewSoloSelected { get => _isPreviewSoloSelected; set => Set(ref _isPreviewSoloSelected, value); }
    public InstrumentPreviewMode PreviewMode
    {
        get => _previewMode;
        set
        {
            if (Set(ref _previewMode, value)) Raise(nameof(PreviewModeIndex));
        }
    }
    public int PreviewModeIndex
    {
        get => (int)PreviewMode;
        set => PreviewMode = value == (int)InstrumentPreviewMode.SelectedSubVoice
            ? InstrumentPreviewMode.SelectedSubVoice
            : InstrumentPreviewMode.FullInstrument;
    }
    public IReadOnlyList<string> PreviewModeLabels { get; } = ["Full Instrument", "Selected SubVoice"];
    public ObservableCollection<SubVoiceListItem> SubVoices { get; } = [];
    public ObservableCollection<LogicalParameterListItem> Parameters { get; } = [];
    public ObservableCollection<MappingFunctionListItem> MappingFunctions { get; } = [];
    public ObservableCollection<ParameterMappingListItem> ParameterMappings { get; } = [];
    public ObservableCollection<EnvelopeListItem> Envelopes { get; } = [];
    public ObservableCollection<MappingChainListItem> MappingChains { get; } = [];
    public ObservableCollection<MappingStepListItem> MappingSteps { get; } = [];
    public ObservableCollection<InitialStateListItem> InitialStateEntries { get; } = [];
    public ObservableCollection<PropertyField> ActiveSubVoiceInitialStateFields { get; } = [];
    public ObservableCollection<PropertyField> InstrumentInitialStateFields { get; } = [];
    public IEnumerable<PropertyField> InstrumentInitialInstrumentFields => InstrumentInitialStateFields.Take(3);
    public IEnumerable<PropertyField> ActiveSubVoiceInitialInstrumentFields => ActiveSubVoiceInitialStateFields.Take(3);
    public IEnumerable<PropertyField> InstrumentOtherInitialStateFields => InstrumentInitialStateFields.Skip(3);
    public IEnumerable<PropertyField> ActiveSubVoiceOtherInitialStateFields => ActiveSubVoiceInitialStateFields.Skip(3);
    public string InstrumentPresetSummary => PresetSummary(InstrumentInitialInstrumentFields);
    public string ActiveSubVoicePresetSummary => PresetSummary(ActiveSubVoiceInitialInstrumentFields);
    private static string PresetSummary(IEnumerable<PropertyField> fields) => "Instrument · " +
        string.Join(" / ", fields.Select(field => string.IsNullOrWhiteSpace(field.Value) ? "Inherit" : field.Value)) + " …";
    public ObservableCollection<InstrumentRenderLane> RenderLanes { get; } = [];

    public override void RefreshSelectionPresentation()
    {
        TryRefreshSelectionPresentation(
            [SubVoiceNoteSnapshot, SubVoiceEventSnapshot]);
    }

    public override void PublishMaterializedSelection(
        IReadOnlyDictionary<TimelineItemKind, TimelineSelectionMetrics> metrics,
        bool metricsAreComplete,
        TimelineSelectionRenderIndex? renderIndex = null)
    {
        base.PublishMaterializedSelection(metrics, metricsAreComplete, renderIndex);
        ScheduleMaterializedSelectionMetricsIfNeeded(
            metricsAreComplete,
            [SubVoiceNoteSnapshot, SubVoiceEventSnapshot]);
    }

    public override void Rebuild(MidoraProject project, long revision)
    {
        using var editorStateUpdate = EditorState?.BeginRebuild();
        BeginPresentationRebuild();
        _subVoiceTargetDiscoveryCancellation.Cancel();
        _subVoiceTargetDiscoveryCancellation.Dispose();
        _subVoiceTargetDiscoveryCancellation = new();
        _subVoiceTargetDiscoveryGeneration = checked(_subVoiceTargetDiscoveryGeneration + 1);
        _subVoiceTargetDiscoveryProject = null;
        _subVoiceTargetDiscoveryVoice = null;
        _subVoiceTargetDiscoverySnapshot = null;
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == ObjectId);
        SubVoices.Clear();
        Parameters.Clear();
        MappingFunctions.Clear();
        ParameterMappings.Clear();
        Envelopes.Clear();
        MappingChains.Clear();
        MappingSteps.Clear();
        InitialStateEntries.Clear();
        ActiveSubVoiceInitialStateFields.Clear();
        InstrumentInitialStateFields.Clear();
        MidiValueTarget? previousTarget = GetRenderLane(ActiveRenderLaneIndex)?.Target;
        RenderLanes.Clear();
        if (instrument is null)
        {
            SelectedMappingStepId = null;
            Summary = "The Event Instrument no longer exists.";
            SubVoiceSnapshot = new(revision, $"instrument:{ObjectId}", Array.Empty<TimelineRenderItem>());
            SubVoiceNoteSnapshot = new(revision, $"instrument-notes:{ObjectId}", Array.Empty<TimelineRenderItem>());
            SubVoiceEventSnapshot = new(revision, $"instrument-events:{ObjectId}", Array.Empty<TimelineRenderItem>());
            SubVoiceVelocitySnapshot = new(revision, $"instrument-velocities:{ObjectId}", Array.Empty<TimelineRenderItem>());
            ActiveSubVoiceId = null;
            ObjectList.SetFactory(null);
            ActiveSubVoiceName = "Missing SubVoice";
            ActiveSubVoiceContext = "The Event Instrument no longer exists.";
            ActiveSubVoiceFollowsInstanceVelocity = false;
            return;
        }

        Header = instrument.Name;
        InstrumentNameText = instrument.Name;
        InstrumentDescriptionText = instrument.Description ?? string.Empty;
        InstrumentColorText = $"#{instrument.Color.Red:X2}{instrument.Color.Green:X2}{instrument.Color.Blue:X2}";
        InstrumentRootNoteText = instrument.RootNote.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InstrumentTemplateLengthText = instrument.TemplateLengthTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        InstrumentPreRollTicksText = instrument.PreRollTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PreRollTicks = instrument.PreRollTicks;
        AddInstrumentInitialStateFields(instrument.InitialState);
        Raise(nameof(InstrumentPresetSummary)); Raise(nameof(InstrumentInitialInstrumentFields));
        Raise(nameof(InstrumentOtherInitialStateFields));
        Summary = $"Root {MidiNoteName(instrument.RootNote)} · Template {instrument.TemplateLengthTicks} ticks · Pre-Roll {instrument.PreRollTicks} ticks · {instrument.SubVoices.Count} SubVoices";
        RequiresChannelIsolation = instrument.RequiresChannelIsolation;
        ShortLifecycle = instrument.ShortLifecycle;
        LongLifecycle = instrument.LongLifecycle;
        OverlapPolicy = instrument.OverlapPolicy;
        OverlapScope = instrument.OverlapScope;
        LoopStartText = instrument.LoopStartTick?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        LoopEndText = instrument.LoopEndTick?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        LoopStartTick = instrument.LoopStartTick;
        LoopEndTick = instrument.LoopEndTick;
        TemplateLengthTicks = instrument.TemplateLengthTicks;
        TemplateEditRevision = revision;
        try { MinimumTemplateLength = ProjectDomainEditCommands.GetMinimumTemplateLength(instrument); }
        catch (Exception error) when (error is InvalidOperationException or OverflowException)
        { MinimumTemplateLength = null; } // Invalid draft remains editable in Configurations; no unsafe drag handle.
        for (int index = 0; index < instrument.SubVoices.Count; index++)
        {
            SubVoice voice = instrument.SubVoices[index];
            SubVoices.Add(new(
                voice.Id,
                string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name,
                voice.Events.Count));
        }
        foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
        {
            Parameters.Add(new(
                parameter.Id,
                parameter.Name,
                parameter.Type.ToString(),
                $"{parameter.Minimum}–{parameter.Maximum}"));
        }
        foreach (CSharpMappingFunction function in instrument.MappingFunctions)
        {
            MappingFunctions.Add(new(function.Id, function.Name, function.AbiVersion));
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            string source = instrument.LogicalParameters.FirstOrDefault(item => item.Id == mapping.ParameterId)?.Name
                ?? "Unavailable Logical Parameter";
            SubVoice? voice = instrument.SubVoices.FirstOrDefault(item => item.Id == mapping.SubVoiceId);
            string targetVoice = voice is null
                ? "Unavailable SubVoice"
                : string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {instrument.SubVoices.IndexOf(voice) + 1}" : voice.Name;
            ParameterMappings.Add(new(
                mapping.Id,
                source,
                $"{targetVoice} · {FormatTarget(mapping.Target)}",
                mapping.Steps.Count));
            AddMappingChain(
                mapping.Steps,
                $"Parameter · {source} → {targetVoice}",
                canDelete: true);
        }
        foreach (SubVoice voice in instrument.SubVoices)
        {
            string voiceName = string.IsNullOrWhiteSpace(voice.Name)
                ? $"SubVoice {instrument.SubVoices.IndexOf(voice) + 1}"
                : voice.Name;
            foreach (SubVoiceEventMapping mapping in voice.EventMappings
                .OrderBy(value => value.Target.EventKind)
                .ThenBy(value => value.Target.EventNumber)
                .ThenBy(value => value.Target.Parameter))
            {
                AddMappingChain(
                    mapping.Steps,
                    $"{voiceName} · {FormatEventMappingTarget(mapping.Target)}",
                    canDelete: mapping.Target.EventKind != TemplateEventKind.Note);
            }
        }

        if (SelectedMappingStepId is MidoraId selectedStepId
            && !MappingSteps.Any(value => value.Id == selectedStepId))
        {
            SelectedMappingStepId = null;
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            Envelopes.Add(new(
                envelope.Id,
                string.IsNullOrWhiteSpace(envelope.Name) ? "Envelope Preset" : envelope.Name,
                $"D {envelope.DelayTicks} · A {envelope.AttackTicks} · H {envelope.HoldTicks} · D {envelope.DecayTicks} · R {envelope.ReleaseTicks}"));
        }

        SubVoice? activeVoice = ResolveActiveSubVoice(instrument, Selection.Primary, ActiveSubVoiceId);
        HashSet<MidoraId> liveSubVoiceIds = instrument.SubVoices
            .Select(value => value.Id)
            .ToHashSet();
        foreach (MidoraId staleId in _subVoiceEditorSettings.Keys
            .Where(value => !liveSubVoiceIds.Contains(value))
            .ToArray())
        {
            _subVoiceEditorSettings.Remove(staleId);
        }
        foreach (MidoraId staleId in _subVoiceEventTargetCache.Keys
            .Where(value => !liveSubVoiceIds.Contains(value))
            .ToArray())
        {
            _subVoiceEventTargetCache.Remove(staleId);
        }
        if (activeVoice is not null)
        {
            if (!_subVoiceEditorSettings.TryGetValue(
                    activeVoice.Id,
                    out SubVoiceEditorSettings? settings))
            {
                TimelineEditorSettings piano = new();
                TimelineEditorSettings eventLane = new();
                piano.Reset(arrangement: false, project.TicksPerQuarterNote);
                eventLane.Reset(arrangement: false, project.TicksPerQuarterNote);
                settings = new(piano, eventLane);
                _subVoiceEditorSettings.Add(activeVoice.Id, settings);
            }
            settings.Piano.ConfigureProject(project, referenceTick: 0);
            settings.EventLane.ConfigureProject(project, referenceTick: 0);
            EditorSettings = settings.Piano;
            EventLaneEditorSettings = settings.EventLane;
        }
        else
        {
            EditorSettings.ConfigureProject(project, referenceTick: 0);
            EventLaneEditorSettings.ConfigureProject(project, referenceTick: 0);
        }
        EditorState?.PrepareSubVoice(project, activeVoice?.Id);
        ActiveSubVoiceId = activeVoice?.Id;
        ObjectList.SetFactory(activeVoice is null ? null
            : () => TimelineObjectListSource.CreateSubVoice(project, activeVoice, instrument.Id));
        ActiveRootPitch = activeVoice?.RootNoteOverride ?? instrument.RootNote;
        ActiveSubVoiceName = activeVoice is null
            ? "No SubVoice"
            : string.IsNullOrWhiteSpace(activeVoice.Name)
                ? $"SubVoice {instrument.SubVoices.IndexOf(activeVoice) + 1}"
                : activeVoice.Name;
        ActiveSubVoiceNameText = activeVoice?.Name ?? string.Empty;
        ActiveSubVoiceRootNoteText = activeVoice?.RootNoteOverride?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        ActiveSubVoiceContext = activeVoice is null
            ? "Create or select a SubVoice to edit its timeline."
            : $"Root Note {(activeVoice.RootNoteOverride.HasValue ? "override" : "inherited")} · "
              + $"effective {MidiNoteName(activeVoice.RootNoteOverride ?? instrument.RootNote)} · "
              + $"Template {instrument.TemplateLengthTicks} ticks";
        ActiveSubVoiceFollowsInstanceVelocity = activeVoice is not null
            && SubVoiceMappingConventions.FollowsInstanceVelocity(activeVoice);

        List<InstrumentRenderLane> lanes = [];
        IReadOnlySet<MidoraId> selectedIds = Selection.IdSet;
        TemplateEventQuerySnapshot? templateSnapshot = activeVoice?.Events.CreateQuerySnapshot();
        if (activeVoice is not null)
        {
            Dictionary<MidiValueTarget, SubVoiceEventMapping?> eventLaneMappings = [];
            foreach (SubVoiceEventMapping mapping in activeVoice.EventMappings)
            {
                if (TemplateEventMidiTargets.TryFromMappingTarget(
                        mapping.Target,
                        out MidiValueTarget laneMidiTarget))
                {
                    eventLaneMappings[laneMidiTarget] = mapping;
                }
            }
            foreach (long discoveryKey in templateSnapshot!.DiscoveryKeys)
            {
                if (TemplateEventMidiTargets.TryDecodeDiscoveryKey(
                        discoveryKey,
                        out MidiValueTarget eventTarget))
                {
                    eventLaneMappings.TryAdd(eventTarget, null);
                }
            }
            foreach (ValueCurve curve in activeVoice.Curves)
                eventLaneMappings.TryAdd(curve.Target, null);
            if (previousTarget is MidiValueTarget retainedTarget)
                eventLaneMappings.TryAdd(retainedTarget, null);
            ScheduleSubVoiceEventTargetIndex(
                project,
                revision,
                activeVoice,
                templateSnapshot);
            foreach ((MidiValueTarget laneMidiTarget, SubVoiceEventMapping? mapping) in eventLaneMappings
                .OrderBy(value => value.Key.Kind)
                .ThenBy(value => value.Key.Number))
            {
                lanes.Add(new(
                    activeVoice.Id,
                    TemplateEventMidiTargets.Format(laneMidiTarget),
                    laneMidiTarget,
                    EventMappingChainId: mapping?.Steps.Id,
                    EventMappingTarget: TemplateEventMidiTargets.ToMappingTarget(laneMidiTarget)));
            }

            AddInitialStateEntries(activeVoice.InitialState);
            AddActiveSubVoiceInitialStateFields(activeVoice.InitialState);
            Raise(nameof(ActiveSubVoicePresetSummary)); Raise(nameof(ActiveSubVoiceInitialInstrumentFields));
            Raise(nameof(ActiveSubVoiceOtherInitialStateFields));
        }

        foreach (InstrumentRenderLane lane in lanes) RenderLanes.Add(lane);
        MidiValueTarget? selectedTarget = null;
        if (Selection.Primary is MidoraId selectedEventId
            && activeVoice?.Events.TryGetById(selectedEventId, out TemplateEvent? selectedEvent) == true
            && selectedEvent is not null)
        {
            MidiValueTarget[] selectedTargets = TemplateEventMidiTargets.Enumerate(selectedEvent).ToArray();
            if (selectedTargets.Length != 0)
            {
                selectedTarget = previousTarget is MidiValueTarget previous
                    && selectedTargets.Contains(previous)
                        ? previous
                        : selectedTargets[0];
            }
        }
        if (_explicitRenderLaneSelectionRevision != Selection.Revision)
        {
            _explicitRenderLaneTarget = null;
            _explicitRenderLaneSelectionRevision = -1;
        }
        MidiValueTarget? preferredTarget = _explicitRenderLaneTarget
            ?? previousTarget
            ?? selectedTarget;
        int preferredLane = preferredTarget is MidiValueTarget target
            ? lanes.FindIndex(item => item.Target == target)
            : -1;
        if (_explicitRenderLaneTarget is not null && preferredLane < 0)
        {
            _explicitRenderLaneTarget = null;
            _explicitRenderLaneSelectionRevision = -1;
            preferredTarget = selectedTarget ?? previousTarget;
            preferredLane = preferredTarget is MidiValueTarget fallbackTarget
                ? lanes.FindIndex(item => item.Target == fallbackTarget)
                : -1;
        }
        ActiveRenderLaneIndex = lanes.Count == 0
            ? -1
            : preferredLane >= 0
                ? preferredLane
                : Math.Clamp(ActiveRenderLaneIndex, 0, lanes.Count - 1);
        ActiveValueMinimum = 0;
        ActiveValueMaximum = 127;
        ActiveValueIntegral = true;
        _activeEditingValueOffset = 0;
        if (lanes.Count != 0)
        {
            InstrumentRenderLane activeLane = lanes[ActiveRenderLaneIndex];
            if (activeLane.Target is MidiValueTarget activeTarget)
            {
                (ActiveValueMinimum, ActiveValueMaximum) = MidiValueRange(activeTarget);
                _activeEditingValueOffset = MidiEditingValueDomain.Offset(activeTarget);
            }
        }
        Raise(nameof(ActiveValueAxisMinimum));
        Raise(nameof(ActiveValueAxisMaximum));
        ITimelineRenderItemSource? activeEventSource = activeVoice is null
            || templateSnapshot is null
            || lanes.Count == 0
            || lanes[ActiveRenderLaneIndex].Target is not MidiValueTarget activeEventTarget
                ? null
                : new PagedTemplateEventLaneTimelineItemSource(
                    activeVoice,
                    templateSnapshot,
                    activeEventTarget,
                    selectedIds,
                    Selection.Primary);
        (TimelineRenderItem[] activeEvents, ITimelineRenderItemSource? retainedActiveEventSource) =
            TimelinePresentationPaging.Adapt(activeEventSource);
        string projectionSuffix = activeVoice?.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        ITimelineRenderItemSource? noteSource = activeVoice is null || templateSnapshot is null
            ? null
            : new PagedTemplateNoteTimelineItemSource(
                activeVoice,
                TemplateNoteTimelineProjection.Notes,
                selectedIds,
                Selection.Primary,
                templateSnapshot);
        (TimelineRenderItem[] noteItems, ITimelineRenderItemSource? retainedNoteSource) =
            TimelinePresentationPaging.Adapt(noteSource);
        ITimelineRenderItemSource? velocitySource = activeVoice is null || templateSnapshot is null
            ? null
            : new PagedTemplateNoteTimelineItemSource(
                activeVoice,
                TemplateNoteTimelineProjection.Velocities,
                selectedIds,
                Selection.Primary,
                templateSnapshot);
        (TimelineRenderItem[] velocityItems, ITimelineRenderItemSource? retainedVelocitySource) =
            TimelinePresentationPaging.Adapt(velocitySource);
        SubVoiceNoteSnapshot = new(
            revision,
            $"instrument-notes:{instrument.Id.Value}:{projectionSuffix}",
            noteItems,
            Enumerable.Range(0, 128).Select(lane => MidiNoteName(127 - lane)).ToArray(),
            itemSource: retainedNoteSource);
        SubVoiceEventSnapshot = new(
            revision,
            $"instrument-events:{instrument.Id.Value}:{projectionSuffix}",
            activeEvents,
            lanes.Count == 0 ? [] : [lanes[ActiveRenderLaneIndex].Label],
            itemSource: retainedActiveEventSource,
            stepSignalSource: activeVoice is not null && templateSnapshot is not null && lanes.Count != 0
                && lanes[ActiveRenderLaneIndex].Target is { } signalTarget
                    ? EventStepSignalSources.Template(activeVoice.Id, templateSnapshot, signalTarget, instrument.TemplateLengthTicks) : null);
        SubVoiceVelocitySnapshot = new(
            revision,
            $"instrument-velocities:{instrument.Id.Value}:{projectionSuffix}",
            velocityItems,
            ["Velocity"],
            itemSource: retainedVelocitySource);
        SubVoiceSnapshot = new(
            revision,
            $"instrument-overview:{instrument.Id.Value}:{projectionSuffix}",
            [],
            overviewSource: templateSnapshot is null
                ? null
                : new TemplateEventOverviewSource(templateSnapshot));

        void AddInitialStateEntries(MidiInitialState state)
        {
            Add("Bank MSB", state.BankMsb);
            Add("Bank LSB", state.BankLsb);
            Add("Program", state.Program);
            Add("Pitch Bend", state.PitchBend);
            Add("Pitch Bend Range Semitones", state.PitchBendRangeSemitones);
            Add("Pitch Bend Range Cents", state.PitchBendRangeCents);
            foreach ((int number, int value) in state.Controllers.OrderBy(item => item.Key))
                InitialStateEntries.Add(new(MidiControlChangeCatalog.Format(number), MidiEditingValueDomain.ControllerDisplay(number, value).ToString(System.Globalization.CultureInfo.InvariantCulture), "SubVoice"));
            foreach ((int number, int value) in state.RegisteredParameters.OrderBy(item => item.Key))
                InitialStateEntries.Add(new($"RPN {number}", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "SubVoice"));
            foreach ((int number, int value) in state.NonRegisteredParameters.OrderBy(item => item.Key))
                InitialStateEntries.Add(new($"NRPN {number}", value.ToString(System.Globalization.CultureInfo.InvariantCulture), "SubVoice"));

            void Add(string target, int? value)
            {
                InitialStateEntries.Add(new(
                    target,
                    value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Not set",
                    "SubVoice"));
            }
        }

        void AddInstrumentInitialStateFields(MidiInitialState state)
        {
            Add("bankMsb", "BANK MSB", state.BankMsb);
            Add("bankLsb", "BANK LSB", state.BankLsb);
            Add("program", "PROGRAM (0–127)", state.Program);
            Add("pitchBend", "PITCH BEND", state.PitchBend);
            Add("pitchRangeSemitones", "PITCH RANGE SEMITONES", state.PitchBendRangeSemitones);
            Add("pitchRangeCents", "PITCH RANGE CENTS", state.PitchBendRangeCents);
            foreach ((int number, int value) in state.Controllers.OrderBy(item => item.Key))
                Add($"cc.{number}", MidiControlChangeCatalog.Format(number), MidiEditingValueDomain.ControllerDisplay(number, value));
            foreach ((int number, int value) in state.RegisteredParameters.OrderBy(item => item.Key))
                Add($"rpn.{number}", $"RPN {number}", value);
            foreach ((int number, int value) in state.NonRegisteredParameters.OrderBy(item => item.Key))
                Add($"nrpn.{number}", $"NRPN {number}", value);

            void Add(string key, string label, int? value) =>
                InstrumentInitialStateFields.Add(new(
                    key,
                    label,
                    value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        }

        void AddActiveSubVoiceInitialStateFields(MidiInitialState state)
        {
            Add("bankMsb", "BANK MSB", state.BankMsb);
            Add("bankLsb", "BANK LSB", state.BankLsb);
            Add("program", "PROGRAM (0–127)", state.Program);
            Add("pitchBend", "PITCH BEND", state.PitchBend);
            Add("pitchRangeSemitones", "PITCH RANGE SEMITONES", state.PitchBendRangeSemitones);
            Add("pitchRangeCents", "PITCH RANGE CENTS", state.PitchBendRangeCents);
            foreach ((int number, int value) in state.Controllers.OrderBy(item => item.Key))
                Add($"cc.{number}", MidiControlChangeCatalog.Format(number), MidiEditingValueDomain.ControllerDisplay(number, value));
            foreach ((int number, int value) in state.RegisteredParameters.OrderBy(item => item.Key))
                Add($"rpn.{number}", $"RPN {number}", value);
            foreach ((int number, int value) in state.NonRegisteredParameters.OrderBy(item => item.Key))
                Add($"nrpn.{number}", $"NRPN {number}", value);

            void Add(string key, string label, int? value) =>
                ActiveSubVoiceInitialStateFields.Add(new(
                    key,
                    label,
                    value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        }

        void AddMappingChain(MappingChain chain, string owner, bool canDelete)
        {
            MappingChains.Add(new(chain.Id, owner, chain.Count, canDelete));
            for (int index = 0; index < chain.Count; index++)
            {
                ValueMappingStep step = chain[index];
                MappingSteps.Add(new(
                    step.Id,
                    chain.Id,
                    owner,
                    index,
                    $"{step.Source} · {step.Operation}{(step.IsEnabled ? string.Empty : " · Disabled")}"));
            }
        }
    }

    private sealed record SubVoiceEditorSettings(
        TimelineEditorSettings Piano,
        TimelineEditorSettings EventLane);

    public override void CancelBackgroundPresentationWork()
    {
        base.CancelBackgroundPresentationWork();
        if (!_subVoiceTargetDiscoveryCancellation.IsCancellationRequested)
            _subVoiceTargetDiscoveryCancellation.Cancel();
        _subVoiceTargetDiscoveryGeneration++;
    }

    protected override void OnPresentationResumed()
    {
        _subVoiceTargetDiscoveryCancellation.Dispose();
        _subVoiceTargetDiscoveryCancellation = new();
        if (_subVoiceTargetDiscoveryProject is { } project
            && _subVoiceTargetDiscoveryVoice is { } voice
            && _subVoiceTargetDiscoverySnapshot is { } snapshot)
            ScheduleSubVoiceEventTargetIndex(project, _subVoiceTargetDiscoveryRevision, voice, snapshot);
        base.OnPresentationResumed();
    }

    protected override void DisposeCore()
    {
        _subVoiceTargetDiscoveryCancellation.Dispose();
        _subVoiceTargetDiscoveryProject = null;
        _subVoiceTargetDiscoveryVoice = null;
        _subVoiceTargetDiscoverySnapshot = null;
        _subVoiceEditorSettings.Clear();
        _subVoiceEventTargetCache.Clear();
        SubVoiceSnapshot = null;
        SubVoiceNoteSnapshot = null;
        SubVoiceEventSnapshot = null;
        SubVoiceVelocitySnapshot = null;
        SubVoices.Clear();
        Parameters.Clear();
        MappingFunctions.Clear();
        ParameterMappings.Clear();
        Envelopes.Clear();
        MappingChains.Clear();
        MappingSteps.Clear();
        InitialStateEntries.Clear();
        ActiveSubVoiceInitialStateFields.Clear();
        InstrumentInitialStateFields.Clear();
        RenderLanes.Clear();
        base.DisposeCore();
    }

    private sealed record SubVoiceEventTargetCacheEntry(
        long Generation,
        TimelineTargetSummary<MidiValueTarget> Index);

    private void ScheduleSubVoiceEventTargetIndex(
        MidoraProject project,
        long revision,
        SubVoice voice,
        TemplateEventQuerySnapshot snapshot)
    {
        _subVoiceTargetDiscoveryProject = project;
        _subVoiceTargetDiscoveryRevision = revision;
        _subVoiceTargetDiscoveryVoice = voice;
        _subVoiceTargetDiscoverySnapshot = snapshot;
        if (IsDisposed || IsPresentationSuspended) return;
        if (_subVoiceEventTargetCache.TryGetValue(
                voice.Id,
                out SubVoiceEventTargetCacheEntry? cached)
            && cached.Generation == snapshot.Generation)
        {
            return;
        }

        CancellationToken cancellationToken = _subVoiceTargetDiscoveryCancellation.Token;
        long requestGeneration = _subVoiceTargetDiscoveryGeneration;
        MidoraId voiceId = voice.Id;
        long sourceGeneration = snapshot.Generation;
        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        _ = Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new TimelineTargetSummary<MidiValueTarget>(
                    snapshot.DiscoveryCounts.Where(static pair => TemplateEventMidiTargets.TryDecodeDiscoveryKey(pair.Key, out _))
                        .ToDictionary(static pair => { TemplateEventMidiTargets.TryDecodeDiscoveryKey(pair.Key, out var target); return target; },
                            static pair => pair.Value),
                    Comparer<MidiValueTarget>.Create(static (left, right) =>
                    {
                        int kind = left.Kind.CompareTo(right.Kind);
                        return kind != 0 ? kind : left.Number.CompareTo(right.Number);
                    }));
            },
            cancellationToken).ContinueWith(
                task =>
                {
                    if (task.IsCanceled || task.IsFaulted || cancellationToken.IsCancellationRequested) return;
                    WorkspacePresentationDispatch.Post(dispatcher, cancellationToken,
                        () =>
                        {
                            if (IsDisposed || IsPresentationSuspended || cancellationToken.IsCancellationRequested
                                || requestGeneration != _subVoiceTargetDiscoveryGeneration
                                || ObjectId is null
                                || voice.Events.Generation != sourceGeneration)
                            {
                                return;
                            }
                            _subVoiceEventTargetCache[voiceId] = new(
                                sourceGeneration,
                                task.Result);
                            Rebuild(project, revision);
                        });
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public InstrumentRenderLane? GetRenderLane(int lane) =>
        lane >= 0 && lane < RenderLanes.Count ? RenderLanes[lane] : null;

    private static SubVoice? ResolveActiveSubVoice(
        EventInstrument instrument,
        MidoraId? primary,
        MidoraId? previous)
    {
        if (primary is MidoraId selected)
        {
            SubVoice? direct = instrument.SubVoices.FirstOrDefault(item => item.Id == selected);
            if (direct is not null) return direct;
            SubVoice? owner = instrument.SubVoices.FirstOrDefault(voice =>
                voice.Events.TryGetById(selected, out _)
                || voice.Curves.Any(curve =>
                    curve.Id == selected
                    || curve.Points.TryGetById(selected, out _)));
            if (owner is not null) return owner;
        }
        return previous is MidoraId previousId
            ? instrument.SubVoices.FirstOrDefault(item => item.Id == previousId) ?? instrument.SubVoices.FirstOrDefault()
            : instrument.SubVoices.FirstOrDefault();
    }

    internal static (double Minimum, double Maximum) MidiValueRange(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.PitchBend => (-8192, 8191),
        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => (0, 16383),
        MidiValueKind.PitchBendRangeCents => (0, 99),
        _ => (0, 127)
    };

    internal static (double Minimum, double Maximum) MidiEditingRange(MidiValueTarget target)
    {
        var (minimum, maximum) = MidiValueRange(target);
        int offset = MidiEditingValueDomain.Offset(target);
        return (minimum + offset, maximum + offset);
    }

    internal static double DenormalizeMidiValue(MidiValueTarget target, double normalized)
    {
        (double minimum, double maximum) = MidiValueRange(target);
        return Math.Round(minimum + Math.Clamp(normalized, 0, 1) * (maximum - minimum), MidpointRounding.AwayFromZero);
    }

    private static double NormalizeMidiValue(MidiValueTarget target, double value)
    {
        (double minimum, double maximum) = MidiValueRange(target);
        return Math.Clamp((value - minimum) / (maximum - minimum), 0, 1);
    }

    private static string FormatTarget(MidiValueTarget target) => TemplateEventMidiTargets.Format(target);

    private static string FormatEventMappingTarget(TemplateEventMappingTarget target)
    {
        string eventName = target.EventKind switch
        {
            TemplateEventKind.Note => "Note",
            TemplateEventKind.ControlChange => MidiControlChangeCatalog.Format(target.EventNumber),
            TemplateEventKind.Bank => "Bank",
            TemplateEventKind.Program => "Program",
            TemplateEventKind.PitchBend => "Pitch Bend",
            TemplateEventKind.RegisteredParameter => $"RPN {target.EventNumber}",
            TemplateEventKind.NonRegisteredParameter => $"NRPN {target.EventNumber}",
            TemplateEventKind.PitchBendRange => "Pitch Bend Range",
            _ => target.EventKind.ToString()
        };
        string parameter = (target.EventKind, target.Parameter) switch
        {
            (TemplateEventKind.Note, TemplateEventMappingParameter.Number) => "Number",
            (TemplateEventKind.Note, TemplateEventMappingParameter.Value) => "Velocity",
            (TemplateEventKind.Bank, TemplateEventMappingParameter.Value) => "MSB",
            (TemplateEventKind.Bank, TemplateEventMappingParameter.SecondaryValue) => "LSB",
            (TemplateEventKind.PitchBendRange, TemplateEventMappingParameter.Value) => "Semitones",
            (TemplateEventKind.PitchBendRange, TemplateEventMappingParameter.SecondaryValue) => "Cents",
            _ => "Value"
        };
        return $"{eventName} · {parameter}";
    }

    private static string MidiNoteName(int note) => TimelineWorkspaceViewModel.MidiNoteName(note);
}

public sealed class SettingsWorkspaceViewModel()
    : WorkspaceViewModel(
        WorkspaceKey.ForType(WorkspaceKind.ProjectSettings),
        "Project Settings")
{
    private string _projectName = string.Empty;
    private string _projectVersion = string.Empty;
    private string _author = string.Empty;
    private long _compiledNoteOnCount;
    private long _compiledMidiEventCount;
    private long _totalEditingTimeMilliseconds;

    public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }
    public string ProjectVersion { get => _projectVersion; private set => Set(ref _projectVersion, value); }
    public string Author { get => _author; private set => Set(ref _author, value); }
    public ObservableCollection<PropertyField> GeneralFields { get; } = [];
    public ObservableCollection<PropertyField> InitialStateFields { get; } = [];
    public ObservableCollection<PropertyField> ResetDefaultFields { get; } = [];

    public override void Rebuild(MidoraProject project, long revision)
    {
        ProjectName = project.Metadata.ProjectName;
        ProjectVersion = project.Metadata.ProjectVersion;
        Author = project.Metadata.AuthorOrTeam;
        Replace(GeneralFields,
            new("settings.project.name", "PROJECT NAME", project.Metadata.ProjectName),
            new("settings.project.version", "PROJECT VERSION", project.Metadata.ProjectVersion),
            new("settings.project.author", "AUTHOR / TEAM", project.Metadata.AuthorOrTeam),
            new("settings.project.originalWork", "ORIGINAL WORK", project.Metadata.OriginalWork),
            new("settings.project.copyright", "COPYRIGHT", project.Metadata.Copyright),
            new("settings.project.noteCount", "NOTES", _compiledNoteOnCount.ToString("N0"), false),
            new("settings.project.eventCount", "EVENTS", _compiledMidiEventCount.ToString("N0"), false),
            new("settings.project.totalWorkTime", "PROJECT WORK TIME", FormatDuration(_totalEditingTimeMilliseconds), false),
            new("settings.project.tpq", "TICKS PER QUARTER NOTE", project.TicksPerQuarterNote.ToString(), false));
        Replace(InitialStateFields, StateFields("settings.initial", project.GlobalInitialState));
        Replace(ResetDefaultFields, StateFields("settings.reset", project.GlobalResetDefaults));
    }

    public void UpdateRuntimeInformation(
        CompilationStatistics statistics,
        long totalEditingTimeMilliseconds)
    {
        if (totalEditingTimeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalEditingTimeMilliseconds));
        }
        _compiledNoteOnCount = statistics.NoteOnEventCount;
        _compiledMidiEventCount = statistics.EventCount;
        _totalEditingTimeMilliseconds = totalEditingTimeMilliseconds;
        UpdateReadOnlyField("settings.project.noteCount", _compiledNoteOnCount.ToString("N0"));
        UpdateReadOnlyField("settings.project.eventCount", _compiledMidiEventCount.ToString("N0"));
        UpdateReadOnlyField(
            "settings.project.totalWorkTime",
            FormatDuration(_totalEditingTimeMilliseconds));
    }

    private void UpdateReadOnlyField(string key, string value)
    {
        PropertyField? field = GeneralFields.FirstOrDefault(item => item.Key == key);
        if (field is not null)
        {
            field.Value = value;
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        long totalSeconds = milliseconds / 1_000;
        long days = totalSeconds / 86_400;
        long hours = totalSeconds / 3_600 % 24;
        long minutes = totalSeconds / 60 % 60;
        long seconds = totalSeconds % 60;
        return days == 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{days:N0}d {hours:00}:{minutes:00}:{seconds:00}";
    }

    private static PropertyField[] StateFields(string prefix, MidiInitialState state)
    {
        List<PropertyField> fields =
        [
            new($"{prefix}.bankMsb", "BANK MSB", state.BankMsb?.ToString() ?? string.Empty),
            new($"{prefix}.bankLsb", "BANK LSB", state.BankLsb?.ToString() ?? string.Empty),
            new($"{prefix}.program", "PROGRAM (0–127)", state.Program?.ToString() ?? string.Empty),
            new($"{prefix}.pitchBend", "PITCH BEND", state.PitchBend?.ToString() ?? string.Empty),
            new($"{prefix}.pitchRangeSemitones", "PITCH RANGE SEMITONES", state.PitchBendRangeSemitones?.ToString() ?? string.Empty),
            new($"{prefix}.pitchRangeCents", "PITCH RANGE CENTS", state.PitchBendRangeCents?.ToString() ?? string.Empty)
        ];
        fields.AddRange(state.Controllers.OrderBy(item => item.Key)
            .Select(item => new PropertyField(
                $"{prefix}.cc.{item.Key}",
                MidiControlChangeCatalog.Format(item.Key),
                MidiEditingValueDomain.ControllerDisplay(item.Key, item.Value).ToString())));
        fields.AddRange(state.RegisteredParameters.OrderBy(item => item.Key)
            .Select(item => new PropertyField($"{prefix}.rpn.{item.Key}", $"RPN {item.Key}", item.Value.ToString())));
        fields.AddRange(state.NonRegisteredParameters.OrderBy(item => item.Key)
            .Select(item => new PropertyField($"{prefix}.nrpn.{item.Key}", $"NRPN {item.Key}", item.Value.ToString())));
        return fields.ToArray();
    }

    private static PropertyField Choice<T>(
        string key,
        string label,
        T value,
        IReadOnlyList<string>? options = null)
        where T : notnull => new(
            key,
            label,
            value.ToString() ?? string.Empty,
            options: options ?? (typeof(T).IsEnum ? Enum.GetNames(typeof(T)) : []));

    private static void Replace(ObservableCollection<PropertyField> target, params PropertyField[] fields)
    {
        target.Clear();
        foreach (PropertyField field in fields) target.Add(field);
    }
}

public sealed class DiagnosticsWorkspaceViewModel()
    : WorkspaceViewModel(
        WorkspaceKey.ForType(WorkspaceKind.Diagnostics),
        "Diagnostics")
{
    private const int SynchronousFilterLimit = 4_096;
    private readonly Dispatcher _diagnosticDispatcher = Dispatcher.CurrentDispatcher;
    private VirtualDiagnosticRows _allDiagnostics = VirtualDiagnosticRows.Empty;
    private VirtualDiagnosticRows _diagnostics = VirtualDiagnosticRows.Empty;
    private CancellationTokenSource _filterCancellation = new();
    private long _filterGeneration;
    private bool _isFiltering;
    private bool _diagnosticFilterDirty = true;
    private string? _filterError;
    private string _diagnosticPageText = "1";
    private string _diagnosticPageError = string.Empty;
    private CompressedMidoraIdSet _workspaceScopeIds = CompressedMidoraIdSet.Empty;
    private CompressedMidoraIdSet _selectionScopeIds = CompressedMidoraIdSet.Empty;
    private string _searchText = string.Empty;
    private string _severityFilter = "All severities";
    private string _statusFilter = "Active";
    private string _scopeFilter = "Whole Project";
    public IReadOnlyList<DiagnosticRow> Diagnostics => _diagnostics;
    public bool IsFiltering => _isFiltering;
    public bool IsDiagnosticPagingVisible => _diagnostics.IsPaged;
    public bool CanGoToDiagnosticPage => IsDiagnosticPagingVisible && !_isFiltering;
    public bool CanPreviousDiagnosticPage => CanGoToDiagnosticPage && _diagnostics.PageIndex > 0;
    public bool CanNextDiagnosticPage => CanGoToDiagnosticPage && _diagnostics.PageIndex < _diagnostics.PageCount - 1;
    public string DiagnosticPageSummary => $"Page {_diagnostics.PageIndex + 1:N0} of {_diagnostics.PageCount:N0}";
    public string DiagnosticPageError => _diagnosticPageError;
    public string DiagnosticPageText
    {
        get => _diagnosticPageText;
        set => Set(ref _diagnosticPageText, value ?? string.Empty);
    }

    public bool GoToDiagnosticPage()
    {
        if (!CanGoToDiagnosticPage) return false;
        if (!long.TryParse(DiagnosticPageText, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long page)
            || page < 1 || page > _diagnostics.PageCount)
        {
            _diagnosticPageError = $"Enter a page number from 1 to {_diagnostics.PageCount:N0}.";
            Raise(nameof(DiagnosticPageError));
            return false;
        }
        PublishDiagnostics(_diagnostics.GetPage(page - 1));
        return true;
    }

    public void MoveDiagnosticPage(bool next)
    {
        if (next ? !CanNextDiagnosticPage : !CanPreviousDiagnosticPage) return;
        PublishDiagnostics(_diagnostics.GetPage(_diagnostics.PageIndex + (next ? 1 : -1)));
    }
    public IReadOnlyList<string> SeverityFilters { get; } = ["All severities", "Error", "Warning", "Information"];
    public IReadOnlyList<string> StatusFilters { get; } =
        ["All statuses", "Active", "Prior Result", "Resolved", "Runtime History"];
    public IReadOnlyList<string> ScopeFilters { get; } = ["Whole Project", "Current Workspace", "Current Selection", "Current Task"];
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? string.Empty)) ApplyFilter();
        }
    }
    public string SeverityFilter
    {
        get => _severityFilter;
        set { if (Set(ref _severityFilter, value ?? "All severities")) ApplyFilter(); }
    }
    public string StatusFilter
    {
        get => _statusFilter;
        set { if (Set(ref _statusFilter, value ?? "Active")) ApplyFilter(); }
    }
    public string ScopeFilter
    {
        get => _scopeFilter;
        set { if (Set(ref _scopeFilter, value ?? "Whole Project")) ApplyFilter(); }
    }
    public string Summary => _filterError is not null
        ? $"Diagnostic filter failed: {_filterError}"
        : _isFiltering
            ? $"Filtering {_allDiagnostics.TotalCount} diagnostic(s)..."
            : _diagnostics.IsPaged
                ? $"Rows {_diagnostics.StartOrdinal + 1:N0}–{_diagnostics.StartOrdinal + _diagnostics.Count:N0} of {_diagnostics.TotalCount:N0} matching diagnostics ({_allDiagnostics.TotalCount:N0} total)"
                : $"Showing {_diagnostics.TotalCount} of {_allDiagnostics.TotalCount} diagnostic(s)";

    public override void Rebuild(MidoraProject project, long revision)
    {
    }

    public void Replace(IEnumerable<DiagnosticRow> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        _allDiagnostics = diagnostics as VirtualDiagnosticRows
            ?? new VirtualDiagnosticRows(diagnostics.ToArray());
        // Retired results must not remain retained while a hidden Workspace waits.
        PublishDiagnostics(VirtualDiagnosticRows.Empty);
        ApplyFilter();
    }

    public void SetScope(WorkspaceViewModel? workspace)
    {
        _selectionScopeIds = workspace?.Selection.SharedIds ?? CompressedMidoraIdSet.Empty;
        _workspaceScopeIds = workspace?.ObjectId is MidoraId objectId
            ? _selectionScopeIds.Add(objectId) : _selectionScopeIds;
        if (ScopeFilter is "Current Workspace" or "Current Selection") ApplyFilter();
    }

    private void ApplyFilter()
    {
        _diagnosticFilterDirty = true;
        _filterCancellation.Cancel();
        _filterCancellation.Dispose();
        _filterCancellation = new();
        long generation = ++_filterGeneration;
        _filterError = null;
        _isFiltering = false;
        Raise(nameof(IsFiltering));
        RaiseDiagnosticPagingProperties();
        if (IsDisposed || IsPresentationSuspended)
        {
            Raise(nameof(Summary));
            return;
        }
        string search = SearchText.Trim();
        string? severity = SeverityFilter == "All severities" ? null
            : SeverityFilter == "Information" ? "Info" : SeverityFilter;
        string? status = StatusFilter == "All statuses" ? null : StatusFilter;
        IReadOnlySet<MidoraId>? scopeIds = ScopeFilter switch
        {
            "Current Workspace" => _workspaceScopeIds,
            "Current Selection" => _selectionScopeIds,
            "Current Task" => CompressedMidoraIdSet.Empty,
            _ => null
        };
        bool? uniformCurrent = _allDiagnostics.UniformIsCurrent;
        if (scopeIds is { Count: 0 }
            || uniformCurrent.HasValue && status is not null
                && status != (uniformCurrent.Value ? "Active" : "Prior Result"))
        {
            PublishDiagnostics(VirtualDiagnosticRows.Empty);
            return;
        }
        if (severity is null && search.Length == 0 && scopeIds is null
            && (status is null || uniformCurrent.HasValue))
        {
            PublishDiagnostics(_allDiagnostics);
            return;
        }
        bool Matches(DiagnosticRow item) =>
            (severity is null || string.Equals(item.Severity, severity, StringComparison.OrdinalIgnoreCase))
            && (status is null || string.Equals(item.Status, status, StringComparison.Ordinal))
            && (scopeIds is null || MatchesAny(item.SourceReference, scopeIds))
            && (search.Length == 0
                || item.Severity.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Code.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Message.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Source.Contains(search, StringComparison.CurrentCultureIgnoreCase));

        VirtualDiagnosticRows source = _allDiagnostics;
        CancellationToken token = _filterCancellation.Token;
        if (source.SourceRecordCount <= SynchronousFilterLimit)
        {
            PublishDiagnostics(source.Filter(Matches, token));
            return;
        }
        _isFiltering = true;
        Raise(nameof(IsFiltering));
        RaiseDiagnosticPagingProperties();
        Raise(nameof(Summary));
        _ = Task.Run(() => source.Filter(Matches, token), token).ContinueWith(task =>
        {
            if (task.IsCanceled || token.IsCancellationRequested) return;
            WorkspacePresentationDispatch.Post(_diagnosticDispatcher, token, () =>
            {
                if (IsDisposed || IsPresentationSuspended || token.IsCancellationRequested
                    || generation != _filterGeneration) return;
                _isFiltering = false;
                Raise(nameof(IsFiltering));
                RaiseDiagnosticPagingProperties();
                if (task.IsFaulted)
                {
                    _filterError = task.Exception?.GetBaseException().Message ?? "Unknown failure";
                    Raise(nameof(Summary));
                }
                else PublishDiagnostics(task.Result);
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void PublishDiagnostics(VirtualDiagnosticRows diagnostics)
    {
        _diagnosticFilterDirty = false;
        _diagnostics = diagnostics;
        _diagnosticPageError = string.Empty;
        DiagnosticPageText = (diagnostics.PageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Raise(nameof(Diagnostics));
        Raise(nameof(Summary));
        RaiseDiagnosticPagingProperties();
    }

    private void RaiseDiagnosticPagingProperties()
    {
        Raise(nameof(IsDiagnosticPagingVisible));
        Raise(nameof(CanGoToDiagnosticPage));
        Raise(nameof(CanPreviousDiagnosticPage));
        Raise(nameof(CanNextDiagnosticPage));
        Raise(nameof(DiagnosticPageSummary));
        Raise(nameof(DiagnosticPageError));
    }

    protected override void OnPresentationSuspended()
    {
        _diagnosticFilterDirty |= _isFiltering;
        _filterCancellation.Cancel();
        _filterGeneration++;
        _isFiltering = false;
        Raise(nameof(IsFiltering));
        RaiseDiagnosticPagingProperties();
        Raise(nameof(Summary));
        base.OnPresentationSuspended();
    }

    protected override void OnPresentationResumed()
    {
        if (_diagnosticFilterDirty) ApplyFilter();
        base.OnPresentationResumed();
    }

    protected override void DisposeCore()
    {
        _filterCancellation.Cancel();
        _filterCancellation.Dispose();
        _allDiagnostics = VirtualDiagnosticRows.Empty;
        _diagnostics = VirtualDiagnosticRows.Empty;
        _workspaceScopeIds = CompressedMidoraIdSet.Empty;
        _selectionScopeIds = CompressedMidoraIdSet.Empty;
        base.DisposeCore();
    }

    private static bool MatchesAny(SourceReference source, IReadOnlySet<MidoraId> ids)
    {
        if (ids.Count == 0) return false;
        return ids.Contains(source.TrackId)
            || ids.Contains(source.SegmentId)
            || ids.Contains(source.LogicalNoteId)
            || ids.Contains(source.EventInstrumentId)
            || ids.Contains(source.SubVoiceId)
            || ids.Contains(source.SourceEventId)
            || ids.Contains(source.LogicalParameterId)
            || ids.Contains(source.LogicalParameterMappingId)
            || ids.Contains(source.MappingStepId)
            || ids.Contains(source.MappingFunctionId)
            || ids.Contains(source.ValueCurveId)
            || ids.Contains(source.EnvelopeId);
    }
}

public static class DiagnosticProjection
{
    public static DiagnosticRow FromCompiler(
        CompilerDiagnostic diagnostic,
        bool isCurrent = true) => new(
        diagnostic.Severity.ToString(),
        "Compile",
        diagnostic.Code,
        diagnostic.Message,
        SourceText(diagnostic.Source),
        isCurrent,
        diagnostic.Source);

    private static string SourceText(SourceReference source)
    {
        if (source.LogicalNoteId != default) return "Logical Note";
        if (source.SegmentId != default) return "Segment";
        if (source.TrackId != default) return "Logical Track";
        if (source.SubVoiceId != default) return "SubVoice";
        if (source.EventInstrumentId != default) return "Event Instrument";
        if (source.MappingFunctionId != default) return "Mapping Function";
        return source.Tick >= 0 ? $"Tick {source.Tick}" : "Project";
    }
}
