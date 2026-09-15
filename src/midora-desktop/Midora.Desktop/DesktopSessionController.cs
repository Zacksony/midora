using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Threading;
using Midora.Application;
using Midora.Audio;
using Midora.Compiler;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.Persistence;
using Midora.Playback;
using Midora.MidiExport;
using Midora.AudioRender;
using Midora.Audio.Bass;
using Midora.Mapping.Contract.V2;
using Midora.Playback.BassWasapi;
using Midora.Midi;

namespace Midora.Desktop;

public sealed partial class DesktopSessionController : ObservableObject, IAsyncDisposable
{
    [Flags]
    private enum ModelRefreshKind
    {
        None = 0,
        History = 1 << 0,
        Content = 1 << 1,
        Compilation = 1 << 2,
        Playback = 1 << 3
    }

    private static readonly string[] WelcomeHeadlines =
    [
        "What will you create?",
        "Let's make some noise.",
        "Ready when you are.",
        "Let the notes begin.",
        "Make something impossible.",
        "Let's dance!",
        "Your next score starts here.",
        "Turn ideas into sound.",
        "Start with a single note.",
        "Make the silence interesting.",
        "Compose without limits.",
        "Something extraordinary starts here.",
        "Time to bend some notes.",
        "Let's build a wall of sound.",
        "Your orchestra is waiting.",
        "Give the silence a melody.",
        "Ready to make history?",
        "Paint with sound.",
        "One note can start everything.",
        "Let's write something unforgettable.",
        "How many notes is too many?",
        "Make every tick count.",
        "Bring the score to life.",
        "Let's break the note counter.",
        "Millions of notes? Why not.",
        "Make the CPU sing.",
        "Push the score beyond reason.",
        "More notes. Still not enough.",
        "Turn density into art.",
        "Build a storm, one tick at a time.",
        "Ready for something absurd?",
        "Follow the melody.",
        "Write what words cannot say.",
        "Find the rhythm.",
        "Let the music take shape.",
        "A new song begins here.",
        "Create something worth replaying.",
        "Give your ideas a voice.",
        "Where will the music go?",
        "Let's turn the timeline black.",
        "One million notes is only the beginning.",
        "How dense can a melody become?",
        "Fill every tick.",
        "Make the renderer earn its keep.",
        "Compose beyond human limits.",
        "Start where inspiration leads.",
        "Every melody needs a first note.",
        "Shape the sound in your head.",
        "Let the next idea surprise you.",
        "Write the music only you can hear."
    ];

    private readonly object _modelRefreshGate = new();
    private ModelRefreshKind _pendingModelRefreshKinds;
    private ProjectChangeSet? _pendingContentChanges;
    private ProjectContext? _pendingModelRefreshContext;
    private bool _modelRefreshScheduled;
    private int _activeModelRefreshPasses;
    private TaskCompletionSource<bool>? _modelRefreshIdleCompletion;
    private long? _pendingSelectionRestoreStateId;
    private long? _pendingSelectionHistoryCaptureStateId;
    private bool _workspaceSelectionHistoryPruneRequested;
    private string? _projectTreeStructureStamp;
    private long _compilerErrorCount;
    private long _compilerWarningCount;
    private readonly MidoraProjectPackageV1 _packages =
        new(MidoraSoftwareVersion.InformationalVersion, instrumentChangeStorage: BoundedInstrumentChangeStorageLoader.Instance);
    private readonly ProjectCreationCoordinator _creation;
    private readonly ProjectOpenCoordinator _opening;
    private ApplicationPreferences _applicationPreferences =
        new ApplicationPreferencesStore().Load().Preferences;
    private InstrumentCatalogResolver _instrumentCatalogResolver = new(
        InstrumentCatalogState.Default,
        Array.Empty<InstrumentCatalogSoundFontEntry>());
    private readonly HashSet<MidoraId> _mutedTrackIds = [];
    private readonly HashSet<MidoraId> _soloTrackIds = [];
    private readonly HashSet<MidoraId> _mutedSharedGroupIds = [];
    private readonly HashSet<MidoraId> _soloSharedGroupIds = [];
    private readonly List<WorkspaceKey> _backNavigation = [];
    private readonly List<WorkspaceKey> _forwardNavigation = [];
    private readonly Dictionary<long, Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>>
        _workspaceSelectionHistory = [];
    private readonly Dictionary<WorkspaceKey, CachedWorkspaceSelectionBookmark>
        _workspaceSelectionBookmarkCache = [];
    private readonly ConditionalWeakTable<StagedProjectEdit, PreparedWorkspaceSelectionPublication>
        _stagedWorkspaceSelectionProjections = new();
    private readonly Dispatcher? _uiDispatcher =
        SynchronizationContext.Current is DispatcherSynchronizationContext
            ? Dispatcher.FromThread(Thread.CurrentThread)
            : null;
    private ProjectContext? _context;
    private BassWasapiChildPlaybackBackend? _preparedPlaybackBackend;
    private WorkspaceViewModel? _activeWorkspace;
    private WorkspaceViewModel? _diagnosticScopeWorkspace;
    private long _revision;
    private string? _notice;
    private DiagnosticRow? _selectedDiagnostic;
    private ProjectTimeSignatureMap? _timeSignatureMap;
    private string _projectTreeSearchText = string.Empty;
    private bool _isNavigatingHistory;
    private string? _statusMessage;
    private string? _statusMessageDetails;
    private string? _statusMessageDetailsTitle;
    private bool _statusMessageIsError;
    private bool _isPlaybackStartPending;
    private long _displayCurrentTick;
    private TempoChange[] _orderedTempoChanges = [];
    private int _activeTempoIndex = -1;
    private long _tempoLookupTick = -1;
    private string _tempoText = "— BPM";
    private DesktopTaskViewModel? _foregroundTask;

    internal long ModelRefreshPassCount { get; private set; }
    internal long WorkspaceRebuildCount { get; private set; }
    internal long ProjectTreeRefreshCount { get; private set; }
    internal long DiagnosticRefreshCount { get; private set; }
    internal long WorkspaceSelectionRefreshCount { get; private set; }
    internal int WorkspaceSelectionHistoryStateCount => _workspaceSelectionHistory.Count;

    public DesktopSessionController()
    {
        _creation = new(_packages);
        _opening = new(_packages);
        // UI collection notifications publish an immutable roster for Onion refreshes.
        // A compile completion can overlap tab creation in a headless host; bindings
        // can also remove a tab re-entrantly during a presentation notification.
        Workspaces.CollectionChanged += (_, _) => PublishOnionWorkspaceRoster();
    }

    public bool HasProject => _context is not null;
    public string WelcomeHeadline { get; } =
        WelcomeHeadlines[Random.Shared.Next(WelcomeHeadlines.Length)];
    public bool IsForegroundTaskRunning => _foregroundTask?.IsRunning == true;
    public DesktopTaskViewModel? ActiveForegroundTask => IsForegroundTaskRunning
        ? _foregroundTask
        : null;
    public bool IsMainWindowTaskLocked => ActiveForegroundTask?.LockLevel >= DesktopTaskLockLevel.MainWindow;
    public bool IsFullApplicationTaskLocked => ActiveForegroundTask?.LockLevel >= DesktopTaskLockLevel.FullApplication;
    public bool CanEditProject => HasProject && !IsForegroundTaskRunning && !IsPlaybackActive;
    public bool CanStartForegroundTask => !IsForegroundTaskRunning && !IsPlaybackActive;
    public bool CanRunProjectTask => HasProject && CanStartForegroundTask;
    public bool HasDamagedProjectObjects => Project is not null
        && (Project.DamagedEventInstruments.Count != 0
            || Project.DamagedLogicalTracks.Count != 0
            || Project.DamagedMidiChannelRoots.Count != 0
            || Project.DamagedPureMidiTracks.Count != 0);
    public bool CanSaveProject => HasProject && !IsForegroundTaskRunning && !HasDamagedProjectObjects;
    public bool CanUseContextMenus => !IsMainWindowTaskLocked;
    public bool CanNavigateBack => _backNavigation.Any(key => Workspaces.Any(item => item.Key == key));
    public bool CanNavigateForward => _forwardNavigation.Any(key => Workspaces.Any(item => item.Key == key));
    public ProjectDocumentSession? Document => _context?.Document;
    public MidoraProject? Project => Document?.Project;
    public ProjectPersistenceCoordinator? Persistence => _context?.Persistence;
    public string WindowTitle => _context is null
        ? "Midora"
        : $"Midora — {ProjectDisplayName}{(Document!.IsModified ? " *" : string.Empty)}";
    public string ProjectDisplayName
    {
        get
        {
            if (Project is null) return "No Project";
            if (!string.IsNullOrWhiteSpace(Project.Metadata.ProjectName))
            {
                return Project.Metadata.ProjectName;
            }
            return Persistence?.CurrentProjectPath is string path
                ? Path.GetFileNameWithoutExtension(path)
                : "Untitled Project";
        }
    }
    public string TitleBarProjectDisplayName => _context is null
        ? "No Project"
        : $"{ProjectDisplayName}{(Document!.IsModified ? " *" : string.Empty)}";
    public string ProjectState => _context is null
        ? "No Project"
        : Persistence?.CurrentProjectPath is null
            ? Document!.IsModified ? "Modified · Unsaved" : "Unsaved"
            : Document!.IsModified ? "Modified" : "Saved";
    public string CompileState => _context is null
        ? "Not Compiled"
        : _context.Compilation.CompilationState switch
        {
            ProjectCompilationState.NotCompiled => "Not Compiled",
            ProjectCompilationState.Outdated => "Compile Result Outdated",
            ProjectCompilationState.Compiling => "Compiling",
            ProjectCompilationState.Succeeded => "Compile Succeeded",
            ProjectCompilationState.Failed => "Compile Failed",
            _ => "Not Compiled"
        };
    public string SoundFontState => _applicationPreferences.GetEnabledSoundFontPaths().Length switch
    {
        0 => "No SoundFonts Enabled",
        1 => "1 SoundFont Enabled",
        int count => $"{count} SoundFonts Enabled"
    };
    public bool HasEnabledSoundFonts =>
        _applicationPreferences.GetEnabledSoundFontPaths().Length != 0;
    public bool IsTrackMuted(MidoraId trackId) => _mutedTrackIds.Contains(trackId);
    public bool IsTrackSolo(MidoraId trackId) => _soloTrackIds.Contains(trackId);
    public bool IsSharedGroupMuted(MidoraId sharedGroupId) =>
        _mutedSharedGroupIds.Contains(sharedGroupId);
    public bool IsSharedGroupSolo(MidoraId sharedGroupId) =>
        _soloSharedGroupIds.Contains(sharedGroupId);
    public long CurrentTick => _displayCurrentTick;
    public string TempoText => _tempoText;
    public string PositionText
    {
        get
        {
            if (_timeSignatureMap is null) return "—";
            ProjectMusicalPosition position = _timeSignatureMap.GetPosition(Math.Max(0, CurrentTick));
            int tickDigits = Math.Max(
                1,
                (Project?.TicksPerQuarterNote ?? 1)
                    .ToString(CultureInfo.InvariantCulture)
                    .Length);
            string tickOffset = position.TickOffset.ToString(
                $"D{tickDigits}",
                CultureInfo.InvariantCulture);
            return $"{position.Bar:D4} : {position.Beat:D2} : {tickOffset}";
        }
    }
    public PlaybackState PlaybackState => _context?.Playback?.State ?? PlaybackState.Stopped;
    public bool IsBuffering => PlaybackState == PlaybackState.Buffering;
    public string PlaybackStatusText
    {
        get
        {
            if (!IsBuffering)
            {
                return PlaybackState.ToString();
            }
            double? progress = _context?.Playback?.BufferingProgress;
            return progress.HasValue
                ? $"Buffering ({Math.Clamp((int)Math.Floor(progress.Value * 100d), 0, 100)}%)"
                : "Buffering";
        }
    }
    public bool IsPlaybackActive => PlaybackState is PlaybackState.Preparing
        or PlaybackState.Playing
        or PlaybackState.Buffering
        or PlaybackState.Stopping;
    public bool IsLoopEnabled => _context?.Playback?.LoopRange is not null;
    public bool CanPlayback => _context?.Playback is not null
        && _context.Compilation.EffectiveSoundFontPaths.Count != 0
        && _context.Compilation.CompilationState is not ProjectCompilationState.Failed
        && !_isPlaybackStartPending
        && !IsPlaybackActive;
    public bool CanTogglePlayback => IsPlaybackActive || CanPlayback;
    public string PrimaryTransportAction => IsPlaybackActive ? "Stop" : "Play";
    public string PrimaryTransportToolTip => IsPlaybackActive
        ? "Stop"
        : PlaybackUnavailableReason ?? "Play";
    public bool CanPreview => _context?.Tasks is not null
        && _context.Compilation.EffectiveSoundFontPaths.Count != 0
        && !IsPlaybackActive
        && !IsForegroundTaskRunning;
    public string? PlaybackUnavailableReason
    {
        get
        {
            if (_context is null) return null;
            return _context.PlaybackUnavailableReason
                ?? (_context.Compilation.EffectiveSoundFontPaths.Count == 0
                    ? "Enable at least one application SoundFont in Preferences."
                    : _context.Compilation.CompilationState == ProjectCompilationState.Failed
                        ? "The current canonical compilation is not consumable."
                        : null);
        }
    }
    public long ErrorCount => _compilerErrorCount;
    public long WarningCount => _compilerWarningCount;
    public bool HasErrors => ErrorCount > 0;
    public bool HasWarnings => WarningCount > 0;
    public string IssueSummary => $"{ErrorCount} Errors, {WarningCount} Warnings";
    public string? Notice
    {
        get => _notice;
        set
        {
            if (Set(ref _notice, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public string ProjectTreeSearchText
    {
        get => _projectTreeSearchText;
        set
        {
            string normalized = value ?? string.Empty;
            if (!Set(ref _projectTreeSearchText, normalized)) return;
            Raise(nameof(IsProjectTreeFiltered));
            RefreshProjectTree();
        }
    }
    public bool IsProjectTreeFiltered => !string.IsNullOrWhiteSpace(ProjectTreeSearchText);
    public DiagnosticRow? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        set => Set(ref _selectedDiagnostic, value);
    }
    public string ActivityText => ActiveForegroundTask?.Status
        ?? PlaybackState.ToString();

    public ObservableCollection<ProjectTreeNode> ProjectTree { get; } = [];
    public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];
    public IReadOnlyList<DiagnosticRow> CompilerDiagnostics { get; private set; } = VirtualDiagnosticRows.Empty;
    public TimelineEditorSettings ArrangementEditorSettings { get; } = new();
    public TimelineEditorSettings PianoRollEditorSettings { get; } = new();
    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (Set(ref _statusMessage, value)) Raise(nameof(HasStatusMessage));
        }
    }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public string? StatusMessageDetails
    {
        get => _statusMessageDetails;
        private set => Set(ref _statusMessageDetails, value);
    }
    public string? StatusMessageDetailsTitle
    {
        get => _statusMessageDetailsTitle;
        private set => Set(ref _statusMessageDetailsTitle, value);
    }
    public bool StatusMessageIsError
    {
        get => _statusMessageIsError;
        private set => Set(ref _statusMessageIsError, value);
    }

    public void SetStatusMessage(
        string? message,
        bool isError = false,
        string? details = null,
        string? detailsTitle = null)
    {
        StatusMessageIsError = isError;
        string? normalized = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        StatusMessageDetails = normalized is null || string.IsNullOrWhiteSpace(details)
            ? null
            : details.Trim();
        StatusMessageDetailsTitle = normalized is null || string.IsNullOrWhiteSpace(detailsTitle)
            ? null
            : detailsTitle.Trim();
        StatusMessage = normalized;
    }

    public WorkspaceViewModel? ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            WorkspaceViewModel? previous = _activeWorkspace;
            if (value?.IsDisposed == true || ReferenceEquals(previous, value)) return;
            previous?.SuspendPresentation();
            if (Set(ref _activeWorkspace, value))
            {
                StopEventInstrumentKeyboardPreviewOnWorkspaceExit(previous, value);
                value?.ResumePresentation();
                if (!_isNavigatingHistory
                    && previous is not null
                    && value is not null
                    && previous.Key != value.Key)
                {
                    _backNavigation.Add(previous.Key);
                    if (_backNavigation.Count > 100) _backNavigation.RemoveAt(0);
                    _forwardNavigation.Clear();
                }
                if (value is not DiagnosticsWorkspaceViewModel)
                {
                    _diagnosticScopeWorkspace = value;
                }
                Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                    .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
                RefreshOnionPresentations();
                Raise(nameof(CanNavigateBack));
                Raise(nameof(CanNavigateForward));
            }
        }
    }

    private void StopEventInstrumentKeyboardPreviewOnWorkspaceExit(
        WorkspaceViewModel? previous,
        WorkspaceViewModel? current)
    {
        if (previous is not InstrumentWorkspaceViewModel
            || ReferenceEquals(previous, current)
            || _context?.Tasks is not ApplicationTaskCoordinator)
        {
            return;
        }

        _ = StopEventInstrumentKeyboardPreviewForEditing();
    }

    public bool StopEventInstrumentKeyboardPreviewForEditing()
    {
        if (_context?.Tasks is not ApplicationTaskCoordinator tasks) return true;

        try
        {
            if (tasks.StopHeldEventInstrumentKeyboardPreview())
            {
                RefreshProperties();
            }
            return true;
        }
        catch (Exception exception)
        {
            SetStatusMessage(
                $"Stop Event Instrument keyboard Preview: {exception.Message}",
                isError: true);
            return false;
        }
    }

    public void NavigateBack() => NavigateWorkspaceHistory(_backNavigation, _forwardNavigation);

    public void NavigateForward() => NavigateWorkspaceHistory(_forwardNavigation, _backNavigation);

    private void NavigateWorkspaceHistory(List<WorkspaceKey> source, List<WorkspaceKey> destination)
    {
        while (source.Count != 0)
        {
            WorkspaceKey key = source[^1];
            source.RemoveAt(source.Count - 1);
            WorkspaceViewModel? target = Workspaces.FirstOrDefault(item => item.Key == key);
            if (target is null || ReferenceEquals(target, ActiveWorkspace)) continue;
            if (ActiveWorkspace is not null)
            {
                destination.Add(ActiveWorkspace.Key);
                if (destination.Count > 100) destination.RemoveAt(0);
            }
            _isNavigatingHistory = true;
            try { ActiveWorkspace = target; }
            finally { _isNavigatingHistory = false; }
            Raise(nameof(CanNavigateBack));
            Raise(nameof(CanNavigateForward));
            return;
        }
        Raise(nameof(CanNavigateBack));
        Raise(nameof(CanNavigateForward));
    }

    public async Task CreateProjectAsync(
        NewProjectCreationRequest request,
        CancellationToken cancellationToken = default)
    {
        NewProjectCreationResult result = await _creation.CreateAsync(request, cancellationToken);
        ProjectContext? next = null;
        try
        {
            next = ProjectContext.FromCreation(
                _packages,
                result,
                _applicationPreferences,
                TakePreparedPlaybackBackend());
            result = null!;
            await ActivateAsync(next);
            next = null;
        }
        finally
        {
            if (next is not null && !ReferenceEquals(_context, next))
            {
                await next.DisposeAsync();
            }
            if (result is not null)
            {
                await result.DisposeAsync();
            }
        }
    }

    public async Task OpenProjectAsync(
        string path,
        IProgress<ProjectOpenCandidateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ProjectOpenCandidate candidate = await _opening.OpenAsync(path, progress, cancellationToken);
        ProjectContext? next = null;
        try
        {
            next = ProjectContext.FromOpenCandidate(
                candidate,
                _applicationPreferences,
                TakePreparedPlaybackBackend());
            candidate = null!;
            await ActivateAsync(next);
            next = null;
        }
        finally
        {
            if (next is not null && !ReferenceEquals(_context, next))
            {
                await next.DisposeAsync();
            }
            if (candidate is not null)
            {
                await candidate.DisposeAsync();
            }
        }
    }

    public async Task<IReadOnlyList<MidiProjectImportDiagnostic>> ImportMidiAsNewProjectAsync(
        string path,
        IReadOnlyDictionary<byte, byte>? zeroBasedPortMapping = null,
        CancellationToken cancellationToken = default,
        IProgress<MidiProjectImportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        MidiProjectImportResult imported = await Task.Run(
            () => MidiProjectImportService.ImportFile(
                fullPath,
                Path.GetFileNameWithoutExtension(fullPath),
                zeroBasedPortMapping,
                cancellationToken,
                progress),
            cancellationToken);
        IReadOnlyList<MidiProjectImportDiagnostic> diagnostics =
            await AdoptMidiImportAsNewProjectAsync(
            imported,
            cancellationToken);
        progress?.Report(new(
            MidiProjectImportPhase.Completed,
            imported.Metrics?.ScannedEventCount ?? 0,
            imported.Metrics?.ScannedEventCount ?? 0,
            imported.Metrics?.SourceFileBytes ?? 0,
            imported.Metrics?.SourceFileBytes ?? 0,
            1));
        return diagnostics;
    }

    internal async Task<IReadOnlyList<MidiProjectImportDiagnostic>> ImportMidiBytesAsNewProjectAsync(
        byte[] file,
        string projectName,
        IReadOnlyDictionary<byte, byte>? zeroBasedPortMapping = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(projectName);
        MidiProjectImportResult imported = await Task.Run(
            () => MidiProjectImportService.Import(
                file,
                projectName,
                zeroBasedPortMapping,
                cancellationToken),
            cancellationToken);
        return await AdoptMidiImportAsNewProjectAsync(imported, cancellationToken);
    }

    internal async Task<IReadOnlyList<MidiProjectImportDiagnostic>> AdoptMidiImportAsNewProjectAsync(
        MidiProjectImportResult imported,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imported);
        cancellationToken.ThrowIfCancellationRequested();
        NewProjectCreationResult adopted;
        try
        {
            adopted = _creation.AdoptImportedProject(imported.Project);
        }
        catch
        {
            imported.Project.Dispose();
            throw;
        }
        ProjectContext? next = null;
        try
        {
            next = ProjectContext.FromCreation(
                _packages,
                adopted,
                _applicationPreferences,
                TakePreparedPlaybackBackend());
            adopted = null!;
            await ActivateAsync(next);
            next = null;
            return imported.Diagnostics;
        }
        finally
        {
            if (next is not null && !ReferenceEquals(_context, next)) await next.DisposeAsync();
            if (adopted is not null) await adopted.DisposeAsync();
        }
    }

    internal static async Task<byte[]> ReadMidiImportFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        await using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > StandardMidiFile.MaximumImportFileByteCount)
        {
            throw new MidoraMidiException(
                $"SMF input exceeds the bounded {StandardMidiFile.MaximumImportFileByteCount}-byte admission limit.");
        }
        byte[] result = GC.AllocateUninitializedArray<byte>((int)length);
        await stream.ReadExactlyAsync(result, cancellationToken);
        byte[] growthProbe = new byte[1];
        if (await stream.ReadAsync(growthProbe, cancellationToken) != 0)
        {
            throw new IOException(
                "The MIDI file changed while it was being read; retry the import after the writer has finished.");
        }
        return result;
    }

    public async Task SaveProjectAsync(
        string? firstSavePath = null,
        bool overwriteAuthorized = false,
        CancellationToken cancellationToken = default)
    {
        if (Persistence is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        await Task.Run(
            () => Persistence.SaveProjectAsync(
                firstSavePath,
                overwriteAuthorized,
                cancellationToken),
            cancellationToken);
        RefreshAll();
    }

    public async Task<string> UpgradeLegacyProjectInPlaceAsync(
        MidoraLegacyProjectUpgradePlanV3 plan,
        CancellationToken cancellationToken = default)
    {
        if (Persistence is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        MidoraProjectSaveResultV1 result = await Task.Run(
            () => Persistence.UpgradeLegacyProjectInPlaceAsync(plan, cancellationToken),
            cancellationToken);
        RefreshAll();
        return result.PermanentLegacyBackupPath
            ?? throw new InvalidOperationException(
                "The legacy upgrade completed without reporting its permanent backup.");
    }

    public Task<MidoraLegacyProjectUpgradePlanV3> PrepareLegacyProjectUpgradeAsync(
        CancellationToken cancellationToken = default)
    {
        if (Persistence is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        return Task.Run(
            () => Persistence.PrepareLegacyProjectUpgradeAsync(cancellationToken),
            cancellationToken);
    }

    public async Task SaveCopyAsync(
        string path,
        bool overwriteAuthorized,
        CancellationToken cancellationToken = default)
    {
        if (Persistence is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        await Task.Run(
            () => Persistence.SaveCopyAsync(path, overwriteAuthorized, cancellationToken),
            cancellationToken);
    }

    public PreparedDesktopMidiExport PrepareMidiExport(DesktopMidiExportOptions options)
    {
        if (_context is null || Project is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        _ = _context.Compilation.EnsureCurrentCompilationAsync().GetAwaiter().GetResult();
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        MidoraProjectFileInformationV1? information = Persistence?.FileInformation;
        return DesktopMidiExportService.Prepare(
            Project,
            Persistence?.CurrentProjectPath,
            information?.CreatedWithSoftwareVersion,
            information?.LastSavedWithSoftwareVersion,
            options);
    }

    public async Task<MidiExportTaskResult> ExecuteMidiExportAsync(
        PreparedDesktopMidiExport prepared,
        bool overwriteAuthorized,
        CancellationToken cancellationToken = default,
        IProgress<MidiExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_context is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        return await Task.Run(() => new MidiExportTaskRunner().ExecuteAsync(new()
        {
            Compilation = prepared.Compilation,
            OutputPlan = prepared.OutputPlan,
            Readme = prepared.Readme,
            OverwriteAuthorized = overwriteAuthorized
        }, cancellationToken, progress));
    }

    public void StartPlayback(long? cursorTick = null)
    {
        if (_context?.Tasks is null)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "The formal realtime playback backend is unavailable.");
        }
        CanonicalCompiledResult result = _context.Compilation
            .EnsureCurrentCompilationAsync()
            .GetAwaiter()
            .GetResult();
        if (!result.IsConsumable)
        {
            throw new InvalidOperationException(
                "The current canonical compilation is not consumable.");
        }
        _context.Tasks.StartMainPlayback(cursorTick);
        RefreshProperties();
    }

    public async Task StartPlaybackAsync(
        long? cursorTick = null,
        CancellationToken cancellationToken = default)
    {
        ProjectContext context = _context
            ?? throw new InvalidOperationException("No Project is open.");
        if (context.Tasks is null)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "The formal realtime playback backend is unavailable.");
        }
        if (_isPlaybackStartPending)
        {
            throw new InvalidOperationException("Playback preparation is already waiting for compilation.");
        }
        _isPlaybackStartPending = true;
        RefreshProperties();
        try
        {
            CanonicalCompiledResult result = await context.Compilation
                .EnsureCurrentCompilationAsync(cancellationToken);
            if (!ReferenceEquals(_context, context))
            {
                throw new OperationCanceledException("The Project changed while playback was preparing.");
            }
            if (!result.IsConsumable)
            {
                throw new InvalidOperationException(
                    "The current canonical compilation is not consumable.");
            }
            context.Tasks.StartMainPlayback(cursorTick);
        }
        finally
        {
            _isPlaybackStartPending = false;
            RefreshProperties();
        }
    }

    public void StopPlayback()
    {
        _context?.Tasks?.StopPlayback();
        RefreshProperties();
    }

    public void SetPlaybackCursor(long tick)
    {
        if (tick < 0) throw new ArgumentOutOfRangeException(nameof(tick));
        if (_context?.Playback is null)
        {
            throw new InvalidOperationException("No Project playback session is open.");
        }
        _context.Playback.Seek(tick);
        RefreshProperties();
    }

    public void SetLoopRange(TickRange? range)
    {
        if (_context?.Playback is null)
        {
            throw new InvalidOperationException("No Project playback session is open.");
        }
        _context.Playback.SetLoop(range);
        RefreshProperties();
    }

    public void SetTrackMuted(MidoraId trackId, bool muted)
    {
        SetTrackMonitoringState(trackId, muted, isSolo: false);
    }

    public void SetTrackSolo(MidoraId trackId, bool solo)
    {
        SetTrackMonitoringState(trackId, solo, isSolo: true);
    }

    public void SetSharedGroupMuted(MidoraId sharedGroupId, bool muted)
    {
        SetSharedGroupMonitoringState(sharedGroupId, muted, isSolo: false);
    }

    public void SetSharedGroupSolo(MidoraId sharedGroupId, bool solo)
    {
        SetSharedGroupMonitoringState(sharedGroupId, solo, isSolo: true);
    }

    public void ResetAllTrackMonitoringStates()
    {
        _context?.Playback?.ResetMonitoringStates();
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        _mutedSharedGroupIds.Clear();
        _soloSharedGroupIds.Clear();
        foreach (TimelineWorkspaceViewModel workspace in Workspaces
                     .OfType<TimelineWorkspaceViewModel>()
                     .Where(item => item.Mode == TimelineWorkspaceMode.Arrangement))
        {
            RefreshWorkspace(workspace);
        }
    }

    private void SetTrackMonitoringState(MidoraId trackId, bool enabled, bool isSolo)
    {
        if (Project is not MidoraProject project
            || (!project.Tracks.Any(track => track.Id == trackId)
                && !project.PureMidiTracks.Any(track => track.Id == trackId)))
        {
            throw new InvalidOperationException("The Arrangement Track no longer exists.");
        }
        HashSet<MidoraId> states = isSolo ? _soloTrackIds : _mutedTrackIds;
        bool wasEnabled = states.Contains(trackId);
        if (wasEnabled == enabled) return;
        if (enabled) states.Add(trackId);
        else states.Remove(trackId);
        try
        {
            if (isSolo) _context?.Playback?.SetTrackSolo(trackId, enabled);
            else _context?.Playback?.SetTrackMuted(trackId, enabled);
        }
        catch
        {
            if (wasEnabled) states.Add(trackId);
            else states.Remove(trackId);
            throw;
        }
        foreach (TimelineWorkspaceViewModel workspace in Workspaces
                     .OfType<TimelineWorkspaceViewModel>()
                     .Where(item => item.Mode == TimelineWorkspaceMode.Arrangement))
        {
            RefreshWorkspace(workspace);
        }
    }

    private void SetSharedGroupMonitoringState(
        MidoraId sharedGroupId,
        bool enabled,
        bool isSolo)
    {
        if (Project is not MidoraProject project
            || (!project.EventInstrumentUsages.Any(value => value.Id == sharedGroupId)
                && !project.MidiChannelRoots.Any(value => value.Id == sharedGroupId)))
        {
            throw new InvalidOperationException("The shared Track group no longer exists.");
        }
        HashSet<MidoraId> states = isSolo
            ? _soloSharedGroupIds
            : _mutedSharedGroupIds;
        bool wasEnabled = states.Contains(sharedGroupId);
        if (wasEnabled == enabled) return;
        if (enabled) states.Add(sharedGroupId);
        else states.Remove(sharedGroupId);
        try
        {
            if (isSolo) _context?.Playback?.SetSharedGroupSolo(sharedGroupId, enabled);
            else _context?.Playback?.SetSharedGroupMuted(sharedGroupId, enabled);
        }
        catch
        {
            if (wasEnabled) states.Add(sharedGroupId);
            else states.Remove(sharedGroupId);
            throw;
        }
        foreach (TimelineWorkspaceViewModel workspace in Workspaces
                     .OfType<TimelineWorkspaceViewModel>()
                     .Where(item => item.Mode == TimelineWorkspaceMode.Arrangement))
        {
            RefreshWorkspace(workspace);
        }
    }

    public void StartHeldEventInstrumentPreview(EventInstrumentPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_context?.Tasks is null)
        {
            throw new InvalidOperationException(
                _context?.PlaybackUnavailableReason ?? "Realtime Preview is unavailable.");
        }
        if (!CanPreview
            && !_context.Tasks.CanReplaceReleasedHeldEventInstrumentKeyboardPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        _context.Tasks.StartHeldEventInstrumentPreview(request);
        RefreshProperties();
    }

    public void BeginPitchAudition(int pitch, int velocity)
    {
        if (!CanPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        (_context?.Playback ?? throw new InvalidOperationException("Realtime Preview is unavailable."))
            .BeginPitchAudition(pitch, velocity);
    }

    public void EndPitchAudition() => _context?.Playback?.EndPitchAudition();

    public void StartHeldSegmentPitchRulerPreview(
        MidoraId trackId,
        MidoraId segmentId,
        int pitch,
        int velocity,
        decimal tempo)
    {
        if (!CanPreview)
        {
            throw new InvalidOperationException(
                PlaybackUnavailableReason ?? "Realtime Preview is currently locked.");
        }
        (_context?.Tasks ?? throw new InvalidOperationException("Realtime Preview is unavailable."))
            .StartHeldSegmentPitchRulerPreview(trackId, segmentId, pitch, velocity, tempo);
        RefreshProperties();
    }

    public Exception? TryStartHeldSegmentNotePreview(SegmentNotePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_context?.Tasks is null)
        {
            return new InvalidOperationException(
                _context?.PlaybackUnavailableReason ?? "Realtime Preview is unavailable.");
        }
        Exception? failure = _context.Tasks.TryStartHeldSegmentNotePreview(request);
        RefreshProperties();
        return failure;
    }

    public SegmentNotePlacementPreviewCompletion CompleteSegmentNotePlacement(
        long finalGateLengthTicks,
        IProjectEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Document is null)
        {
            throw new InvalidOperationException("No Project note-placement session is available.");
        }

        if (_context?.Tasks is null)
        {
            Document.Execute(command);
            RefreshProperties();
            return new SegmentNotePlacementPreviewCompletion(null, null);
        }

        SegmentNotePlacementPreviewCompletion result = _context.Tasks.CompleteSegmentNotePlacement(
            finalGateLengthTicks,
            () => Document.Execute(command));
        RefreshProperties();
        return result;
    }

    public void EndHeldPreviewGate()
    {
        if (_context?.Playback?.IsHeldPreviewGateOpen == true)
        {
            _context.Tasks!.EndHeldPreviewGate();
            RefreshProperties();
        }
    }

    public void CancelHeldPreview()
    {
        _context?.Tasks?.CancelHeldPreview();
        RefreshProperties();
    }

    public void ResetPlaybackEngine()
    {
        if (_context?.Tasks is null) return;
        _context.Tasks.ResetPlaybackEngine();
        RefreshProperties();
    }

    public void UpdatePlayback()
    {
        if (!Monitor.TryEnter(_presetPreviewTransitions)) return;
        try { UpdatePlaybackCore(); }
        finally { Monitor.Exit(_presetPreviewTransitions); }
    }

    private void UpdatePlaybackCore()
    {
        if (_context?.Playback is null) return;
        _context.Playback.Update();
        bool tickChanged = CapturePlaybackTick();
        bool tempoChanged = UpdateTempoForTick(_displayCurrentTick);
        RefreshTimelinePlaybackCursors(_displayCurrentTick);
        if (tickChanged)
        {
            Raise(nameof(CurrentTick));
            Raise(nameof(PositionText));
        }
        if (tempoChanged)
        {
            Raise(nameof(TempoText));
        }
        Raise(nameof(PlaybackState));
        Raise(nameof(IsBuffering));
        Raise(nameof(PlaybackStatusText));
        Raise(nameof(IsPlaybackActive));
        Raise(nameof(IsLoopEnabled));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanTogglePlayback));
        Raise(nameof(PrimaryTransportAction));
        Raise(nameof(PrimaryTransportToolTip));
        Raise(nameof(CanPreview));
    }

    public async Task<PreparedDesktopAudioRender> PrepareAudioRenderAsync(
        DesktopAudioRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_context is null || Project is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        CanonicalCompiledResult current = await _context.Compilation
            .EnsureCurrentCompilationAsync(cancellationToken);
        if (!current.IsConsumable)
        {
            throw new InvalidOperationException(
                "Audio rendering requires a consumable current canonical compilation.");
        }
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        return await DesktopAudioRenderService.PrepareAsync(
            Project,
            Persistence?.CurrentProjectPath,
            _applicationPreferences.GetEnabledSoundFontConfigurations(),
            _applicationPreferences.Playback.MasterVolumeDecibels,
            options,
            cancellationToken);
    }

    public async Task<AudioRenderTaskResult> ExecuteAudioRenderAsync(
        PreparedDesktopAudioRender prepared,
        bool overwriteAuthorized,
        IProgress<AudioRenderTaskProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_context is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        using IDisposable editLock = _context.Compilation.AcquireProjectEditLock();
        AudioRenderTaskRunner runner = new(prepared.Worker);
        return await runner.ExecuteAsync(
            new()
            {
                Compilation = prepared.Compilation,
                OutputPlan = prepared.OutputPlan,
                SoundFont = prepared.SoundFont,
                SampleRate = prepared.SampleRate,
                MaximumSampleVoicesPerUnitStream = prepared.MaximumSampleVoicesPerUnitStream,
                MasterVolumeDecibels = prepared.MasterVolumeDecibels,
                OverwriteAuthorized = overwriteAuthorized
            },
            _context.Compilation,
            progress,
            cancellationToken);
    }

    public async Task<CanonicalCompiledResult> CompileProjectAsync(CancellationToken cancellationToken = default)
    {
        if (_context is null) throw new InvalidOperationException("No Project is open.");
        return await _context.Compilation.RecompileAsync(
            new ProjectChangeSet { AffectsEverything = true },
            cancellationToken);
    }

    public ProjectEditExecution Execute(IProjectEditCommand command)
    {
        CompletePendingSelectionHistoryCaptureBeforeEdit();
        if (Document is null)
        {
            throw new InvalidOperationException("No Project is open.");
        }
        if (!CanEditProject)
        {
            throw new InvalidOperationException(
                "Project editing is locked while playback or a foreground task is active.");
        }
        bool truncatesRedoBranch = Document.CanRedo;
        if (truncatesRedoBranch)
        {
            lock (_modelRefreshGate)
            {
                _workspaceSelectionHistoryPruneRequested = true;
            }
        }
        ProjectEditExecution result;
        try
        {
            result = Document.Execute(command);
        }
        catch
        {
            if (truncatesRedoBranch)
            {
                lock (_modelRefreshGate)
                {
                    _workspaceSelectionHistoryPruneRequested = false;
                }
            }
            throw;
        }
        if (!result.Changed && truncatesRedoBranch)
        {
            lock (_modelRefreshGate)
            {
                _workspaceSelectionHistoryPruneRequested = false;
            }
        }
        // ProjectDocumentSession raises HistoryChanged synchronously for a changed edit.
        // That event is the single refresh/revision path; refreshing here as well would
        // rebuild every rendered workspace twice for one atomic edit.
        return result;
    }

    public ProjectEditExecution ExecutePreservingWorkspaceSelection(
        IProjectEditCommand command,
        WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workspace);
        CompletePendingSelectionHistoryCaptureBeforeEdit();
        ProjectDocumentSession document = Document
            ?? throw new InvalidOperationException("No Project is open.");
        if (!Workspaces.Contains(workspace))
        {
            throw new InvalidOperationException(
                "The selection-preserving edit target is not an open Workspace.");
        }

        long beforeStateId = document.CurrentStateId;
        Dictionary<WorkspaceKey, WorkspaceSelectionBookmark> before =
            CaptureOpenWorkspaceSelections();
        ProjectEditExecution result = Execute(command);
        if (!result.Changed)
        {
            return result;
        }

        foreach ((WorkspaceKey key, WorkspaceSelectionBookmark bookmark) in before)
        {
            StoreSelection(beforeStateId, key, bookmark);
        }
        if (_uiDispatcher is null)
        {
            StoreCurrentWorkspaceSelections(document.CurrentStateId, before);
        }
        else
        {
            QueueSelectionHistoryCapture(document.CurrentStateId);
        }
        return result;
    }

    internal void StartInstrumentPresetPreview(Guid owner, InstrumentPresetPreviewRequest request, bool held) =>
        (_context?.Tasks ?? throw new InvalidOperationException(PlaybackUnavailableReason ?? "Realtime Preview is unavailable."))
            .StartInstrumentPresetPreview(owner, request, held);

    internal void StopInstrumentPresetPreview(Guid owner) => _context?.Tasks?.StopInstrumentPresetPreview(owner);
    internal void ReleaseInstrumentPresetPreviewKey(Guid owner) => _context?.Tasks?.ReleaseInstrumentPresetPreviewKey(owner);

    private readonly object _presetPreviewTransitions = new();
    internal void BeginPresetPreviewTransition() => Monitor.Enter(_presetPreviewTransitions);
    internal void EndPresetPreviewTransition() => Monitor.Exit(_presetPreviewTransitions);

    public StagedProjectEdit PrepareProjectEdit(
        IProjectEditCommand command,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null)
        => PrepareProjectEditCore(
            command,
            selectionWorkspace: null,
            cancellationToken,
            progress, retainedSelectionIds: null);

    public StagedProjectEdit PrepareProjectEdit(
        IProjectEditCommand command,
        WorkspaceViewModel selectionWorkspace,
        CancellationToken cancellationToken = default,
        IProgress<TimelineEditPreparationProgress>? progress = null,
        CompressedMidoraIdSet? retainedSelectionIds = null)
    {
        ArgumentNullException.ThrowIfNull(selectionWorkspace);
        return PrepareProjectEditCore(
            command,
            selectionWorkspace,
            cancellationToken,
            progress, retainedSelectionIds);
    }

    private StagedProjectEdit PrepareProjectEditCore(
        IProjectEditCommand command,
        WorkspaceViewModel? selectionWorkspace,
        CancellationToken cancellationToken,
        IProgress<TimelineEditPreparationProgress>? progress,
        CompressedMidoraIdSet? retainedSelectionIds)
    {
        ArgumentNullException.ThrowIfNull(command);
        ProjectDocumentSession document = Document
            ?? throw new InvalidOperationException("No Project is open.");
        if (selectionWorkspace is not null && !Workspaces.Contains(selectionWorkspace))
        {
            throw new InvalidOperationException(
                "The selection-preserving edit target is not an open Workspace.");
        }
        if (IsPlaybackActive)
        {
            throw new InvalidOperationException(
                "Project editing is locked while playback is active.");
        }
        long? frozenSelectionRevision = retainedSelectionIds is null ? null : selectionWorkspace?.Selection.Revision;
        if (retainedSelectionIds is not null && selectionWorkspace is not null
            && !retainedSelectionIds.IsSubsetOf(selectionWorkspace.Selection.SharedIds))
            throw new InvalidOperationException("Retained objects must belong to the frozen Workspace selection.");
        StagedProjectEdit staged = document.PrepareEdit(
            command,
            cancellationToken,
            progress is null ? null : new BeforeSelectionProjectionProgress(progress));
        try
        {
            if (retainedSelectionIds is not null && staged.PreparedSelection is null)
                throw new InvalidOperationException("A typed selection edit must publish its exact resulting selection.");
            if (staged.PreparedSelection is PreparedTimelineSelection selection)
            {
                // Project preparation is not the end of the foreground task:
                // compressing/result-projecting the selection can still spill
                // or be cancelled. Its work count is not known here.
                progress?.Report(new TimelineEditPreparationProgress(
                    TimelineEditPreparationPhase.BuildingResult, 0, 0).InRange(0.97, 0));
                cancellationToken.ThrowIfCancellationRequested();
                PreparedWorkspaceSelectionProjection projection =
                    retainedSelectionIds is not null && selectionWorkspace is not null
                    ? TimelineObjectSelection.MergeResult(selectionWorkspace.Selection,
                        selection.ResultSelectionIds, retainedSelectionIds, cancellationToken)
                    : command is TimelineCreationEditCommand creation
                    ? (selectionWorkspace?.Selection ?? new WorkspaceSelection()).PrepareProjection(
                        selection.ResultSelectionIds, creation.ResultSource, creation.ResultSource.QuantizeScope,
                        cancellationToken)
                    : command is IProjectClipboardPasteCommand paste
                    ? ClipboardSelectionProjection.Prepare(
                        document.Project, paste, selection.ResultSelectionIds,
                        selectionWorkspace?.Selection, cancellationToken)
                    : selectionWorkspace?.Selection.PrepareProjection(
                        selection.ResultSelectionIds,
                        cancellationToken)
                    ?? PreparedWorkspaceSelectionProjection.Create(
                        selection.ResultSelectionIds,
                        cancellationToken);
                _stagedWorkspaceSelectionProjections.Add(
                    staged,
                    new(selectionWorkspace?.Selection, projection, frozenSelectionRevision));
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(TimelineEditPreparationPhase.Ready, 1, 1));
            return staged;
        }
        catch
        {
            staged.Dispose();
            throw;
        }
    }

    private sealed class BeforeSelectionProjectionProgress(IProgress<TimelineEditPreparationProgress> target)
        : IProgress<TimelineEditPreparationProgress>
    {
        public void Report(TimelineEditPreparationProgress value) => target.Report(
            value.Phase == TimelineEditPreparationPhase.Ready
                ? new TimelineEditPreparationProgress(TimelineEditPreparationPhase.BuildingResult,
                    value.Completed, value.Total) { Detail = value.Detail }.InWorkRange(0.97, 0)
                : value.InRange(0, 0.97));
    }

    /// <summary>
    /// Prepares a typed Cut's already-staged deletion selection off-thread,
    /// before the OS clipboard and Project enter their publication section.
    /// It uses the same history bookmarks as ordinary prepared edits.
    /// </summary>
    internal void PrepareWorkspaceSelectionForStagedEdit(StagedProjectEdit staged,
        WorkspaceViewModel workspace, CompressedMidoraIdSet retainedSelectionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(retainedSelectionIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Workspaces.Contains(workspace))
            throw new InvalidOperationException("The selection edit target is not an open Workspace.");
        var selection = workspace.Selection;
        long revision = selection.Revision;
        if (!retainedSelectionIds.IsSubsetOf(selection.SharedIds))
            throw new InvalidOperationException("Retained objects must belong to the frozen Workspace selection.");
        var preparedSelection = staged.PreparedSelection
            ?? throw new InvalidOperationException("A typed selection edit must publish its exact resulting selection.");
        var projection = TimelineObjectSelection.MergeResult(selection,
            preparedSelection.ResultSelectionIds, retainedSelectionIds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(selection, workspace.Selection) || revision != selection.Revision)
            throw new InvalidOperationException("The Workspace selection changed during preparation.");
        _stagedWorkspaceSelectionProjections.Add(staged, new(selection, projection, revision));
    }

    internal bool TryGetPreparedWorkspaceSelectionProjection(
        StagedProjectEdit staged,
        out PreparedWorkspaceSelectionProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(staged);
        bool found = _stagedWorkspaceSelectionProjections.TryGetValue(
            staged,
            out PreparedWorkspaceSelectionPublication? publication);
        projection = publication?.Projection;
        return found;
    }

    /// <summary>
    /// Publishes a revision-gated edit prepared by the current foreground
    /// task. This is the only edit path intentionally allowed while that task
    /// owns the main-window lock.
    /// </summary>
    public ProjectEditExecution ExecutePreparedPreservingWorkspaceSelection(
        StagedProjectEdit staged,
        WorkspaceViewModel workspace,
        ITimelineSelectionResultEditCommand? resultSelectionCommand = null)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(workspace);
        CompletePendingSelectionHistoryCaptureBeforeEdit();
        ProjectDocumentSession document = Document
            ?? throw new InvalidOperationException("No Project is open.");
        if (!Workspaces.Contains(workspace))
        {
            throw new InvalidOperationException(
                "The selection-preserving edit target is not an open Workspace.");
        }
        if (IsPlaybackActive || (!CanEditProject && !IsForegroundTaskRunning))
        {
            throw new InvalidOperationException(
                "Project editing is locked while playback or another operation is active.");
        }

        bool hasPreparedSelection = _stagedWorkspaceSelectionProjections.TryGetValue(
            staged,
            out PreparedWorkspaceSelectionPublication? preparedSelectionPublication);
        _stagedWorkspaceSelectionProjections.Remove(staged);
        if (resultSelectionCommand is not null && !hasPreparedSelection)
        {
            staged.Dispose();
            throw new InvalidOperationException(
                "The prepared Timeline edit has no frozen Workspace selection projection.");
        }
        if (preparedSelectionPublication?.ExpectedSelection is WorkspaceSelection expectedSelection
            && (!ReferenceEquals(expectedSelection, workspace.Selection)
                || preparedSelectionPublication.ExpectedRevision is long revision && revision != workspace.Selection.Revision))
        {
            staged.Dispose();
            throw new InvalidOperationException(
                "The prepared Timeline selection belongs to another Workspace.");
        }

        long beforeStateId = document.CurrentStateId;
        Dictionary<WorkspaceKey, WorkspaceSelectionBookmark> before =
            CaptureOpenWorkspaceSelections();
        bool truncatesRedoBranch = document.CanRedo;
        if (truncatesRedoBranch)
        {
            lock (_modelRefreshGate) _workspaceSelectionHistoryPruneRequested = true;
        }

        ProjectEditExecution result;
        try
        {
            result = document.ExecutePrepared(staged);
        }
        catch
        {
            if (truncatesRedoBranch)
            {
                lock (_modelRefreshGate) _workspaceSelectionHistoryPruneRequested = false;
            }
            throw;
        }
        if (!result.Changed)
        {
            if (truncatesRedoBranch)
            {
                lock (_modelRefreshGate) _workspaceSelectionHistoryPruneRequested = false;
            }
            // An exact-key collision can discard every newly copied note while
            // still producing a valid (empty) result selection. Selection-only
            // publication must not create history or mark the Project modified.
            if (preparedSelectionPublication is not null)
            {
                workspace.Selection.AdoptPrepared(preparedSelectionPublication.Projection);
                if (_uiDispatcher is null)
                    StoreCurrentWorkspaceSelections(document.CurrentStateId, before);
                else QueueSelectionHistoryCapture(document.CurrentStateId);
            }
            return result;
        }

        foreach ((WorkspaceKey key, WorkspaceSelectionBookmark bookmark) in before)
            StoreSelection(beforeStateId, key, bookmark);

        if (preparedSelectionPublication is not null)
            workspace.Selection.AdoptPrepared(preparedSelectionPublication.Projection);

        if (_uiDispatcher is null)
            StoreCurrentWorkspaceSelections(document.CurrentStateId, before);
        else
            QueueSelectionHistoryCapture(document.CurrentStateId);
        return result;
    }

    private sealed record PreparedWorkspaceSelectionPublication(
        WorkspaceSelection? ExpectedSelection,
        PreparedWorkspaceSelectionProjection Projection,
        long? ExpectedRevision = null);

    public void ApplyObjectProperties(
        WorkspaceViewModel workspace,
        IReadOnlyCollection<PropertyField> fields)
    {
        IProjectEditCommand? command = CreateObjectPropertiesEdit(workspace, fields);
        if (command is not null) ExecutePreservingWorkspaceSelection(command, workspace);
    }

    internal IProjectEditCommand? CreateObjectPropertiesEdit(
        WorkspaceViewModel workspace,
        IReadOnlyCollection<PropertyField> fields)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(fields);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            throw new InvalidOperationException(
                "The Properties target is not an open Project Workspace.");
        }

        PropertyField[] pending = fields
            .Where(field => field.HasPendingChange)
            .ToArray();
        if (pending.Length == 0) return null;
        if (pending.Any(field => !ObjectPropertiesProjection.CanApplyFromOwnedEditor(workspace, field)))
        {
            throw new InvalidOperationException("One or more object properties are read-only.");
        }

        Dictionary<string, string> edits = pending.ToDictionary(
            field => field.Key,
            field => field.Value,
            StringComparer.Ordinal);
        return ObjectPropertiesProjection.CreateEditCommand(
            Project,
            workspace,
            edits);
    }

    public ObjectPropertiesViewModel CreateObjectProperties(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ObjectPropertiesViewModel result = new();
        ObjectPropertiesProjection.Rebuild(
            result,
            Project,
            workspace,
            _instrumentCatalogResolver);
        return result;
    }

    public void SetInstrumentCatalogResolver(InstrumentCatalogResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _instrumentCatalogResolver = resolver;
    }

    public void ApplyProjectSettingsField(PropertyField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!field.IsEditable || Project is null)
        {
            throw new InvalidOperationException("This Project Settings field is read-only.");
        }
        Execute(ProjectSettingsProjection.CreateEditCommand(Project, field));
    }

    public DesktopTaskViewModel BeginTask(
        string name,
        bool canCancel,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.ProjectEdit)
    {
        if (IsForegroundTaskRunning)
        {
            throw new InvalidOperationException("Another foreground task is already running.");
        }
        _foregroundTask?.Dispose();
        DesktopTaskViewModel task = new(name, canCancel, lockLevel);
        _foregroundTask = task;
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
        Raise(nameof(IsForegroundTaskRunning));
        Raise(nameof(CanEditProject));
        Raise(nameof(CanStartForegroundTask));
        Raise(nameof(CanRunProjectTask));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
        Raise(nameof(CanNavigateBack));
        Raise(nameof(CanNavigateForward));
        return task;
    }

    public void ReportTask(DesktopTaskViewModel task, string detail, double? progress = null)
    {
        if (!ReferenceEquals(_foregroundTask, task) || !task.IsRunning) return;
        task.Report(detail, progress);
        SetStatusMessage(detail);
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
    }

    public void CompleteTask(DesktopTaskViewModel task, string status, string detail = "")
    {
        if (!ReferenceEquals(_foregroundTask, task) || !task.IsRunning) return;
        task.Complete(status, detail);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            SetStatusMessage(detail, status == "Failed");
        }
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
        Raise(nameof(IsForegroundTaskRunning));
        Raise(nameof(CanEditProject));
        Raise(nameof(CanStartForegroundTask));
        Raise(nameof(CanRunProjectTask));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
    }

    public bool RequiresAudioWorkerRebuild(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        return !_applicationPreferences.RealtimeAudio.Equals(preferences.RealtimeAudio)
            || !_applicationPreferences.AudioCache.Equals(preferences.AudioCache)
            || !SoundFontPreferencesEqual(
                _applicationPreferences.SoundFonts,
                preferences.SoundFonts);
    }

    public async Task ApplyApplicationPreferencesAsync(
        ApplicationPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        bool rebuildAudioWorker = RequiresAudioWorkerRebuild(preferences);
        if (IsPlaybackActive)
        {
            throw new InvalidOperationException(
                "Application Preferences can only change while playback is stopped.");
        }

        _applicationPreferences = preferences;
        if (!rebuildAudioWorker)
        {
            _context?.Playback?.ConfigurePlaybackPreferences(
                new PlaybackMasterConfiguration(
                    checked((float)preferences.Playback.MasterVolumeDecibels),
                    preferences.Playback.LimiterEnabled),
                preferences.Playback.StopCursorBehavior);
            Raise(nameof(SoundFontState));
            Raise(nameof(HasEnabledSoundFonts));
            return;
        }

        BassWasapiChildPlaybackBackend? stalePreparedBackend = _preparedPlaybackBackend;
        _preparedPlaybackBackend = null;
        stalePreparedBackend?.Dispose();

        if (_context is null)
        {
            SoundFontConfiguration[] enabledSoundFonts =
                preferences.GetEnabledSoundFontConfigurations();
            if (enabledSoundFonts.Length != 0)
            {
                BassWasapiChildPlaybackBackend? preparedBackend =
                    ProjectContext.CreatePlaybackBackend(preferences);
                try
                {
                    SoundFontSetDefinition soundFontSet =
                        SoundFontSetDefinition.Create(enabledSoundFonts);
                    preparedBackend.SetSoundFontSet(
                        enabledSoundFonts,
                        soundFontSet.CacheIdentity);
                    await Task.Run(
                        () => preparedBackend.Prepare(cancellationToken),
                        cancellationToken);
                    _preparedPlaybackBackend = preparedBackend;
                    preparedBackend = null;
                }
                finally
                {
                    preparedBackend?.Dispose();
                }
            }
            Raise(nameof(SoundFontState));
            Raise(nameof(HasEnabledSoundFonts));
            return;
        }

        PlaybackController? previousPlayback = _context.Playback;
        if (previousPlayback is not null)
        {
            previousPlayback.StateChanged -= OnPlaybackStateChanged;
        }
        _context.ReconfigurePlaybackServices(preferences);
        if (_context.Playback is not null)
        {
            foreach (MidoraId trackId in _mutedTrackIds) _context.Playback.SetTrackMuted(trackId, true);
            foreach (MidoraId trackId in _soloTrackIds) _context.Playback.SetTrackSolo(trackId, true);
            foreach (MidoraId sharedGroupId in _mutedSharedGroupIds)
                _context.Playback.SetSharedGroupMuted(sharedGroupId, true);
            foreach (MidoraId sharedGroupId in _soloSharedGroupIds)
                _context.Playback.SetSharedGroupSolo(sharedGroupId, true);
            _context.Playback.StateChanged += OnPlaybackStateChanged;
        }
        try
        {
            if (preferences.GetEnabledSoundFontPaths().Length != 0)
            {
                PlaybackController playback = _context.Playback
                    ?? throw new InvalidOperationException(
                        _context.PlaybackUnavailableReason
                        ?? "The formal audio Worker is unavailable.");
                await Task.Run(
                    () => playback.WarmUpAudioBackend(cancellationToken),
                    cancellationToken);
            }
        }
        finally
        {
            RefreshProperties();
            Raise(nameof(SoundFontState));
            Raise(nameof(HasEnabledSoundFonts));
        }
    }

    public async Task<Exception?> TryInitializeAudioWorkerAsync(
        CancellationToken cancellationToken = default)
    {
        if (_context is null || _applicationPreferences.GetEnabledSoundFontPaths().Length == 0)
        {
            return null;
        }
        PlaybackController? playback = _context.Playback;
        if (playback is null)
        {
            return new InvalidOperationException(
                _context.PlaybackUnavailableReason
                ?? "The formal audio Worker is unavailable.");
        }
        try
        {
            await Task.Run(
                () => playback.WarmUpAudioBackend(cancellationToken),
                cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            RefreshProperties();
        }
    }

    private BassWasapiChildPlaybackBackend? TakePreparedPlaybackBackend()
    {
        BassWasapiChildPlaybackBackend? result = _preparedPlaybackBackend;
        _preparedPlaybackBackend = null;
        return result;
    }

    private static bool SoundFontPreferencesEqual(
        IReadOnlyList<ApplicationSoundFontPreference> left,
        IReadOnlyList<ApplicationSoundFontPreference> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].Enabled != right[index].Enabled
                || left[index].Target != right[index].Target
                || !string.Equals(
                    left[index].Path,
                    right[index].Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    public void NavigateToDiagnostic(DiagnosticRow diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (Project is null) return;
        SourceReference source = diagnostic.SourceReference;
        if (source.SegmentId != default)
        {
            (LogicalTrack Track, Segment Segment)? located = TimelineWorkspaceViewModel.FindSegment(Project, source.SegmentId);
            if (located is null)
            {
                SetStatusMessage("The diagnostic references a Segment that no longer exists.", isError: true);
                return;
            }
            TimelineWorkspaceViewModel workspace = OpenSegment(source.SegmentId);
            MidoraId target = source.LogicalNoteId != default
                && located.Value.Segment.Notes.TryGetById(source.LogicalNoteId, out _)
                    ? source.LogicalNoteId
                    : source.SegmentId;
            workspace.Selection.Replace(target);
            workspace.EditCursorTick = Math.Max(0, source.Tick);
            RefreshWorkspace(workspace);
            return;
        }
        if (source.EventInstrumentId != default)
        {
            InstrumentWorkspaceViewModel workspace = OpenInstrument(source.EventInstrumentId);
            MidoraId target = source.MappingFunctionId != default
                ? source.MappingFunctionId
                : source.SourceEventId != default
                    ? source.SourceEventId
                    : source.EventInstrumentId;
            workspace.Selection.Replace(target);
            RefreshWorkspace(workspace);
            return;
        }
        if (source.TrackId != default)
        {
            OpenArrangement();
            return;
        }
        if (source.SourceEventId == default && source.Tick < 0)
        {
            SetStatusMessage("This diagnostic has no navigable Project source.", isError: true);
            return;
        }
        TimelineWorkspaceViewModel conductor = (TimelineWorkspaceViewModel)OpenWorkspace(
            ProjectTree.First(item => item.Kind == ProjectTreeNodeKind.Conductor));
        MidoraId conductorId = source.SourceEventId;
        if (conductorId != default)
        {
            conductor.Selection.Replace(conductorId);
            RefreshWorkspace(conductor);
        }
        conductor.EditCursorTick = source.Tick >= 0 ? source.Tick : conductor.EditCursorTick;
    }

    public void RenameProjectTreeNode(ProjectTreeNode node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be renamed.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.RenameLogicalTrack(id, name),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.RenameEventInstrument(id, name),
            _ => throw new InvalidOperationException("This Project node cannot be renamed.")
        };
        Execute(command);
    }

    public void MoveProjectTreeNode(ProjectTreeNode node, int direction)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Project is null || direction == 0) return;
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be reordered.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.ReorderLogicalTrack(
                id,
                Math.Clamp(Project.Tracks.FindIndex(item => item.Id == id) + Math.Sign(direction), 0, Project.Tracks.Count - 1)),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.ReorderEventInstrument(
                id,
                Math.Clamp(Project.EventInstruments.FindIndex(item => item.Id == id) + Math.Sign(direction), 0, Project.EventInstruments.Count - 1)),
            _ => throw new InvalidOperationException("This Project node cannot be reordered.")
        };
        Execute(command);
    }

    public void ReorderProjectTreeNode(ProjectTreeNode node, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(node);
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be reordered.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.ReorderLogicalTrack(id, newIndex),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.ReorderEventInstrument(id, newIndex),
            _ => throw new InvalidOperationException("This Project node cannot be reordered.")
        };
        Execute(command);
    }

    public void DeleteProjectTreeNode(ProjectTreeNode node, bool confirmed)
    {
        ArgumentNullException.ThrowIfNull(node);
        MidoraId id = node.ObjectId
            ?? throw new InvalidOperationException("This Project node cannot be deleted.");
        IProjectEditCommand command = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.DeleteLogicalTrack(id, confirmed),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.DeleteEventInstrument(id, confirmed),
            ProjectTreeNodeKind.DamagedEventInstrument => ProjectDomainEditCommands.DeleteDamagedEventInstrument(id),
            ProjectTreeNodeKind.DamagedLogicalTrack => ProjectDomainEditCommands.DeleteDamagedLogicalTrack(id),
            _ => throw new InvalidOperationException("This Project node cannot be deleted.")
        };
        Execute(command);
    }

    public void BindLogicalTrack(MidoraId trackId, MidoraId? eventInstrumentId) =>
        Execute(ProjectDomainEditCommands.BindLogicalTrack(trackId, eventInstrumentId));

    public void Undo()
    {
        CompletePendingSelectionHistoryCaptureBeforeEdit();
        if (!CanEditProject) return;
        if (Document?.CanUndo != true) return;
        ProjectHistoryEntryInfo target = Document.History.Single(value =>
            value.AfterStateId == Document.CurrentStateId);
        RequestWorkspaceSelectionRestore(target.BeforeStateId);
        try
        {
            Document.Undo();
        }
        catch
        {
            CancelWorkspaceSelectionRestore(target.BeforeStateId);
            throw;
        }
    }

    internal void CaptureSelectionBeforeHistoryTransition() => CompletePendingSelectionHistoryCaptureBeforeEdit();

    internal void PublishHistoryTransition(PreparedProjectHistoryTransition transition)
    {
        if (Document is not ProjectDocumentSession document)
            throw new InvalidOperationException("The Project was closed while preparing history.");
        RequestWorkspaceSelectionRestore(transition.TargetStateId);
        try { document.PublishHistoryTransition(transition); }
        catch { CancelWorkspaceSelectionRestore(transition.TargetStateId); throw; }
    }

    public void Redo()
    {
        CompletePendingSelectionHistoryCaptureBeforeEdit();
        if (!CanEditProject) return;
        if (Document?.CanRedo != true) return;
        ProjectHistoryEntryInfo target = Document.History.Single(value =>
            value.BeforeStateId == Document.CurrentStateId);
        RequestWorkspaceSelectionRestore(target.AfterStateId);
        try
        {
            Document.Redo();
        }
        catch
        {
            CancelWorkspaceSelectionRestore(target.AfterStateId);
            throw;
        }
    }

    private WorkspaceSelectionBookmark CaptureSelection(WorkspaceViewModel workspace)
    {
        WorkspaceSelection selection = workspace.Selection;
        if (_workspaceSelectionBookmarkCache.TryGetValue(
                workspace.Key,
                out CachedWorkspaceSelectionBookmark cached)
            && cached.SelectionRevision == selection.Revision)
        {
            return cached.Bookmark;
        }
        WorkspaceSelectionBookmark bookmark = new(
            CompressedMidoraIdSet.Create(selection.IdSet),
            selection.Primary,
            selection.Anchor);
        _workspaceSelectionBookmarkCache[workspace.Key] = new(
            selection.Revision,
            bookmark);
        return bookmark;
    }

    private Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>
        CaptureOpenWorkspaceSelections()
    {
        Dictionary<WorkspaceKey, WorkspaceSelectionBookmark> result =
            new(Workspaces.Count);
        foreach (WorkspaceViewModel openWorkspace in Workspaces)
        {
            result[openWorkspace.Key] = CaptureSelection(openWorkspace);
        }
        return result;
    }

    private void StoreCurrentWorkspaceSelections(
        long stateId,
        IReadOnlyDictionary<WorkspaceKey, WorkspaceSelectionBookmark>? previous = null)
    {
        if (Document?.CurrentStateId != stateId) return;
        foreach (WorkspaceViewModel openWorkspace in Workspaces)
        {
            WorkspaceSelectionBookmark bookmark = CaptureSelection(openWorkspace);
            if (previous is not null
                && previous.TryGetValue(openWorkspace.Key, out WorkspaceSelectionBookmark? old)
                && SelectionsEqual(old, bookmark))
            {
                bookmark = old;
            }
            StoreSelection(stateId, openWorkspace.Key, bookmark);
        }
    }

    private void QueueSelectionHistoryCapture(long stateId)
    {
        bool scheduleDispatcher = false;
        lock (_modelRefreshGate)
        {
            ProjectContext? context = _context;
            if (context is null) return;
            _pendingModelRefreshContext = context;
            _pendingSelectionHistoryCaptureStateId = stateId;
            if (!_modelRefreshScheduled)
            {
                _modelRefreshScheduled = true;
                scheduleDispatcher = _uiDispatcher is not null;
            }
        }
        if (scheduleDispatcher)
        {
            _uiDispatcher!.BeginInvoke(
                DispatcherPriority.Render,
                new Action(DrainPendingModelRefresh));
        }
    }

    private void CompletePendingSelectionHistoryCaptureBeforeEdit()
    {
        if (_uiDispatcher?.CheckAccess() != true) return;
        bool pending;
        lock (_modelRefreshGate)
        {
            pending = _pendingSelectionHistoryCaptureStateId is not null;
        }
        if (pending) DrainPendingModelRefresh();
    }

    private static bool SelectionsEqual(
        WorkspaceSelectionBookmark left,
        WorkspaceSelectionBookmark right) =>
        ReferenceEquals(left, right)
        || left.Primary == right.Primary
        && left.Anchor == right.Anchor
        && (ReferenceEquals(left.Ids, right.Ids)
            || left.Ids.SetEquals(right.Ids));

    private void StoreSelection(
        long stateId,
        WorkspaceKey workspaceKey,
        WorkspaceSelectionBookmark bookmark)
    {
        if (!_workspaceSelectionHistory.TryGetValue(
                stateId,
                out Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>? byWorkspace))
        {
            byWorkspace = [];
            _workspaceSelectionHistory.Add(stateId, byWorkspace);
        }
        byWorkspace[workspaceKey] = bookmark;
    }

    private void RequestWorkspaceSelectionRestore(long stateId)
    {
        lock (_modelRefreshGate)
        {
            _pendingSelectionRestoreStateId = stateId;
        }
    }

    private void CancelWorkspaceSelectionRestore(long stateId)
    {
        lock (_modelRefreshGate)
        {
            if (_pendingSelectionRestoreStateId == stateId)
            {
                _pendingSelectionRestoreStateId = null;
            }
        }
    }

    private List<WorkspaceViewModel> RestoreWorkspaceSelections(long stateId)
    {
        List<WorkspaceViewModel> changed = [];
        if (!_workspaceSelectionHistory.TryGetValue(
                stateId,
                out Dictionary<WorkspaceKey, WorkspaceSelectionBookmark>? byWorkspace))
        {
            return changed;
        }
        foreach ((WorkspaceKey key, WorkspaceSelectionBookmark bookmark) in byWorkspace)
        {
            WorkspaceViewModel? workspace = Workspaces.FirstOrDefault(value => value.Key == key);
            if (workspace is null) continue;
            if (workspace.Selection.Primary == bookmark.Primary
                && workspace.Selection.Anchor == bookmark.Anchor
                && workspace.Selection.IdSet is CompressedMidoraIdSet current
                && (ReferenceEquals(current, bookmark.Ids)
                    || current.SetEquals(bookmark.Ids)))
            {
                continue;
            }
            workspace.Selection.AdoptMaterialized(
                bookmark.Ids,
                bookmark.Primary,
                bookmark.Anchor);
            changed.Add(workspace);
        }
        return changed;
    }

    private void PruneWorkspaceSelectionHistory()
    {
        if (Document is null)
        {
            _workspaceSelectionHistory.Clear();
            _workspaceSelectionBookmarkCache.Clear();
            return;
        }
        HashSet<long> liveStateIds = [Document.CurrentStateId];
        foreach (ProjectHistoryEntryInfo entry in Document.History)
        {
            liveStateIds.Add(entry.BeforeStateId);
            liveStateIds.Add(entry.AfterStateId);
        }
        foreach (long staleStateId in _workspaceSelectionHistory.Keys
            .Where(value => !liveStateIds.Contains(value))
            .ToArray())
        {
            _workspaceSelectionHistory.Remove(staleStateId);
        }
    }

    public WorkspaceViewModel OpenWorkspace(ProjectTreeNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        WorkspaceViewModel workspace = node.Kind switch
        {
            ProjectTreeNodeKind.Conductor => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.ConductorTrack),
                () => new TimelineWorkspaceViewModel(
                    WorkspaceKey.ForType(WorkspaceKind.ConductorTrack),
                    "Conductor Track",
                    TimelineWorkspaceMode.Conductor,
                    ArrangementEditorSettings)),
            ProjectTreeNodeKind.InstrumentLibrary => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.EventInstrumentLibrary),
                static () => new LibraryWorkspaceViewModel()),
            ProjectTreeNodeKind.EventInstrument when node.ObjectId is MidoraId id => GetOrCreate(
                WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, id),
                () => new InstrumentWorkspaceViewModel(id, node.Title)),
            ProjectTreeNodeKind.LogicalTracks or ProjectTreeNodeKind.LogicalTrack => OpenArrangement(),
            ProjectTreeNodeKind.ProjectSettings => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.ProjectSettings),
                static () => new SettingsWorkspaceViewModel()),
            ProjectTreeNodeKind.Diagnostics => GetOrCreate(
                WorkspaceKey.ForType(WorkspaceKind.Diagnostics),
                static () => new DiagnosticsWorkspaceViewModel()),
            ProjectTreeNodeKind.DamagedEventInstrument or ProjectTreeNodeKind.DamagedLogicalTrack =>
                throw new InvalidOperationException(
                    "Damaged placeholders cannot be opened or edited. Review the error tooltip, then explicitly delete the placeholder or discard this Project session."),
            _ => throw new InvalidOperationException("This Project tree node has no Workspace.")
        };
        ActiveWorkspace = workspace;
        return workspace;
    }

    public TimelineWorkspaceViewModel OpenArrangement()
    {
        TimelineWorkspaceViewModel workspace = (TimelineWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            () => new TimelineWorkspaceViewModel(
                WorkspaceKey.ForType(WorkspaceKind.Arrangement),
                "Arrangement",
                TimelineWorkspaceMode.Arrangement,
                ArrangementEditorSettings));
        ActiveWorkspace = workspace;
        return workspace;
    }

    public TimelineWorkspaceViewModel OpenSegment(MidoraId segmentId)
    {
        TimelineWorkspaceViewModel workspace = (TimelineWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segmentId),
            () => new TimelineWorkspaceViewModel(
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segmentId),
                "Segment",
                TimelineWorkspaceMode.Segment,
                PianoRollEditorSettings));
        CenterSegmentEditorOnArrangementCursor(workspace, segmentId);
        ActiveWorkspace = workspace;
        return workspace;
    }

    private void CenterSegmentEditorOnArrangementCursor(
        TimelineWorkspaceViewModel workspace,
        MidoraId segmentId)
    {
        if (Project is null
            || ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } arrangement)
        {
            return;
        }
        if (arrangement.EditCursorTick is not long projectTick)
        {
            return;
        }

        (long ProjectStartTick, long LengthTicks, long ContentOffsetTick)? segment =
            TimelineWorkspaceViewModel.FindSegment(Project, segmentId) is { } logical
                ? (logical.Segment.ProjectStartTick, logical.Segment.LengthTicks, logical.Segment.ContentOffsetTick)
                : TimelineWorkspaceViewModel.FindMidiSegment(Project, segmentId) is { } midi
                    ? (midi.Segment.ProjectStartTick, midi.Segment.LengthTicks, midi.Segment.ContentOffsetTick)
                    : null;
        if (segment is null)
        {
            return;
        }
        long projectEndTick = checked(segment.Value.ProjectStartTick + segment.Value.LengthTicks);
        if (projectTick < segment.Value.ProjectStartTick || projectTick >= projectEndTick)
        {
            return;
        }

        long localTick = checked(
            segment.Value.ContentOffsetTick + (projectTick - segment.Value.ProjectStartTick));
        workspace.EditCursorTick = localTick;
        workspace.CenterViewportOnTick(localTick);
    }

    public InstrumentWorkspaceViewModel OpenInstrument(MidoraId instrumentId)
    {
        EventInstrument instrument = Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
            ?? throw new InvalidOperationException("The Event Instrument no longer exists.");
        InstrumentWorkspaceViewModel workspace = (InstrumentWorkspaceViewModel)GetOrCreate(
            WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, instrumentId),
            () => new InstrumentWorkspaceViewModel(instrumentId, instrument.Name));
        ActiveWorkspace = workspace;
        return workspace;
    }

    public void CloseWorkspace(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!workspace.CanClose) return;
        int index = Workspaces.IndexOf(workspace);
        if (index < 0) return;
        Workspaces.RemoveAt(index);
        _workspaceSelectionBookmarkCache.Remove(workspace.Key);
        if (ReferenceEquals(ActiveWorkspace, workspace))
        {
            ActiveWorkspace = Workspaces.Count == 0
                ? null
                : Workspaces[Math.Clamp(index - 1, 0, Workspaces.Count - 1)];
        }
        if (ReferenceEquals(_diagnosticScopeWorkspace, workspace))
        {
            _diagnosticScopeWorkspace = ActiveWorkspace is DiagnosticsWorkspaceViewModel ? null : ActiveWorkspace;
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
        }
        workspace.Dispose();
        _backNavigation.RemoveAll(key => key == workspace.Key);
        _forwardNavigation.RemoveAll(key => key == workspace.Key);
        Raise(nameof(CanNavigateBack));
        Raise(nameof(CanNavigateForward));
    }

    public void ReorderWorkspace(WorkspaceViewModel workspace, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        int oldIndex = Workspaces.IndexOf(workspace);
        if (oldIndex < 0)
        {
            throw new InvalidOperationException("The Workspace is no longer open.");
        }
        if (newIndex < 0 || newIndex >= Workspaces.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(newIndex));
        }
        if (!workspace.CanReorder) return;
        newIndex = Math.Max(1, newIndex);
        if (oldIndex != newIndex) Workspaces.Move(oldIndex, newIndex);
        ActiveWorkspace = workspace;
    }

    public void RefreshWorkspace(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            return;
        }
        PrepareWorkspaceRuntimeState(workspace);
        workspace.Rebuild(Project, _revision);
        RefreshOnionPresentations();
        workspace.PresentationDocumentRevision = Document?.PublicationRevision ?? -1;
        workspace.RefreshSelectionPresentation();
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            timeline.UpdatePlaybackCursor(Project, CurrentTick);
        }
        if (workspace is not DiagnosticsWorkspaceViewModel)
        {
            if (ReferenceEquals(workspace, ActiveWorkspace)) _diagnosticScopeWorkspace = workspace;
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
        }
    }

    internal bool ActivateCreatedSubVoiceEventLane(
        InstrumentWorkspaceViewModel workspace, MidoraId subVoiceId, MidiValueTarget target)
    {
        if (!ReferenceEquals(ActiveWorkspace, workspace) || !Workspaces.Contains(workspace)
            || workspace.IsDisposed || workspace.IsPresentationSuspended
            || workspace.ActiveSubVoiceId != subVoiceId
            || Project?.EventInstruments.FirstOrDefault(value => value.Id == workspace.ObjectId)?
                .SubVoices.FirstOrDefault(value => value.Id == subVoiceId) is not SubVoice voice
            || !voice.EventMappings.Any(value => value.Target == TemplateEventMidiTargets.ToMappingTarget(target)))
            return false;

        workspace.PreferEventLaneOnNextRebuild(target);
        RefreshWorkspace(workspace);
        return workspace.TryActivateEventLane(target);
    }

    public void RefreshProjectRuntimeInformation()
    {
        ProjectContext? context = _context;
        if (context is null)
        {
            return;
        }
        CompilationStatistics statistics = context.Compilation.LastAttempt.Statistics;
        long totalEditingTimeMilliseconds =
            context.Compilation.SnapshotTotalEditingTimeMilliseconds();
        foreach (SettingsWorkspaceViewModel workspace in
            Workspaces.OfType<SettingsWorkspaceViewModel>())
        {
            workspace.UpdateRuntimeInformation(statistics, totalEditingTimeMilliseconds);
        }
    }

    public void RefreshWorkspaceSelection(WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            return;
        }
        WorkspaceSelectionRefreshCount++;
        workspace.RefreshSelectionPresentation();
        if (workspace is not DiagnosticsWorkspaceViewModel)
        {
            if (ReferenceEquals(workspace, ActiveWorkspace)) _diagnosticScopeWorkspace = workspace;
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
        }
    }

    public void SelectWorkspaceObject(WorkspaceViewModel workspace, MidoraId id)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!Workspaces.Contains(workspace)) return;
        workspace.Selection.Replace(id);
        workspace.RefreshSelectionPresentation();
        if (ReferenceEquals(workspace, _diagnosticScopeWorkspace))
        {
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(workspace);
        }
    }

    public void ActivateSubVoiceEditor(InstrumentWorkspaceViewModel workspace, MidoraId subVoiceId)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        EventInstrument? instrument = Project?.EventInstruments
            .FirstOrDefault(value => value.Id == workspace.ObjectId);
        if (!Workspaces.Contains(workspace)
            || instrument?.SubVoices.Any(value => value.Id == subVoiceId) != true)
        {
            return;
        }
        SelectWorkspaceObject(workspace, subVoiceId);
        RefreshWorkspace(workspace);
        workspace.ActiveSectionIndex = 1;
    }

    public async Task CloseProjectAsync()
    {
        ProjectContext? previous = _context;
        if (previous is null) return;
        Task refreshesIdle = DetachProjectContextFromRefreshes(previous);
        Unsubscribe(previous);
        await refreshesIdle;
        List<Exception>? failures = null;
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }

        Cleanup(TimelineRasterCacheSession.Clear);
        // Workspace teardown can notify user-facing bindings. One failing
        // Workspace must not skip the remaining tabs or the Project context.
        foreach (WorkspaceViewModel workspace in Workspaces.ToArray())
            Cleanup(workspace.Dispose);
        Cleanup(Workspaces.Clear);
        _backNavigation.Clear();
        _forwardNavigation.Clear();
        _workspaceSelectionHistory.Clear();
        _workspaceSelectionBookmarkCache.Clear();
        _projectTreeStructureStamp = null;
        _diagnosticScopeWorkspace = null;
        Cleanup(ProjectTree.Clear);
        CompilerDiagnostics = VirtualDiagnosticRows.Empty;
        Cleanup(() => Raise(nameof(CompilerDiagnostics)));
        Cleanup(() => SelectedDiagnostic = null);
        _mutedTrackIds.Clear();
        _soloTrackIds.Clear();
        _mutedSharedGroupIds.Clear();
        _soloSharedGroupIds.Clear();
        DesktopTaskViewModel? foregroundTask = _foregroundTask;
        _foregroundTask = null;
        Cleanup(() => foregroundTask?.Dispose());
        Cleanup(() => ActiveWorkspace = null);
        _activeWorkspace = null;
        _revision = 0;
        _timeSignatureMap = null;
        Cleanup(() => ArrangementEditorSettings.Reset(arrangement: true));
        Cleanup(() => PianoRollEditorSettings.Reset(arrangement: false));
        _displayCurrentTick = 0;
        _orderedTempoChanges = [];
        _activeTempoIndex = -1;
        _tempoLookupTick = -1;
        _tempoText = "— BPM";
        Cleanup(() => SetStatusMessage(null));
        Cleanup(RefreshProperties);
        try { await previous.DisposeAsync(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }

        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException("Project closing cleanup failed.", failures);
    }

    public async ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;
        try { await CloseProjectAsync(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        BassWasapiChildPlaybackBackend? preparedBackend = _preparedPlaybackBackend;
        _preparedPlaybackBackend = null;
        try { preparedBackend?.Dispose(); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException("Desktop session cleanup failed.", failures);
    }

    private async Task ActivateAsync(ProjectContext next)
    {
        ArgumentNullException.ThrowIfNull(next);
        ProjectContext? previous = _context;
        if (previous is not null)
        {
            Task refreshesIdle = DetachProjectContextFromRefreshes(previous);
            Unsubscribe(previous);
            await refreshesIdle;
        }
        List<Exception>? failures = null;
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        WorkspaceViewModel[] previousWorkspaces = Workspaces.ToArray();
        try
        {
            try
            {
                // This is the ownership handoff. Callers must not dispose next
                // merely because retiring old UI or later presentation failed.
                AttachProjectContextToRefreshes(next);
                _mutedTrackIds.Clear();
                _soloTrackIds.Clear();
                _mutedSharedGroupIds.Clear();
                _soloSharedGroupIds.Clear();
                ProjectSegmentIndex.Warm(next.Compilation.Project);
                _displayCurrentTick = next.Playback?.CurrentTick ?? 0;
                Subscribe(next);
            }
            finally
            {
                Cleanup(TimelineRasterCacheSession.Clear);
                foreach (WorkspaceViewModel workspace in previousWorkspaces)
                    Cleanup(workspace.Dispose);
                Cleanup(Workspaces.Clear);
                _backNavigation.Clear();
                _forwardNavigation.Clear();
                _workspaceSelectionHistory.Clear();
                _workspaceSelectionBookmarkCache.Clear();
                _projectTreeStructureStamp = null;
                _diagnosticScopeWorkspace = null;
                Cleanup(() => ActiveWorkspace = null);
                _activeWorkspace = null;
            }
            _revision = 1;
            _timeSignatureMap = new(Project!);
            ArrangementEditorSettings.Reset(arrangement: true, Project!.TicksPerQuarterNote);
            PianoRollEditorSettings.Reset(arrangement: false, Project.TicksPerQuarterNote);
            ArrangementEditorSettings.ConfigureProject(Project, referenceTick: 0);
            PianoRollEditorSettings.ConfigureProject(Project, referenceTick: 0);
            SetStatusMessage(null);
            RefreshAll();
            OpenArrangement();
            AudioCacheWarning cacheWarning = next.Compilation.AudioCacheWarning;
            if (cacheWarning.Code != AudioCacheWarningCode.None)
            {
                SetStatusMessage("Audio cache warning: " + cacheWarning.Message);
            }
        }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (previous is not null)
        {
            try { await previous.DisposeAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is { Count: 1 })
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures is not null)
            throw new AggregateException("Project activation cleanup failed.", failures);
    }

    private WorkspaceViewModel GetOrCreate(
        WorkspaceKey key,
        Func<WorkspaceViewModel> factory)
    {
        WorkspaceViewModel? existing = Workspaces.FirstOrDefault(item => item.Key == key);
        if (existing is not null)
        {
            return existing;
        }
        WorkspaceViewModel created = factory();
        if (Project is not null)
        {
            PrepareWorkspaceRuntimeState(created);
            created.Rebuild(Project, _revision);
            created.PresentationDocumentRevision = Document?.PublicationRevision ?? -1;
            if (created is TimelineWorkspaceViewModel timeline)
            {
                timeline.UpdatePlaybackCursor(Project, CurrentTick);
            }
            if (created is DiagnosticsWorkspaceViewModel diagnostics)
            {
                diagnostics.Replace(CompilerDiagnostics);
                diagnostics.SetScope(_diagnosticScopeWorkspace);
            }
        }
        if (created.Kind == WorkspaceKind.Arrangement)
            Workspaces.Insert(0, created);
        else
            Workspaces.Add(created);
        RefreshOnionPresentations();
        return created;
    }

    private void RefreshAll()
    {
        CapturePlaybackTick();
        _timeSignatureMap = Project is null ? null : new ProjectTimeSignatureMap(Project);
        RebuildTempoLookup();
        if (Project is not null)
        {
            ArrangementEditorSettings.ConfigureProject(Project, CurrentTick);
            PianoRollEditorSettings.ConfigureProject(Project, CurrentTick);
        }
        RefreshDiagnostics();
        RefreshProjectTree();
        if (Project is not null)
        {
            foreach (WorkspaceViewModel workspace in Workspaces.ToArray())
            {
                if (!ObjectStillExists(workspace))
                {
                    CloseWorkspace(workspace);
                    continue;
                }
                PrepareWorkspaceRuntimeState(workspace);
                workspace.Rebuild(Project, _revision);
                workspace.PresentationDocumentRevision = Document?.PublicationRevision ?? -1;
                workspace.RefreshSelectionPresentation();
                if (workspace is TimelineWorkspaceViewModel timeline)
                {
                    timeline.UpdatePlaybackCursor(Project, CurrentTick);
                }
            }
        }
        RefreshOnionPresentations();
        RefreshProperties();
    }

    private HashSet<WorkspaceViewModel> RefreshChanged(ProjectChangeSet changes)
    {
        HashSet<WorkspaceViewModel> rebuilt = [];
        if (Project is null || IsEmpty(changes))
        {
            return rebuilt;
        }
        if (changes.AffectsEverything || changes.AffectsConductor)
        {
            _timeSignatureMap = new ProjectTimeSignatureMap(Project);
            RebuildTempoLookup();
            ArrangementEditorSettings.ConfigureProject(Project, CurrentTick);
            PianoRollEditorSettings.ConfigureProject(Project, CurrentTick);
        }
        RefreshProjectTreeIfChanged();
        HashSet<MidoraId> trackIds = changes.TrackIds;
        HashSet<MidoraId> instrumentIds = changes.EventInstrumentIds;
        HashSet<MidoraId> usageIds = changes.EventInstrumentUsageIds;
        HashSet<MidoraId> rootIds = changes.MidiChannelRootIds;
        HashSet<MidoraId> pureTrackIds = changes.PureMidiTrackIds;
        foreach (WorkspaceViewModel workspace in Workspaces.ToArray())
        {
            if (!ObjectStillExists(workspace))
            {
                CloseWorkspace(workspace);
                continue;
            }
            if (workspace is DiagnosticsWorkspaceViewModel)
            {
                continue;
            }
            if (!WorkspaceAffected(
                    workspace,
                    changes,
                    trackIds,
                    instrumentIds,
                    usageIds,
                    rootIds,
                    pureTrackIds))
            {
                continue;
            }
            PrepareWorkspaceRuntimeState(workspace);
            workspace.Rebuild(Project, _revision);
            workspace.PresentationDocumentRevision = Document?.PublicationRevision ?? -1;
            WorkspaceRebuildCount++;
            workspace.RefreshSelectionPresentation();
            rebuilt.Add(workspace);
            if (workspace is TimelineWorkspaceViewModel timeline)
            {
                timeline.UpdatePlaybackCursor(Project, CurrentTick);
            }
        }
        RefreshOnionPresentations();
        return rebuilt;
    }

    /// <summary>
    /// Starts an unmodified marquee replacement by committing the empty
    /// formal selection before any potentially long out-of-core range query.
    /// If that query is canceled by a lane/snapshot rebuild, later edits must
    /// observe the empty revision rather than resurrecting its predecessor.
    /// </summary>
    public TimelineSelectionSnapshot BeginWorkspaceSelectionReplacement(
        WorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (Project is null || !Workspaces.Contains(workspace))
        {
            throw new InvalidOperationException(
                "The selection replacement target is not an open Project Workspace.");
        }
        workspace.Selection.Clear();
        RefreshWorkspaceSelection(workspace);
        return workspace.SelectionSnapshot;
    }

    private static bool IsEmpty(ProjectChangeSet changes) =>
        !changes.AffectsEverything
        && !changes.AffectsConductor
        && !changes.AffectsAudioPcmCacheGeneration
        && changes.TrackIds.Count == 0
        && changes.EventInstrumentIds.Count == 0
        && changes.EventInstrumentUsageIds.Count == 0
        && changes.MidiChannelRootIds.Count == 0
        && changes.PureMidiTrackIds.Count == 0
        && changes.PresentationTrackIds.Count == 0
        && changes.PresentationEventInstrumentIds.Count == 0;

    private bool WorkspaceAffected(
        WorkspaceViewModel workspace,
        ProjectChangeSet changes,
        HashSet<MidoraId> trackIds,
        HashSet<MidoraId> instrumentIds,
        HashSet<MidoraId> usageIds,
        HashSet<MidoraId> rootIds,
        HashSet<MidoraId> pureTrackIds)
    {
        if (changes.AffectsEverything) return true;
        return workspace.Kind switch
        {
            WorkspaceKind.Arrangement => changes.AffectsConductor
                || trackIds.Count != 0
                || instrumentIds.Count != 0
                || usageIds.Count != 0
                || rootIds.Count != 0
                || pureTrackIds.Count != 0
                || changes.PresentationTrackIds.Count != 0
                || changes.PresentationEventInstrumentIds.Count != 0,
            WorkspaceKind.ConductorTrack => changes.AffectsConductor,
            WorkspaceKind.SegmentEditor => workspace.ObjectId is MidoraId segmentId
                && (TimelineWorkspaceViewModel.FindSegment(Project!, segmentId) is { } located
                    && (trackIds.Contains(located.Track.Id)
                        || changes.PresentationTrackIds.Contains(located.Track.Id)
                        || located.Track.EventInstrumentUsageId is MidoraId usageId
                           && usageIds.Contains(usageId)
                        || Project!.ResolveEventInstrumentDefinitionId(located.Track) is MidoraId instrumentId
                           && (instrumentIds.Contains(instrumentId)
                               || changes.PresentationEventInstrumentIds.Contains(instrumentId)))
                    || TimelineWorkspaceViewModel.FindMidiSegment(Project!, segmentId) is { } midi
                    && (pureTrackIds.Contains(midi.Track.Id)
                        || changes.PresentationTrackIds.Contains(midi.Track.Id)
                        || rootIds.Contains(midi.Track.MidiChannelRootId))),
            WorkspaceKind.EventInstrumentLibrary => instrumentIds.Count != 0
                || changes.PresentationEventInstrumentIds.Count != 0,
            WorkspaceKind.EventInstrumentEditor => workspace.ObjectId is MidoraId eventInstrumentId
                && (instrumentIds.Contains(eventInstrumentId)
                    || changes.PresentationEventInstrumentIds.Contains(eventInstrumentId)),
            WorkspaceKind.ProjectSettings => false,
            WorkspaceKind.Diagnostics => false,
            _ => false
        };
    }

    private void PrepareWorkspaceRuntimeState(WorkspaceViewModel workspace)
    {
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } timeline)
        {
            timeline.SetTrackMonitoringStates(
                _mutedTrackIds,
                _soloTrackIds,
                _mutedSharedGroupIds,
                _soloSharedGroupIds);
        }
        if (workspace is SettingsWorkspaceViewModel settings && _context is not null)
        {
            settings.UpdateRuntimeInformation(
                _context.Compilation.LastAttempt.Statistics,
                _context.Compilation.SnapshotTotalEditingTimeMilliseconds());
        }
    }

    private void RefreshProjectTree()
    {
        ProjectTreeRefreshCount++;
        ProjectTree.Clear();
        if (Project is null)
        {
            _projectTreeStructureStamp = null;
            return;
        }

        string query = ProjectTreeSearchText.Trim();
        bool filtered = query.Length != 0;
        if (!filtered)
        {
            ProjectTree.Add(new(ProjectTreeNodeKind.Conductor, "Conductor Track"));
        }
        ProjectTreeNode library = new(ProjectTreeNodeKind.InstrumentLibrary, "Event Instrument Library");
        foreach (EventInstrument instrument in Project.EventInstruments.Where(item =>
                     MatchesProjectTreeFilter(item.Name, query)))
        {
            library.Children.Add(new(ProjectTreeNodeKind.EventInstrument, instrument.Name, instrument.Id));
        }
        foreach (DamagedProjectObject damaged in Project.DamagedEventInstruments
                     .Where(item => MatchesProjectTreeFilter(item.NameSnapshot, query))
                     .OrderBy(item => item.OriginalIndex)
                     .ThenBy(item => item.Id))
        {
            ProjectTreeNode node = CreateDamagedNode(ProjectTreeNodeKind.DamagedEventInstrument, damaged);
            library.Children.Insert(Math.Clamp(damaged.OriginalIndex, 0, library.Children.Count), node);
        }
        if (!filtered || library.Children.Count != 0) ProjectTree.Add(library);

        ProjectTreeNode tracks = new(ProjectTreeNodeKind.LogicalTracks, "Logical Tracks");
        foreach (LogicalTrack track in Project.Tracks)
        {
            EventInstrument? bound = Project.FindEventInstrumentDefinition(track);
            string boundName = bound is null
                ? track.LastBoundEventInstrumentName ?? string.Empty
                : bound.Name;
            if (!MatchesProjectTreeFilter(track.Name, query)
                && !MatchesProjectTreeFilter(boundName, query))
            {
                continue;
            }
            tracks.Children.Add(new(
                ProjectTreeNodeKind.LogicalTrack,
                TimelineWorkspaceViewModel.TrackDisplayName(Project, track),
                track.Id,
                bound is null
                    ? "Unbound"
                    : string.IsNullOrWhiteSpace(bound.Name) ? "Unnamed instrument" : bound.Name));
        }
        foreach (DamagedProjectObject damaged in Project.DamagedLogicalTracks
                     .Where(item => MatchesProjectTreeFilter(item.NameSnapshot, query))
                     .OrderBy(item => item.OriginalIndex)
                     .ThenBy(item => item.Id))
        {
            ProjectTreeNode node = CreateDamagedNode(ProjectTreeNodeKind.DamagedLogicalTrack, damaged);
            tracks.Children.Insert(Math.Clamp(damaged.OriginalIndex, 0, tracks.Children.Count), node);
        }
        if (!filtered || tracks.Children.Count != 0) ProjectTree.Add(tracks);
        if (!filtered)
        {
            ProjectTree.Add(new(ProjectTreeNodeKind.ProjectSettings, "Project Settings"));
            ProjectTree.Add(new(ProjectTreeNodeKind.Diagnostics, $"Diagnostics ({IssueSummary})"));
        }
        _projectTreeStructureStamp = CaptureProjectTreeStructureStamp(Project);
    }

    private void RefreshProjectTreeIfChanged()
    {
        if (Project is null)
        {
            return;
        }
        string stamp = CaptureProjectTreeStructureStamp(Project);
        if (!string.Equals(_projectTreeStructureStamp, stamp, StringComparison.Ordinal))
        {
            RefreshProjectTree();
        }
    }

    public bool TryApplyMaterializedWorkspaceSelection(
        WorkspaceViewModel workspace,
        TimelineMaterializedSelection materialization)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(materialization);
        if (!IsWorkspaceSelectionMaterializationCurrent(
                workspace,
                materialization.BaseSelectionRevision))
        {
            return false;
        }
        if (!materialization.IsUnchanged)
        {
            workspace.Selection.AdoptMaterialized(
                materialization.Ids,
                materialization.Primary,
                materialization.Anchor);
        }
        WorkspaceSelectionRefreshCount++;
        workspace.PublishMaterializedSelection(
            materialization.Metrics,
            materialization.MetricsAreComplete,
            materialization.RenderIndex);
        if (workspace is not DiagnosticsWorkspaceViewModel)
        {
            if (ReferenceEquals(workspace, ActiveWorkspace)) _diagnosticScopeWorkspace = workspace;
            Workspaces.OfType<DiagnosticsWorkspaceViewModel>()
                .FirstOrDefault()?.SetScope(_diagnosticScopeWorkspace);
        }
        return true;
    }

    public bool IsWorkspaceSelectionMaterializationCurrent(
        WorkspaceViewModel workspace,
        long baseSelectionRevision)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        bool contentRefreshPending;
        lock (_modelRefreshGate)
        {
            contentRefreshPending = (_pendingModelRefreshKinds & ModelRefreshKind.Content) != 0;
        }
        return Project is not null
            && Workspaces.Contains(workspace)
            // An asynchronous selection query belongs to the immutable Timeline
            // snapshot on which it started. A Project edit can commit synchronously while
            // its WPF projection rebuild is still queued at Render priority.
            // Reject publication in that window; otherwise an old marquee can
            // repopulate a selection that the user already cleared just before
            // drawing an Event/Parameter point.
            && !contentRefreshPending
            && baseSelectionRevision == workspace.Selection.Revision;
    }

    private string CaptureProjectTreeStructureStamp(MidoraProject project)
    {
        StringBuilder builder = new();
        AppendStampField(builder, ProjectTreeSearchText.Trim());
        Dictionary<MidoraId, EventInstrument> instrumentsById = new(
            project.EventInstruments.Count);
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            instrumentsById.TryAdd(instrument.Id, instrument);
            AppendStampField(builder, instrument.Id.Value);
            AppendStampField(builder, instrument.Name);
        }
        Dictionary<MidoraId, MidoraId> definitionIdByUsageId = new(
            project.EventInstrumentUsages.Count);
        foreach (EventInstrumentUsage usage in project.EventInstrumentUsages)
        {
            definitionIdByUsageId.TryAdd(usage.Id, usage.EventInstrumentId);
        }
        foreach (DamagedProjectObject damaged in project.DamagedEventInstruments)
        {
            AppendStampField(builder, damaged.Id.Value);
            AppendStampField(builder, damaged.NameSnapshot);
            AppendStampField(builder, damaged.OriginalIndex.ToString(CultureInfo.InvariantCulture));
            AppendStampField(builder, damaged.Error);
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            AppendStampField(builder, track.Id.Value);
            AppendStampField(builder, track.Name);
            AppendStampField(builder, track.EventInstrumentUsageId?.Value);
            AppendStampField(builder, track.LastBoundEventInstrumentName);
            EventInstrument? definition = track.EventInstrumentUsageId is MidoraId usageId
                && definitionIdByUsageId.TryGetValue(usageId, out MidoraId definitionId)
                && instrumentsById.TryGetValue(definitionId, out EventInstrument? resolved)
                    ? resolved
                    : null;
            AppendStampField(builder, definition?.Id.Value);
            AppendStampField(builder, definition?.Name);
        }
        foreach (DamagedProjectObject damaged in project.DamagedLogicalTracks)
        {
            AppendStampField(builder, damaged.Id.Value);
            AppendStampField(builder, damaged.NameSnapshot);
            AppendStampField(builder, damaged.OriginalIndex.ToString(CultureInfo.InvariantCulture));
            AppendStampField(builder, damaged.Error);
        }
        return builder.ToString();
    }

    private static void AppendStampField(StringBuilder builder, string? value)
    {
        value ??= string.Empty;
        builder.Append(value.Length).Append(':').Append(value).Append(';');
    }

    private static void AppendStampField(StringBuilder builder, long value) =>
        AppendStampField(builder, value.ToString(CultureInfo.InvariantCulture));

    private static void AppendStampField(StringBuilder builder, long? value) =>
        AppendStampField(
            builder,
            value?.ToString(CultureInfo.InvariantCulture));

    private static bool MatchesProjectTreeFilter(string? value, string query) =>
        query.Length == 0 || (value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private void RefreshDiagnosticProjectTreeNode()
    {
        ProjectTreeNode? node = ProjectTree.FirstOrDefault(item => item.Kind == ProjectTreeNodeKind.Diagnostics);
        if (node is not null)
        {
            node.Title = $"Diagnostics ({IssueSummary})";
        }
    }

    private static ProjectTreeNode CreateDamagedNode(
        ProjectTreeNodeKind kind,
        DamagedProjectObject damaged)
    {
        string name = string.IsNullOrWhiteSpace(damaged.NameSnapshot)
            ? Path.GetFileNameWithoutExtension(damaged.PackagePath)
            : damaged.NameSnapshot;
        return new(kind, $"[Damaged] {name}", damaged.Id, damaged.Error);
    }

    private void RefreshDiagnostics()
    {
        DiagnosticRefreshCount++;
        lock (_modelRefreshGate)
        {
            ICompilerDiagnosticSequence source = _context?.Compilation.LastAttempt.Diagnostics
                ?? CompilerDiagnosticList.Empty;
            VirtualDiagnosticRows diagnostics = new(source, _context?.Compilation.IsCompilationCurrent ?? false);
            CompilerDiagnostics = diagnostics;
            Raise(nameof(CompilerDiagnostics));
            foreach (DiagnosticsWorkspaceViewModel workspace in
                Workspaces.OfType<DiagnosticsWorkspaceViewModel>())
            {
                workspace.Replace(diagnostics);
            }
            _compilerErrorCount = CompilerDiagnosticList.CountSeverity(source, DiagnosticSeverity.Error);
            _compilerWarningCount = CompilerDiagnosticList.CountSeverity(source, DiagnosticSeverity.Warning);
        }
    }

    private bool ObjectStillExists(WorkspaceViewModel workspace)
    {
        if (Project is null || workspace.ObjectId is not MidoraId id) return true;
        return workspace.Kind switch
        {
            WorkspaceKind.SegmentEditor =>
                TimelineWorkspaceViewModel.FindSegment(Project, id) is not null
                || TimelineWorkspaceViewModel.FindMidiSegment(Project, id) is not null,
            WorkspaceKind.EventInstrumentEditor => Project.EventInstruments.Any(item => item.Id == id),
            _ => true
        };
    }

    private void RefreshProperties()
    {
        CapturePlaybackTick();
        UpdateTempoForTick(_displayCurrentTick);
        RefreshTimelinePlaybackCursors(_displayCurrentTick);
        Raise(nameof(HasProject));
        Raise(nameof(Document));
        Raise(nameof(Project));
        Raise(nameof(Persistence));
        Raise(nameof(WindowTitle));
        Raise(nameof(ProjectDisplayName));
        Raise(nameof(TitleBarProjectDisplayName));
        Raise(nameof(ProjectState));
        Raise(nameof(CompileState));
        Raise(nameof(SoundFontState));
        Raise(nameof(HasEnabledSoundFonts));
        Raise(nameof(PositionText));
        Raise(nameof(CurrentTick));
        Raise(nameof(TempoText));
        Raise(nameof(PlaybackState));
        Raise(nameof(IsBuffering));
        Raise(nameof(PlaybackStatusText));
        Raise(nameof(IsPlaybackActive));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanTogglePlayback));
        Raise(nameof(PrimaryTransportAction));
        Raise(nameof(PrimaryTransportToolTip));
        Raise(nameof(CanPreview));
        Raise(nameof(PlaybackUnavailableReason));
        Raise(nameof(ErrorCount));
        Raise(nameof(WarningCount));
        Raise(nameof(HasErrors));
        Raise(nameof(HasWarnings));
        Raise(nameof(IssueSummary));
        Raise(nameof(ActivityText));
        Raise(nameof(ActiveForegroundTask));
        Raise(nameof(IsMainWindowTaskLocked));
        Raise(nameof(IsFullApplicationTaskLocked));
        Raise(nameof(IsForegroundTaskRunning));
        Raise(nameof(CanEditProject));
        Raise(nameof(CanStartForegroundTask));
        Raise(nameof(CanRunProjectTask));
        Raise(nameof(HasDamagedProjectObjects));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
    }

    private void RefreshDocumentStateProperties()
    {
        Raise(nameof(Document));
        Raise(nameof(Project));
        Raise(nameof(WindowTitle));
        Raise(nameof(ProjectDisplayName));
        Raise(nameof(TitleBarProjectDisplayName));
        Raise(nameof(ProjectState));
        Raise(nameof(CanEditProject));
        Raise(nameof(HasDamagedProjectObjects));
        Raise(nameof(CanSaveProject));
        Raise(nameof(CanUseContextMenus));
    }

    private void RefreshCompilationProperties()
    {
        Raise(nameof(CompileState));
        Raise(nameof(CanPlayback));
        Raise(nameof(CanTogglePlayback));
        Raise(nameof(PrimaryTransportAction));
        Raise(nameof(PrimaryTransportToolTip));
        Raise(nameof(CanPreview));
        Raise(nameof(PlaybackUnavailableReason));
        Raise(nameof(ErrorCount));
        Raise(nameof(WarningCount));
        Raise(nameof(HasErrors));
        Raise(nameof(HasWarnings));
        Raise(nameof(IssueSummary));
        Raise(nameof(ActivityText));
    }

    private void RefreshTimelinePlaybackCursors() =>
        RefreshTimelinePlaybackCursors(_displayCurrentTick);

    private void RefreshTimelinePlaybackCursors(long currentTick)
    {
        if (Project is null)
        {
            return;
        }
        foreach (IPlaybackTimelineWorkspace timeline in Workspaces.OfType<IPlaybackTimelineWorkspace>())
        {
            timeline.UpdatePlaybackCursor(Project, currentTick);
        }
    }

    private bool CapturePlaybackTick()
    {
        long next = Math.Max(0, _context?.Playback?.CurrentTick ?? 0);
        if (_displayCurrentTick == next)
        {
            return false;
        }
        _displayCurrentTick = next;
        return true;
    }

    private void RebuildTempoLookup()
    {
        _orderedTempoChanges = Project?.Conductor.Tempos
            .OrderBy(static value => value.Tick)
            .ToArray()
            ?? [];
        _activeTempoIndex = -1;
        _tempoLookupTick = -1;
        UpdateTempoForTick(_displayCurrentTick, force: true);
    }

    private bool UpdateTempoForTick(long tick, bool force = false)
    {
        tick = Math.Max(0, tick);
        int previousTempoIndex = _activeTempoIndex;
        if (_orderedTempoChanges.Length == 0)
        {
            _activeTempoIndex = -1;
        }
        else if (tick >= _tempoLookupTick && _activeTempoIndex >= -1)
        {
            while (_activeTempoIndex + 1 < _orderedTempoChanges.Length
                && _orderedTempoChanges[_activeTempoIndex + 1].Tick <= tick)
            {
                _activeTempoIndex++;
            }
        }
        else
        {
            int low = 0;
            int high = _orderedTempoChanges.Length;
            while (low < high)
            {
                int middle = low + ((high - low) >> 1);
                if (_orderedTempoChanges[middle].Tick <= tick)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            _activeTempoIndex = low - 1;
        }
        _tempoLookupTick = tick;
        if (!force && previousTempoIndex == _activeTempoIndex)
        {
            return false;
        }
        string next = _activeTempoIndex < 0
            ? "— BPM"
            : $"{_orderedTempoChanges[_activeTempoIndex].BeatsPerMinute:0.00} BPM";
        if (string.Equals(_tempoText, next, StringComparison.Ordinal))
        {
            return false;
        }
        _tempoText = next;
        return true;
    }

    private void Subscribe(ProjectContext context)
    {
        _onionIdentity = Guid.NewGuid().ToString("N");
        context.Persistence.Presentation.Changed += OnPresentationChanged;
        context.Document.HistoryChanged += OnDocumentHistoryChanged;
        context.Document.ContentChanged += OnDocumentContentChanged;
        context.Compilation.CompilationChanged += OnCompilationChanged;
        if (context.Playback is not null)
        {
            context.Playback.StateChanged += OnPlaybackStateChanged;
        }
    }

    private void Unsubscribe(ProjectContext context)
    {
        context.Persistence.Presentation.Changed -= OnPresentationChanged;
        context.Document.HistoryChanged -= OnDocumentHistoryChanged;
        context.Document.ContentChanged -= OnDocumentContentChanged;
        context.Compilation.CompilationChanged -= OnCompilationChanged;
        if (context.Playback is not null)
        {
            context.Playback.StateChanged -= OnPlaybackStateChanged;
        }
    }

    private void OnDocumentHistoryChanged(object? sender, EventArgs e)
    {
        ProjectContext? context = _context;
        if (context is null || !ReferenceEquals(sender, context.Document)) return;
        ScheduleModelRefresh(ModelRefreshKind.History, sourceContext: context);
    }

    private void OnDocumentContentChanged(
        object? sender,
        ProjectContentChangedEventArgs e)
    {
        ProjectContext? context = _context;
        if (context is null || !ReferenceEquals(sender, context.Document)) return;
        ScheduleModelRefresh(
            ModelRefreshKind.Content,
            ToChangeSet(e),
            context);
    }

    private void OnCompilationChanged(object? sender, EventArgs e)
    {
        ProjectContext? context = _context;
        if (context is null || !ReferenceEquals(sender, context.Compilation)) return;
        ScheduleModelRefresh(ModelRefreshKind.Compilation, sourceContext: context);
    }

    private void OnPlaybackStateChanged(object? sender, EventArgs e)
    {
        ProjectContext? context = _context;
        if (context is null || !ReferenceEquals(sender, context.Playback)) return;
        ScheduleModelRefresh(ModelRefreshKind.Playback, sourceContext: context);
    }

    private void ScheduleModelRefresh(
        ModelRefreshKind kind,
        ProjectChangeSet? changes = null,
        ProjectContext? sourceContext = null)
    {
        bool scheduleDispatcher = false;
        bool runSynchronously = false;
        lock (_modelRefreshGate)
        {
            ProjectContext? current = _context;
            if (current is null
                || sourceContext is not null && !ReferenceEquals(current, sourceContext))
            {
                return;
            }
            _pendingModelRefreshContext = current;
            _pendingModelRefreshKinds |= kind;
            if (changes is not null)
            {
                _pendingContentChanges = _pendingContentChanges is null
                    ? CloneChanges(changes)
                    : MergeChanges(_pendingContentChanges, changes);
            }
            if (!_modelRefreshScheduled)
            {
                _modelRefreshScheduled = true;
                scheduleDispatcher = _uiDispatcher is not null;
                runSynchronously = !scheduleDispatcher;
            }
        }

        if (scheduleDispatcher)
        {
            // A document transaction emits Compilation/Content/History in one
            // synchronous burst. A single render-priority callback projects the
            // final state once and avoids layout/input re-entrancy.
            _uiDispatcher!.BeginInvoke(
                DispatcherPriority.Render,
                new Action(DrainPendingModelRefresh));
        }
        else if (runSynchronously)
        {
            DrainPendingModelRefresh();
        }
    }

    private void DrainPendingModelRefresh()
    {
        ModelRefreshKind kinds;
        ProjectChangeSet? changes;
        ProjectContext? refreshContext;
        long? restoreStateId;
        long? selectionHistoryCaptureStateId;
        bool pruneSelectionHistory;
        lock (_modelRefreshGate)
        {
            kinds = _pendingModelRefreshKinds;
            changes = _pendingContentChanges;
            refreshContext = _pendingModelRefreshContext;
            // Compilation can notify while Undo/Redo has mutated the source but
            // before the document commits its history state. Only the document's
            // post-commit Content/History notification may consume the bookmark.
            restoreStateId = (kinds & (ModelRefreshKind.Content | ModelRefreshKind.History)) != 0
                ? _pendingSelectionRestoreStateId : null;
            selectionHistoryCaptureStateId = _pendingSelectionHistoryCaptureStateId;
            pruneSelectionHistory = (kinds & ModelRefreshKind.History) != 0
                && _workspaceSelectionHistoryPruneRequested;
            _pendingModelRefreshKinds = ModelRefreshKind.None;
            _pendingContentChanges = null;
            _pendingModelRefreshContext = null;
            if (restoreStateId is not null) _pendingSelectionRestoreStateId = null;
            _pendingSelectionHistoryCaptureStateId = null;
            if (pruneSelectionHistory)
            {
                _workspaceSelectionHistoryPruneRequested = false;
            }
            _modelRefreshScheduled = false;
            if (kinds == ModelRefreshKind.None
                && restoreStateId is null
                && selectionHistoryCaptureStateId is null
                || refreshContext is null
                || !ReferenceEquals(_context, refreshContext))
            {
                return;
            }
            _activeModelRefreshPasses = checked(_activeModelRefreshPasses + 1);
        }

        try
        {
            ModelRefreshPassCount++;
            List<WorkspaceViewModel> selectionChanged = restoreStateId is long stateId
                && Document?.CurrentStateId == stateId
                ? RestoreWorkspaceSelections(stateId)
                : [];
            HashSet<WorkspaceViewModel> rebuilt = [];
            if ((kinds & ModelRefreshKind.Content) != 0)
            {
                _revision++;
                rebuilt = RefreshChanged(changes ?? ProjectChangeSet.Everything);
            }
            foreach (WorkspaceViewModel workspace in selectionChanged)
            {
                if (!rebuilt.Contains(workspace))
                {
                    RefreshWorkspaceSelection(workspace);
                }
            }

            if (selectionHistoryCaptureStateId is long captureStateId
                && Document?.CurrentStateId == captureStateId)
            {
                StoreCurrentWorkspaceSelections(captureStateId);
            }

            if ((kinds & ModelRefreshKind.History) != 0 && pruneSelectionHistory)
            {
                PruneWorkspaceSelectionHistory();
            }
            if ((kinds & (ModelRefreshKind.Content | ModelRefreshKind.History)) != 0)
            {
                RefreshDocumentStateProperties();
            }

            if ((kinds & ModelRefreshKind.Compilation) != 0)
            {
                ProjectCompilationState state = refreshContext.Compilation.CompilationState;
                if (state is ProjectCompilationState.NotCompiled
                    or ProjectCompilationState.Succeeded
                    or ProjectCompilationState.Failed)
                {
                    RefreshDiagnostics();
                    RefreshDiagnosticProjectTreeNode();
                    RefreshProjectRuntimeInformation();
                }
                RefreshCompilationProperties();
                RefreshOnionPresentations();
                if (refreshContext.Compilation.DiagnosticCapacityFailureMessage is string capacityFailure)
                    SetStatusMessage("Compile Project: " + capacityFailure, isError: true);
            }

            if ((kinds & ModelRefreshKind.Playback) != 0)
            {
                if (refreshContext.Playback is PlaybackController
                    {
                        State: PlaybackState.Error,
                        LastError: Exception failure
                    })
                {
                    SetStatusMessage($"Playback failed: {failure.Message}", isError: true);
                }
                else if (refreshContext.Compilation.AudioCacheWarning is
                         { Code: not AudioCacheWarningCode.None } warning)
                {
                    SetStatusMessage("Audio cache warning: " + warning.Message);
                }
                RefreshProperties();
            }
        }
        finally
        {
            CompleteModelRefreshPass();
        }
    }

    private Task DetachProjectContextFromRefreshes(ProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_modelRefreshGate)
        {
            if (!ReferenceEquals(_context, context)) return Task.CompletedTask;
            _context = null;
            ResetPendingModelRefreshCore();
            if (_activeModelRefreshPasses == 0) return Task.CompletedTask;
            return (_modelRefreshIdleCompletion ??= new(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private void AttachProjectContextToRefreshes(ProjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_modelRefreshGate)
        {
            if (_context is not null || _activeModelRefreshPasses != 0)
            {
                throw new InvalidOperationException(
                    "A Project context cannot be attached while another context is active.");
            }
            ResetPendingModelRefreshCore();
            _context = context;
        }
    }

    private void CompleteModelRefreshPass()
    {
        TaskCompletionSource<bool>? completion = null;
        lock (_modelRefreshGate)
        {
            _activeModelRefreshPasses--;
            if (_activeModelRefreshPasses < 0)
            {
                _activeModelRefreshPasses = 0;
                throw new InvalidOperationException("The model refresh pass count became negative.");
            }
            if (_activeModelRefreshPasses == 0)
            {
                completion = _modelRefreshIdleCompletion;
                _modelRefreshIdleCompletion = null;
            }
        }
        completion?.TrySetResult(true);
    }

    private void ResetPendingModelRefreshCore()
    {
        _pendingModelRefreshKinds = ModelRefreshKind.None;
        _pendingContentChanges = null;
        _pendingModelRefreshContext = null;
        _pendingSelectionRestoreStateId = null;
        _pendingSelectionHistoryCaptureStateId = null;
        _workspaceSelectionHistoryPruneRequested = false;
        _modelRefreshScheduled = false;
    }

    private static ProjectChangeSet ToChangeSet(ProjectContentChangedEventArgs source)
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
        return result;
    }

    private static ProjectChangeSet CloneChanges(ProjectChangeSet source) =>
        MergeChanges(new ProjectChangeSet(), source);

    private static ProjectChangeSet MergeChanges(
        ProjectChangeSet left,
        ProjectChangeSet right)
    {
        ProjectChangeSet result = new()
        {
            AffectsEverything = left.AffectsEverything || right.AffectsEverything,
            AffectsConductor = left.AffectsConductor || right.AffectsConductor,
            AffectsAudioPcmCacheGeneration = left.AffectsAudioPcmCacheGeneration
                || right.AffectsAudioPcmCacheGeneration
        };
        result.TrackIds.UnionWith(left.TrackIds);
        result.TrackIds.UnionWith(right.TrackIds);
        result.EventInstrumentIds.UnionWith(left.EventInstrumentIds);
        result.EventInstrumentIds.UnionWith(right.EventInstrumentIds);
        result.EventInstrumentUsageIds.UnionWith(left.EventInstrumentUsageIds);
        result.EventInstrumentUsageIds.UnionWith(right.EventInstrumentUsageIds);
        result.MidiChannelRootIds.UnionWith(left.MidiChannelRootIds);
        result.MidiChannelRootIds.UnionWith(right.MidiChannelRootIds);
        result.PureMidiTrackIds.UnionWith(left.PureMidiTrackIds);
        result.PureMidiTrackIds.UnionWith(right.PureMidiTrackIds);
        result.PresentationTrackIds.UnionWith(left.PresentationTrackIds);
        result.PresentationTrackIds.UnionWith(right.PresentationTrackIds);
        result.PresentationEventInstrumentIds.UnionWith(
            left.PresentationEventInstrumentIds);
        result.PresentationEventInstrumentIds.UnionWith(
            right.PresentationEventInstrumentIds);
        return result;
    }

    private sealed record WorkspaceSelectionBookmark(
        CompressedMidoraIdSet Ids,
        MidoraId? Primary,
        MidoraId? Anchor);

    private readonly record struct CachedWorkspaceSelectionBookmark(
        long SelectionRevision,
        WorkspaceSelectionBookmark Bookmark);

    private sealed class ProjectContext : IAsyncDisposable
    {
        private readonly IAsyncDisposable _owner;
        private int _disposeStarted;

        private ProjectContext(
            IAsyncDisposable owner,
            ProjectCompilationSession compilation,
            ProjectDocumentSession document,
            ProjectPersistenceCoordinator persistence,
            PlaybackController? playback,
            ApplicationTaskCoordinator? tasks,
            string? playbackUnavailableReason)
        {
            _owner = owner;
            Compilation = compilation;
            Document = document;
            Persistence = persistence;
            Playback = playback;
            Tasks = tasks;
            PlaybackUnavailableReason = playbackUnavailableReason;
        }

        public ProjectCompilationSession Compilation { get; }
        public ProjectDocumentSession Document { get; }
        public ProjectPersistenceCoordinator Persistence { get; }
        public PlaybackController? Playback { get; private set; }
        public ApplicationTaskCoordinator? Tasks { get; private set; }
        public string? PlaybackUnavailableReason { get; private set; }

        public static ProjectContext FromCreation(
            MidoraProjectPackageV1 packages,
            NewProjectCreationResult result,
            ApplicationPreferences preferences,
            BassWasapiChildPlaybackBackend? preparedPlaybackBackend = null)
        {
            ProjectCompilationSession compilation = new(
                result.Project,
                executionMode: ProjectCompilationExecutionMode.Background);
            try
            {
                compilation.SetEffectiveSoundFontConfigurations(
                    preferences.GetEnabledSoundFontConfigurations());
                ProjectDocumentSession document = new(compilation, result.Origin);
                ProjectPersistenceCoordinator persistence = new(
                    document,
                    packages,
                    result.CurrentProjectPath,
                    result.FileInformation);
                CreatePlaybackServices(
                    compilation,
                    preferences,
                    preparedPlaybackBackend,
                    out PlaybackController? playback,
                    out ApplicationTaskCoordinator? tasks,
                    out string? playbackFailure);
                preparedPlaybackBackend = null;
                return new(
                    result,
                    compilation,
                    document,
                    persistence,
                    playback,
                    tasks,
                    playbackFailure);
            }
            catch
            {
                preparedPlaybackBackend?.Dispose();
                compilation.Dispose();
                throw;
            }
        }

        public static ProjectContext FromOpenCandidate(
            ProjectOpenCandidate candidate,
            ApplicationPreferences preferences,
            BassWasapiChildPlaybackBackend? preparedPlaybackBackend = null)
        {
            ProjectCompilationSession compilation = new(
                candidate.Project,
                executionMode: ProjectCompilationExecutionMode.Background);
            try
            {
                compilation.SetEffectiveSoundFontConfigurations(
                    preferences.GetEnabledSoundFontConfigurations());
                ProjectDocumentSession document = candidate.CreateDocumentSession(compilation);
                ProjectPersistenceCoordinator persistence =
                    candidate.CreatePersistenceCoordinator(document);
                CreatePlaybackServices(
                    compilation,
                    preferences,
                    preparedPlaybackBackend,
                    out PlaybackController? playback,
                    out ApplicationTaskCoordinator? tasks,
                    out string? playbackFailure);
                preparedPlaybackBackend = null;
                return new(
                    candidate,
                    compilation,
                    document,
                    persistence,
                    playback,
                    tasks,
                    playbackFailure);
            }
            catch
            {
                preparedPlaybackBackend?.Dispose();
                compilation.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            List<Exception>? failures = null;
            void Cleanup(Action action)
            {
                try { action(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }

            // A backend/history failure must not strand the compiler worker or
            // the Project's owned session files. Preserve release order and
            // report every failure after all independent owners were attempted.
            Cleanup(() => Tasks?.Dispose());
            Cleanup(() => Playback?.Dispose());
            Cleanup(Document.Dispose);
            Cleanup(Compilation.Dispose);
            try { await _owner.DisposeAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }

            if (failures is { Count: 1 })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures is not null)
                throw new AggregateException("Project context cleanup failed.", failures);
        }

        public void ReconfigurePlaybackServices(ApplicationPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            Tasks?.Dispose();
            Playback?.Dispose();
            Tasks = null;
            Playback = null;
            Compilation.SetEffectiveSoundFontConfigurations(
                preferences.GetEnabledSoundFontConfigurations());
            CreatePlaybackServices(
                Compilation,
                preferences,
                preparedPlaybackBackend: null,
                out PlaybackController? playback,
                out ApplicationTaskCoordinator? tasks,
                out string? failure);
            Playback = playback;
            Tasks = tasks;
            PlaybackUnavailableReason = failure;
        }

        private static void CreatePlaybackServices(
            ProjectCompilationSession compilation,
            ApplicationPreferences? suppliedPreferences,
            BassWasapiChildPlaybackBackend? preparedPlaybackBackend,
            out PlaybackController? playback,
            out ApplicationTaskCoordinator? tasks,
            out string? failure)
        {
            playback = null;
            tasks = null;
            ApplicationPreferences preferences = suppliedPreferences
                ?? new ApplicationPreferencesStore().Load().Preferences;
            compilation.ConfigureAudioCache(
                preferences.AudioCache.RootPath,
                preferences.AudioCache.MaximumReusableBytes);
            BassWasapiChildPlaybackBackend? backend = preparedPlaybackBackend;
            try
            {
                if (backend is null)
                {
                    backend = CreatePlaybackBackend(preferences);
                }
                else
                {
                    // The detached backend was prepared with the metadata identity. Align the
                    // Project session before PlaybackController attaches so it does not discard
                    // the already-loaded persistent Worker as a stale SoundFont host.
                    compilation.RefreshEffectiveSoundFontCacheIdentity();
                }
                playback = new(
                    compilation,
                    backend,
                    new PlaybackMasterConfiguration(
                        checked((float)preferences.Playback.MasterVolumeDecibels),
                        preferences.Playback.LimiterEnabled),
                    preferences.Playback.StopCursorBehavior);
                backend = null;
                tasks = new(compilation, playback);
                failure = null;
            }
            catch (Exception exception)
            {
                backend?.Dispose();
                playback?.Dispose();
                playback = null;
                tasks = null;
                failure = exception.Message;
            }
        }

        public static BassWasapiChildPlaybackBackend CreatePlaybackBackend(
            ApplicationPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            if (!FormalAudioWorkerLocator.TryLocate(
                    out string? workerPath,
                    out string? nativeDirectory,
                    out string? failure))
            {
                throw new InvalidOperationException(
                    failure ?? "The formal audio Worker is unavailable.");
            }
            RealtimeAudioPreferences audio = preferences.RealtimeAudio;
            return new(new(
                workerPath!,
                nativeDirectory!,
                audio.PlaybackOutputDeviceId,
                audio.RenderAheadMilliseconds,
                audio.DeviceBufferRequestMilliseconds,
                new BassMidiRendererSettings(
                    audio.MaximumSampleVoicesPerUnitStream,
                    Midora.Audio.InitialReleaseAudioRuntimePolicy.WorkFrameCount),
                new AudioMasterSettings(
                    checked((float)preferences.Playback.MasterVolumeDecibels),
                    AudioMasterSettings.LimiterCeilingV2,
                    AudioMasterSettings.LimiterReleaseMillisecondsV2),
                TimeSpan.FromSeconds(30)));
        }
    }
}
