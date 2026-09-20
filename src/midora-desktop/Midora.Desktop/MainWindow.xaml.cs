using System.ComponentModel;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Midora.MidiExport;
using Midora.AudioRender;
using Midora.Compiler;
using Midora.Persistence;

namespace Midora.Desktop;

public partial class MainWindow : Window
{
    private const double MinimumUiReorderDragDistance = 10;
    private const string ProjectTreeDragFormat = "Midora.ProjectTreeNode";
    private const string WorkspaceTabDragFormat = "Midora.WorkspaceTab";
    private const string EventInstrumentDragFormat = "Midora.EventInstrumentId";
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private readonly DesktopSessionController _session = new();
    private readonly ApplicationPreferencesStore _preferenceStore = new();
    private readonly InstrumentCatalogStore _instrumentCatalogStore = new();
    private readonly RecentProjectsService _recentProjects = new(new RecentProjectsStore());
    private ApplicationPreferences _preferences = ApplicationPreferences.Default;
    private InstrumentCatalogState _instrumentCatalog = InstrumentCatalogState.Default;
    private InstrumentCatalogResolver _instrumentCatalogResolver = new(
        InstrumentCatalogState.Default,
        Array.Empty<InstrumentCatalogSoundFontEntry>());
    private bool _instrumentCatalogCanPublish = true;
    private ApplicationPreferenceNotice? _instrumentCatalogNotice;
    private HwndSource? _windowSource;
    private bool _closeApproved;
    private bool _closeRequestInProgress;
    private bool _operationInProgress;
    private readonly DispatcherTimer _playbackTimer;
    private ProjectObjectClipboardPayload? _projectClipboard;
    private ProjectDocumentSession? _clipboardDocument;
    private (MidoraId SegmentId, long StartTick, int Pitch, int Velocity)? _notePlacement;
    private (MidoraId InstrumentId, MidoraId SubVoiceId, long StartTick, int Pitch, int Velocity)? _templateNotePlacement;
    private Point? _projectTreeDragStart;
    private ProjectTreeNode? _projectTreeDragNode;
    private Point? _workspaceTabDragStart;
    private WorkspaceViewModel? _workspaceTabDragWorkspace;
    private bool _spaceStartedPlayback;
    private Point? _instrumentListDragStart;
    private MidoraId? _instrumentListDragId;
    private ListBoxItem? _instrumentBrowserDropContainer;
    private bool _instrumentBrowserDropAfter;
    private int? _trackHeaderContextLane;
    private MidoraId? _arrangementSharedGroupContextId;
    private MidoraId? _logicalTrackShortcutTrackId;
    private (ArrangementLaneKind Kind, MidoraId Id)? _arrangementHeaderShortcut;
    private readonly TimelineCommandTarget _pendingTimelineAltTarget = new();
    private TimelineSurface? _pendingTimelineAltReleaseFocus
    {
        get => _pendingTimelineAltTarget.Resolve(_session.ActiveWorkspace);
        set => _pendingTimelineAltTarget.Set(value);
    }
    private bool _synchronizingInstrumentStructureSelection;
    private bool _followPlaybackViewportInteractionActive;
    private CancellationTokenSource? _instrumentLoopCommitDelay;
    private CancellationTokenSource? _timelineSelectionMaterialization;
    private long _nextProjectRuntimeInformationRefresh;
    private TimelineSelectionOperationContext? _timelineSelectionOperationContext;
    private TimelineSelectionOperationContext? _timelineQuantizeOperationContext;
    private readonly TimelineCommandTarget _lastTimelineCommandTarget = new();
    private TimelineSurface? _lastTimelineCommandSurface
    {
        get => _lastTimelineCommandTarget.Resolve(_session.ActiveWorkspace);
        set => _lastTimelineCommandTarget.Set(value);
    }

    private enum TimelineSelectionObjectKind
    {
        Segments,
        MidiSegments,
        MixedSegments,
        LogicalNotes,
        LogicalParameterPoints,
        DirectMidiNotes,
        DirectMidiEventPoints,
        TemplateNotes,
        SubVoiceEventPoints
    }

    private sealed record TimelineSelectionOperationContext(
        TimelineSelectionObjectKind Kind,
        CompressedMidoraIdSet Ids,
        MidoraId? OwnerId = null,
        MidoraId? SecondaryId = null,
        MidiValueTarget? MidiTarget = null,
        DirectMidiEventLaneTarget? DirectMidiTarget = null,
        double PointMinimum = 0,
        double PointMaximum = 127,
        CompressedMidoraIdSet? RetainedIds = null,
        long? FrozenSelectionRevision = null);

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _session;
        _session.PropertyChanged += OnClipboardSessionChanged;
        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseDown += OnPreviewMouseDownForPlaybackShortcut;
        Deactivated += OnWindowDeactivated;
        ContextMenuOpening += OnEditingSurfaceContextMenuOpening;
        AddHandler(TimelineSurface.SelectionActionRequestedEvent,
            new EventHandler<TimelineSelectionActionEventArgs>(OnSelectionActionRequested));
        AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnSelectionCommandSurfaceLoaded), true);
        MainMenu.AddHandler(
            MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler(OnMainMenuSubmenuOpened),
            handledEventsToo: true);
        AddHandler(
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnAnyTabSelectionChangedForPreviewPriority),
            handledEventsToo: true);
        AddHandler(
            Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnKeyboardFocusChangedForInputMethod),
            handledEventsToo: true);
        AddHandler(
            TimelineSurface.AltGestureConsumedEvent,
            new RoutedEventHandler(OnTimelineAltGestureConsumed));
        AddHandler(TimelineSurface.TickRangeExceededEvent, new RoutedEventHandler((_, e) =>
        {
            e.Handled = true;
            ShowError("Timeline edit", "The requested Tick or duration exceeds the supported 64-bit range. The gesture was cancelled; no edit was applied.");
        }));
        LoadDesktopPreferences();
        ReloadInstrumentCatalogSnapshot(reportNoticeInStatus: true);
        _playbackTimer = new(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _playbackTimer.Start();
        if (_recentProjects.StartupNotice is not null)
        {
            _session.SetStatusMessage(_recentProjects.StartupNotice.Message, isError: true);
        }
    }

    public async Task HandleStartupRequestAsync(ApplicationStartupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? candidate = request.Arguments
            .Select(argument => Path.IsPathFullyQualified(argument)
                ? argument
                : Path.GetFullPath(Path.Combine(request.WorkingDirectory, argument)))
            .FirstOrDefault(path => string.Equals(
                Path.GetExtension(path), ".midora", StringComparison.OrdinalIgnoreCase));
        if (candidate is null) return;
        if (!File.Exists(candidate))
        {
            ShowError("Open Project", $"The requested Project does not exist.\n\n{candidate}");
            return;
        }
        if (!StopPlaybackForProjectCommand("Open Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        Exception? audioInitializationFailure = null;
        if (await RunOperationAsync(
                "Open Project",
                async cancellationToken =>
                {
                    await _session.OpenProjectAsync(candidate, cancellationToken: cancellationToken);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
                },
                canCancel: false))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(candidate));
            RecordRecentProject(candidate);
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        _session.PropertyChanged -= OnClipboardSessionChanged;
        ClearTimelineCommandTargets();
        _projectClipboard?.Dispose();
        _projectClipboard = null;
        _clipboardDocument = null;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(OnWindowMessage);
            _windowSource = null;
        }
        _playbackTimer.Stop();
        Interlocked.Exchange(ref _instrumentLoopCommitDelay, null)?.Cancel();
        Interlocked.Exchange(ref _timelineSelectionMaterialization, null)?.Cancel();
        await _session.DisposeAsync();
        SaveDesktopPreferences();
        base.OnClosed(e);
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("New Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        NewProjectDialog dialog = new(
            ExistingRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy))
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.Request is null) return;
        NewProjectCreationRequest request = dialog.Request;
        Exception? audioInitializationFailure = null;
        if (await RunOperationAsync(
                "Create Project",
                async cancellationToken =>
                {
                    await _session.CreateProjectAsync(request, cancellationToken);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
                },
                canCancel: false))
        {
            if (request.TargetPath is string targetPath)
            {
                RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(targetPath));
                RecordRecentProject(targetPath);
            }
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    private async void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("Open Project") || !await ConfirmCloseCurrentProjectAsync()) return;
        OpenFileDialog dialog = new()
        {
            Title = "Open Midora Project",
            Filter = "Midora Project (*.midora;*.zip)|*.midora;*.zip|Midora Package (*.midora)|*.midora|ZIP Package (*.zip)|*.zip|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.OpenProject)
        };
        if (dialog.ShowDialog(this) != true) return;
        Exception? audioInitializationFailure = null;
        if (await RunOperationAsync(
                "Open Project",
                async cancellationToken =>
                {
                    await _session.OpenProjectAsync(
                        dialog.FileName,
                        cancellationToken: cancellationToken);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
                },
                canCancel: false))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(dialog.FileName));
            RecordRecentProject(dialog.FileName);
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    private async void OnSaveProjectClick(object sender, RoutedEventArgs e) => await SaveProjectAsync();

    private async Task<bool> SaveProjectAsync()
    {
        if (!_session.HasProject) return true;
        if (_session.HasDamagedProjectObjects)
        {
            _session.Notice = "Saving is disabled while damaged Arrangement object placeholders remain. Delete every damaged placeholder first, or discard this Project session.";
            return false;
        }
        if (!StopPlaybackForProjectCommand("Save Project")) return false;
        string? firstPath = null;
        ProjectPersistenceCoordinator? persistence = _session.Persistence;
        if (persistence?.RequiresFormatUpgrade == true)
        {
            int sourceFormat = persistence.LegacySourceFileFormatVersion
                ?? throw new InvalidOperationException(
                    "The migrated Project has no source format version.");
            string sourcePath = persistence.ProtectedSourceProjectPath
                ?? throw new InvalidOperationException(
                    "The migrated Project has no source path.");
            MidoraLegacyProjectUpgradePlanV3? upgradePlan = null;
            bool prepared = await RunOperationAsync(
                "Prepare Project Upgrade",
                async cancellationToken => upgradePlan =
                    await _session.PrepareLegacyProjectUpgradeAsync(cancellationToken),
                canCancel: false);
            if (!prepared || upgradePlan is null)
            {
                return false;
            }
            MessageBoxResult decision = MessageDialog.Show(
                $"This Project was opened from Format {sourceFormat}. Saving will create a permanent exact-byte backup beside the original file, then replace the original path with Format {PersistenceContractV4.FileFormatVersion}.\n\nSource:\n{sourcePath}\n\nOriginal-byte backup:\n{upgradePlan.PermanentBackupPath}\n\nContinue?",
                "Upgrade Project Format",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (decision != MessageBoxResult.Yes)
            {
                return false;
            }
            string? backupPath = null;
            bool upgraded = await RunOperationAsync(
                "Upgrade and Save Project",
                async () => backupPath = await _session.UpgradeLegacyProjectInPlaceAsync(upgradePlan));
            if (upgraded && backupPath is not null)
            {
                RecordRecentDirectory(
                    RecentDirectoryPurpose.SaveAndSaveCopy,
                    Path.GetDirectoryName(sourcePath));
                RecordRecentProject(sourcePath);
                _session.Notice = $"Project upgraded to Format {PersistenceContractV4.FileFormatVersion}. Original bytes preserved at: {backupPath}";
            }
            return upgraded;
        }
        if (persistence?.CurrentProjectPath is null)
        {
            SaveFileDialog dialog = CreateProjectSaveDialog("Save Midora Project");
            if (dialog.ShowDialog(this) != true) return false;
            firstPath = dialog.FileName;
        }
        MidoraProjectSaveResultV1? saveResult = null;
        bool saved = await RunOperationAsync(
            "Save Project",
            async () => saveResult = await _session.SaveProjectAsync(
                firstPath,
                overwriteAuthorized: firstPath is not null && File.Exists(firstPath)));
        if (saved && (firstPath ?? _session.Persistence?.CurrentProjectPath) is string path)
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(path));
            RecordRecentProject(path);
            ReportOmittedPresentationSections(saveResult);
        }
        return saved;
    }

    private void ReportOmittedPresentationSections(MidoraProjectSaveResultV1? result)
    {
        if (result?.OmittedPresentationSections is not { Count: > 0 } omitted)
        {
            return;
        }

        string list = string.Join(Environment.NewLine, omitted.Select(section => $"• {section}"));
        _session.SetStatusMessage(
            "音乐已保存，但部分视图状态未保存。",
            details: $"以下 presentation 分区因损坏或预算限制被完整省略：{Environment.NewLine}{list}",
            detailsTitle: "Presentation Save");
    }

    private async void OnSaveCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        if (_session.HasDamagedProjectObjects)
        {
            _session.Notice = "Save Copy is disabled while damaged Arrangement object placeholders remain.";
            return;
        }
        if (!StopPlaybackForProjectCommand("Save Project Copy")) return;
        SaveFileDialog dialog = CreateProjectSaveDialog("Save Project Copy");
        if (dialog.ShowDialog(this) != true) return;
        MidoraProjectSaveResultV1? saveResult = null;
        if (await RunOperationAsync(
            "Save Project Copy",
            async () => saveResult = await _session.SaveCopyAsync(
                dialog.FileName,
                overwriteAuthorized: File.Exists(dialog.FileName))))
        {
            RecordRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy, Path.GetDirectoryName(dialog.FileName));
            ReportOmittedPresentationSections(saveResult);
        }
    }

    private async void OnApplicationPreferencesClick(object sender, RoutedEventArgs e)
        => await OpenApplicationPreferencesAsync(ApplicationPreferencesPage.Audio);

    private async void OnSoundFontStatusMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await OpenApplicationPreferencesAsync(ApplicationPreferencesPage.SoundFonts);
    }

    private async Task OpenApplicationPreferencesAsync(ApplicationPreferencesPage initialPage)
    {
        if (!PrepareForModalSurface()) return;
        if (!_session.CanStartForegroundTask)
        {
            _session.SetStatusMessage(
                "Stop playback and wait for the current foreground task before changing Application Preferences.",
                isError: true);
            return;
        }
        ApplicationPreferencesDialog dialog = new(
            _preferences,
            _instrumentCatalog,
            TryPublishApplicationPreferences, initialPage) { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Result is null) return;

        ApplicationPreferences preferences = dialog.Result;
        bool rebuildAudioWorker = _session.RequiresAudioWorkerRebuild(preferences);
        if (!rebuildAudioWorker)
        {
            try
            {
                await _session.ApplyApplicationPreferencesAsync(preferences);
                _preferences = preferences;
                RefreshInstrumentCatalogResolver();
                _session.SetStatusMessage("Application Preferences were saved and applied.");
            }
            catch (Exception exception)
            {
                _preferences = preferences;
                RefreshInstrumentCatalogResolver();
                ShowError(
                    "Application Preferences",
                    $"Preferences were saved, but could not be applied: {exception.Message}");
            }
            return;
        }

        bool applied = await RunOperationAsync(
            "Saving Settings",
            async cancellationToken =>
            {
                _preferences = preferences;
                await _session.ApplyApplicationPreferencesAsync(
                    preferences,
                    cancellationToken);
            },
            canCancel: false,
            lockLevel: DesktopTaskLockLevel.FullApplication);
        if (applied)
        {
            _preferences = preferences;
            RefreshInstrumentCatalogResolver();
            _session.SetStatusMessage(
                "Application Preferences were saved; the audio Worker is ready.");
        }
        else
        {
            // The durable settings and in-memory preference snapshot must agree even when
            // operational BASS initialization fails. A later settings Apply or playback attempt
            // can retry initialization without silently reverting what was saved.
            _preferences = preferences;
            RefreshInstrumentCatalogResolver();
        }
    }

    private string? TryPublishApplicationPreferences(ApplicationPreferences preferences)
    {
        try
        {
            ApplicationPreferencesSaveResult saved = _preferenceStore.Save(preferences);
            return saved.Succeeded
                ? null
                : saved.Notice?.Message ?? "Application Preferences could not be saved.";
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException)
        {
            return exception.Message;
        }
    }

    private void OnInstrumentCatalogsClick(object sender, RoutedEventArgs e)
    {
        if (!PrepareForModalSurface())
        {
            return;
        }

        ReloadInstrumentCatalogSnapshot(reportNoticeInStatus: false);
        if (_instrumentCatalogNotice is not null)
        {
            MessageDialog.Show(
                this,
                _instrumentCatalogNotice.Message,
                "Instrument Catalogs",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        if (!_instrumentCatalogCanPublish)
        {
            return;
        }

        InstrumentCatalogDialog dialog = new(
            _instrumentCatalog,
            _preferences.SoundFonts,
            TryPublishInstrumentCatalog)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.Result is null)
        {
            return;
        }

        _instrumentCatalog = dialog.Result;
        _instrumentCatalogCanPublish = true;
        _instrumentCatalogNotice = null;
        RefreshInstrumentCatalogResolver();
        _session.SetStatusMessage("Instrument Catalogs were saved.");
    }

    private string? TryPublishInstrumentCatalog(InstrumentCatalogState catalog)
    {
        try
        {
            InstrumentCatalogSaveResult saved = _instrumentCatalogStore.Save(catalog);
            return saved.Succeeded
                ? null
                : saved.Notice?.Message ?? "Instrument Catalogs could not be saved.";
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or InvalidDataException
            or InvalidOperationException
            or NotSupportedException
            or OverflowException)
        {
            return exception.Message;
        }
    }

    private async void OnCloseProjectClick(object sender, RoutedEventArgs e)
    {
        if (StopPlaybackForProjectCommand("Close Project") && await ConfirmCloseCurrentProjectAsync()) await _session.CloseProjectAsync();
    }

    private bool StopPlaybackForProjectCommand(string command)
    {
        if (!PrepareForModalSurface()) return false;
        if (_operationInProgress)
        {
            _session.SetStatusMessage(
                $"{command} cannot start while another foreground task is running. Midora does not queue tasks.",
                isError: true);
            return false;
        }
        if (!_session.IsPlaybackActive) return true;
        try
        {
            _session.StopPlayback();
            return !_session.IsPlaybackActive;
        }
        catch (Exception exception)
        {
            ShowError(command, $"Playback cleanup did not complete: {exception.Message}");
            return false;
        }
    }

    private async Task<bool> ConfirmCloseCurrentProjectAsync()
    {
        if (!_session.HasProject
            || (_session.Document?.IsModified != true
                && _session.Persistence?.CurrentProjectPath is not null))
        {
            return true;
        }
        if (_session.HasDamagedProjectObjects)
        {
            return MessageDialog.Show(
                this,
                "This Project has unsaved changes and damaged object placeholders. Saving is prohibited until every damaged placeholder is deleted. Close and discard the current session changes?",
                "Discard Unsavable Project Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        MessageBoxResult result = MessageDialog.Show(
            this,
            "Save changes to the current Project before closing it?",
            "Unsaved Project Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => await SaveProjectAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => RunHistoryTransition(redo: false);
    private void OnRedoClick(object sender, RoutedEventArgs e) => RunHistoryTransition(redo: true);
    private async void RunHistoryTransition(bool redo)
    {
        if (!_session.CanEditProject || _session.Document is not ProjectDocumentSession document
            || (redo ? !document.CanRedo : !document.CanUndo)) return;
        WorkspaceViewModel? workspace = _session.ActiveWorkspace;
        TimelineSurface? source = _lastTimelineCommandSurface;
        _session.CaptureSelectionBeforeHistoryTransition();
        await RunOperationAsync(redo ? "Redo" : "Undo", async token =>
        {
            _session.ActiveForegroundTask?.Report("Preparing history transition...", 0);
            PreparedProjectHistoryTransition prepared = await Task.Run(
                () => document.PrepareHistoryTransition(redo, token), token);
            _session.ActiveForegroundTask!.SealCancellationBeforePublication();
            _session.PublishHistoryTransition(prepared);
        }, canCancel: true);
        if (workspace is not null) RestoreModalCommandFocus(workspace, source);
    }
    private void OnNavigateBackClick(object sender, RoutedEventArgs e) => _session.NavigateBack();
    private void OnNavigateForwardClick(object sender, RoutedEventArgs e) => _session.NavigateForward();

    private void OnWorkspaceTabListClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ContextMenu menu = new() { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (WorkspaceViewModel workspace in _session.Workspaces)
        {
            MenuItem item = new()
            {
                Header = workspace.Header,
                IsCheckable = true,
                IsChecked = ReferenceEquals(workspace, _session.ActiveWorkspace),
                Tag = workspace
            };
            item.Click += (_, _) => _session.ActiveWorkspace = (WorkspaceViewModel)item.Tag;
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No open Workspaces", IsEnabled = false });
        }
        menu.IsOpen = true;
    }
    private void OnCutClick(object sender, RoutedEventArgs e) => CutOrCopyProjectSelection(cut: true);
    private void OnCopyClick(object sender, RoutedEventArgs e) => CutOrCopyProjectSelection(cut: false);
    private void OnPasteClick(object sender, RoutedEventArgs e) => PasteProjectSelection();
    private void OnSelectAllClick(object sender, RoutedEventArgs e) => SelectAllInFocusedScope();
    private void OnDuplicateClick(object sender, RoutedEventArgs e) => DuplicateFocusedSelection();

    private async void OnPlayClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.StartPlaybackAsync();
            _spaceStartedPlayback = true;
        }
        catch (Exception exception)
        {
            _spaceStartedPlayback = false;
            _session.SetStatusMessage($"Play: {exception.Message}", isError: true);
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, RestorePlaybackShortcutFocus);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _spaceStartedPlayback = false;
        RunSynchronous("Stop", _session.StopPlayback);
    }

    private void OnPrimaryTransportClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsPlaybackActive)
        {
            OnStopClick(sender, e);
        }
        else
        {
            OnPlayClick(sender, e);
        }
    }

    private void OnLoopClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle) return;
        if (toggle.IsChecked != true)
        {
            RunSynchronous("Disable Loop", () => _session.SetLoopRange(null));
            return;
        }
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                TimeRangeStartTick: long start,
                TimeRangeEndTick: long end
            } timeline
            || end <= start)
        {
            ShowUnavailable("Enable Loop", "Create a non-empty Time Range in the active timeline first.");
            toggle.IsChecked = false;
            return;
        }
        RunSynchronous("Enable Loop", () => _session.SetLoopRange(
            timeline.ToProjectRange(_session.Project!, start, end)));
    }

    private async void OnResetPlaybackClick(object sender, RoutedEventArgs e)
    {
        Exception? audioInitializationFailure = null;
        bool completed = await RunOperationAsync(
            "Reset Playback Engine",
            async cancellationToken =>
            {
                _session.ResetPlaybackEngine();
                audioInitializationFailure =
                    await InitializeAudioWorkerForActiveProjectAsync(cancellationToken);
            },
            canCancel: false,
            lockLevel: DesktopTaskLockLevel.FullApplication);
        if (completed)
        {
            ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        _session.RefreshCompilationProgress();
        long now = Environment.TickCount64;
        if (now >= _nextProjectRuntimeInformationRefresh)
        {
            _nextProjectRuntimeInformationRefresh = now + 1_000;
            _session.RefreshProjectRuntimeInformation();
        }
        if (!_session.IsPlaybackActive) return;
        try
        {
            _session.UpdatePlayback();
            FollowActivePlayback(force: false);
        }
        catch (Exception exception)
        {
            ShowError("Playback", exception.Message);
        }
    }

    private void FollowActivePlayback(bool force)
    {
        if (_session.ActiveWorkspace is not IPlaybackTimelineWorkspace timeline)
        {
            return;
        }

        long? startTick = TimelinePlaybackFollowPolicy.ResolveStartTick(
            _preferences.DesktopUi.FollowPlayback,
            _session.IsPlaybackActive,
            _followPlaybackViewportInteractionActive,
            timeline.StartTick,
            timeline.TickSpan,
            timeline.PlaybackCursorTick,
            force);
        if (startTick is long resolvedStartTick)
        {
            timeline.StartTick = resolvedStartTick;
        }
    }

    private bool CanTemporarilySuspendPlaybackFollow() =>
        _preferences.DesktopUi.FollowPlayback
        && _session.IsPlaybackActive
        && _session.ActiveWorkspace is IPlaybackTimelineWorkspace { PlaybackCursorTick: not null };

    internal void OnFollowViewportPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool beginsExplicitViewportDrag = sender switch
        {
            TimelineOverviewSurface => e.ChangedButton == MouseButton.Left,
            TimelineSurface => e.ChangedButton == MouseButton.Middle,
            _ => false
        };
        if (beginsExplicitViewportDrag && CanTemporarilySuspendPlaybackFollow())
        {
            _followPlaybackViewportInteractionActive = true;
        }
    }

    internal void OnFollowViewportPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        bool endsExplicitViewportDrag = sender switch
        {
            TimelineOverviewSurface => e.ChangedButton == MouseButton.Left,
            TimelineSurface => e.ChangedButton == MouseButton.Middle,
            _ => false
        };
        if (endsExplicitViewportDrag)
        {
            EndFollowPlaybackViewportInteraction();
        }
    }

    internal void OnFollowViewportLostMouseCapture(object sender, MouseEventArgs e) =>
        EndFollowPlaybackViewportInteraction();

    private void EndFollowPlaybackViewportInteraction()
    {
        if (!_followPlaybackViewportInteractionActive)
        {
            return;
        }

        _followPlaybackViewportInteractionActive = false;
        FollowActivePlayback(force: true);
    }

    internal void OnFollowOverviewPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CanTemporarilySuspendPlaybackFollow())
        {
            e.Handled = true;
        }
    }

    private void OnNewTrackClick(object sender, RoutedEventArgs e)
    {
        QueueCreateLogicalTrack(sender, insertionIndex: null);
    }

    private void OnTrackHeaderNewTrackClick(object sender, RoutedEventArgs e)
    {
        int? insertionIndex = null;
        if (_session.Project is MidoraProject project
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel arrangement
            && _trackHeaderContextLane is int lane
            && arrangement.GetArrangementLane(lane) is { ObjectId: MidoraId trackId })
        {
            int currentIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
            if (currentIndex >= 0) insertionIndex = currentIndex + 1;
        }
        QueueCreateLogicalTrack(sender, insertionIndex);
    }

    private void QueueCreateLogicalTrack(object sender, int? insertionIndex)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (!_session.HasProject) return;
            RunSynchronous("Create Logical Track", () =>
            {
                _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                    insertionIndex: insertionIndex));
                _session.OpenArrangement();
            });
        });
    }

    private void OnNewTrackWithInstrumentClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (_session.Project is not MidoraProject project) return;
            NewLogicalTrackWithInstrumentDialog dialog = new(project.EventInstruments)
            {
                Owner = this
            };
            if (ShowModalDialog(dialog) != true) return;
            if (dialog.CreatesInstrument)
            {
                RunSynchronous("Create Logical Track with Event Instrument", () =>
                {
                    _session.Execute(
                        ProjectDomainEditCommands.CreateLogicalTrackWithNewEventInstrument(
                            dialog.NewInstrumentName));
                    _session.OpenInstrument(project.EventInstruments[^1].Id);
                });
                return;
            }
            else if (dialog.ExistingInstrumentId is MidoraId instrumentId)
            {
                RunSynchronous("Create Logical Track", () =>
                    _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                        eventInstrumentId: instrumentId)));
            }
            _session.OpenArrangement();
        });
    }

    private void OnNewRawMidiTrackClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (_session.Project is not MidoraProject project) return;
            NewRawMidiTrackDialog dialog = new(project.MidiChannelRoots) { Owner = this };
            if (ShowModalDialog(dialog) != true) return;
            RunSynchronous("Create Raw MIDI Track", () =>
                _session.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
                    dialog.TrackName,
                    dialog.RoutingMode,
                    dialog.OneBasedPort,
                    dialog.OneBasedChannel,
                    dialog.ChannelMode)));
            _session.OpenArrangement();
        });
    }

    private void OnNewInstrumentClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () =>
        {
            if (!_session.HasProject) return;
            RunSynchronous("Create Event Instrument", () =>
            {
                _session.Execute(ProjectDomainEditCommands.CreateEventInstrument());
                EventInstrument created = _session.Project!.EventInstruments[^1];
                _session.OpenInstrument(created.Id);
            });
        });
    }

    private void RunAfterMenuClosed(object sender, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!PrepareForModalSurface()) return;

        // A Popup owns a separate HWND. Close it and let the current routed input
        // event unwind before an edit rebuilds ItemsSource-backed WPF collections.
        // Mutating the visual tree synchronously from the Popup button's Click route
        // can leave mouse capture and layout processing in a re-entrant state.
        NewProjectItemPopup.IsOpen = false;
        NewProjectItemButton.IsChecked = false;
        _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private void OnProjectTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        e.Handled = true;
        QueueOpenWorkspace(node);
    }

    private void QueueOpenWorkspace(ProjectTreeNode node) =>
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                try { _session.OpenWorkspace(node); }
                catch (InvalidOperationException exception)
                {
                    _session.SetStatusMessage(exception.Message, isError: true);
                }
            },
            DispatcherPriority.Normal);

    private void OnProjectTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void OnProjectTreeLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _projectTreeDragStart = null;
        _projectTreeDragNode = null;
        if (!_session.CanEditProject || _session.IsProjectTreeFiltered) return;
        TreeViewItem? item = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ProjectTreeNode node
            || node.Kind is not (ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.EventInstrument))
        {
            return;
        }
        _projectTreeDragStart = e.GetPosition(ProjectTree);
        _projectTreeDragNode = node;
    }

    private void OnProjectTreeMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _projectTreeDragStart is not Point origin
            || _projectTreeDragNode is not ProjectTreeNode source
            || !_session.CanEditProject
            || _session.IsProjectTreeFiltered)
        {
            return;
        }
        Point current = e.GetPosition(ProjectTree);
        if (!HasReachedUiReorderDragThreshold(origin, current))
        {
            return;
        }
        _projectTreeDragStart = null;
        _projectTreeDragNode = null;
        DataObject data = new(ProjectTreeDragFormat, source);
        _ = DragDrop.DoDragDrop(ProjectTree, data, DragDropEffects.Move | DragDropEffects.Link);
    }

    private void OnProjectTreeDragOver(object sender, DragEventArgs e)
    {
        ProjectTreeNode? source = e.Data.GetDataPresent(ProjectTreeDragFormat)
            ? e.Data.GetData(ProjectTreeDragFormat) as ProjectTreeNode
            : null;
        ProjectTreeNode? target = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            as ProjectTreeNode;
        e.Effects = GetProjectTreeDropEffect(source, target);
        e.Handled = true;
    }

    private void OnProjectTreeDrop(object sender, DragEventArgs e)
    {
        ProjectTreeNode? source = e.Data.GetDataPresent(ProjectTreeDragFormat)
            ? e.Data.GetData(ProjectTreeDragFormat) as ProjectTreeNode
            : null;
        ProjectTreeNode? target = FindVisualAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext
            as ProjectTreeNode;
        DragDropEffects effect = GetProjectTreeDropEffect(source, target);
        e.Effects = effect;
        e.Handled = true;
        if (source is null || target is null || effect == DragDropEffects.None || _session.Project is null) return;
        if (effect == DragDropEffects.Link)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Input,
                () => RunSynchronous(
                    "Project Tree Drag and Drop",
                    () => ApplyProjectTreeDrop(source, target)));
            return;
        }
        RunSynchronous("Project Tree Drag and Drop", () => ApplyProjectTreeDrop(source, target));
    }

    private DragDropEffects GetProjectTreeDropEffect(ProjectTreeNode? source, ProjectTreeNode? target)
    {
        if (source is null
            || target is null
            || ReferenceEquals(source, target)
            || !_session.CanEditProject
            || _session.IsProjectTreeFiltered)
        {
            return DragDropEffects.None;
        }
        return (source.Kind, target.Kind) switch
        {
            (ProjectTreeNodeKind.LogicalTrack, ProjectTreeNodeKind.LogicalTrack) => DragDropEffects.Move,
            (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.EventInstrument) => DragDropEffects.Move,
            (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.LogicalTracks) => DragDropEffects.Link,
            _ => DragDropEffects.None
        };
    }

    private void ApplyProjectTreeDrop(ProjectTreeNode source, ProjectTreeNode target)
    {
        MidoraProject project = _session.Project
            ?? throw new InvalidOperationException("No Project is open.");
        if (source.ObjectId is not MidoraId sourceId)
        {
            throw new InvalidOperationException("The dragged Project object is unavailable.");
        }
        switch (source.Kind, target.Kind)
        {
            case (ProjectTreeNodeKind.LogicalTrack, ProjectTreeNodeKind.LogicalTrack)
                when target.ObjectId is MidoraId targetTrackId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.Tracks.FindIndex(item => item.Id == targetTrackId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.EventInstrument)
                when target.ObjectId is MidoraId targetInstrumentId:
                _session.ReorderProjectTreeNode(
                    source,
                    project.EventInstruments.FindIndex(item => item.Id == targetInstrumentId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTracks):
                EventInstrument instrument = project.EventInstruments.Single(item => item.Id == sourceId);
                _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(eventInstrumentId: sourceId));
                return;
            case (ProjectTreeNodeKind.EventInstrument, ProjectTreeNodeKind.LogicalTrack)
                when target.ObjectId is MidoraId targetTrackId:
                LogicalTrack track = project.Tracks.Single(item => item.Id == targetTrackId);
                if (project.ResolveEventInstrumentDefinitionId(track) is MidoraId currentId
                    && currentId != sourceId)
                {
                    EventInstrument? current = project.EventInstruments.FirstOrDefault(item => item.Id == currentId);
                    EventInstrument replacement = project.EventInstruments.Single(item => item.Id == sourceId);
                    if (MessageDialog.Show(
                            this,
                            $"Rebind Logical Track '{track.Name}' from '{current?.Name ?? track.LastBoundEventInstrumentName ?? "Unavailable Event Instrument"}' to '{replacement.Name}'? Existing Segments and Logical Parameter lanes are preserved; incompatible references will be diagnosed and are not repaired automatically.",
                            "Rebind Logical Track",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }
                _session.BindLogicalTrack(targetTrackId, sourceId);
                return;
            default:
                throw new InvalidOperationException("This Project Tree drop target is not supported.");
        }
    }

    internal static T? FindVisualAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null && current is not T)
        {
            current = GetUiParent(current);
        }
        return current as T;
    }

    private static DependencyObject? GetUiParent(DependencyObject current)
    {
        if (current is ContentElement content)
        {
            return ContentOperations.GetParent(content)
                ?? (content as FrameworkContentElement)?.Parent;
        }
        if (current is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(current)
                ?? (current as FrameworkElement)?.Parent
                ?? (current as FrameworkElement)?.TemplatedParent;
        }
        return LogicalTreeHelper.GetParent(current);
    }

    internal static bool HasReachedUiReorderDragThreshold(Point origin, Point current)
    {
        double horizontal = current.X - origin.X;
        double vertical = current.Y - origin.Y;
        double systemDistance = Math.Sqrt(
            SystemParameters.MinimumHorizontalDragDistance
                * SystemParameters.MinimumHorizontalDragDistance
            + SystemParameters.MinimumVerticalDragDistance
                * SystemParameters.MinimumVerticalDragDistance);
        double threshold = Math.Max(MinimumUiReorderDragDistance, systemDistance);
        return horizontal * horizontal + vertical * vertical >= threshold * threshold;
    }

    private void ExecuteAndSelectCreated(
        IProjectEditCommand command,
        WorkspaceViewModel workspace)
    {
        MidoraProject project = _session.Project
            ?? throw new InvalidOperationException("No Project is open.");
        long firstNewStableId = project.NextStableId;
        _session.Execute(command);
        SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
    }

    private WorkspaceViewModel? _preparedSelectionWorkspace;
    private long _preparedSelectionRevision = -1;

    private void SelectCreatedWorkspaceObjects(
        WorkspaceViewModel workspace,
        long firstNewStableId,
        bool replaceSelectionWhenNoObjectSurvives = false,
        WorkspaceTimelineSelectionSource? timelineSource = null)
    {
        if (ReferenceEquals(workspace, _preparedSelectionWorkspace)
            && _session.Document?.PublicationRevision == _preparedSelectionRevision) return;
        SelectCreatedWorkspaceObjects(
            _session,
            workspace,
            firstNewStableId,
            replaceSelectionWhenNoObjectSurvives,
            timelineSource);
    }

    internal static void SelectCreatedWorkspaceObjects(
        DesktopSessionController session,
        WorkspaceViewModel workspace,
        long firstNewStableId,
        bool replaceSelectionWhenNoObjectSurvives = false,
        WorkspaceTimelineSelectionSource? timelineSource = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(workspace);
        if (session.Project is not MidoraProject project) return;
        MidoraId? createdParameter = null;
        static CompressedMidoraIdSet NewIds<T>(IEnumerable<T> items, Func<T, MidoraId> id, long first) =>
            CompressedMidoraIdSet.Create(items.Select(id).Where(value => value.Value >= first));

        CompressedMidoraIdSet created = workspace switch
        {
            TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } =>
                NewIds(project.Tracks.SelectMany(item => item.Segments), item => item.Id, firstNewStableId),
            TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } => SegmentSelection(segmentId),
            TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor } conductor =>
                CompressedMidoraIdSet.Create(conductor.EnumerateConductorIds()
                    .Where(id => id.Value >= firstNewStableId)),
            InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } =>
                InstrumentSelection(instrumentId),
            _ => CompressedMidoraIdSet.Empty
        };
        if (created.Count == 0)
        {
            if (replaceSelectionWhenNoObjectSurvives)
            {
                workspace.Selection.Clear();
                session.RefreshWorkspaceSelection(workspace);
            }
            return;
        }
        if (timelineSource is WorkspaceTimelineSelectionSource source)
        {
            workspace.Selection.ApplyRange(
                created,
                WorkspaceSelectionRangeMode.Replace,
                source);
        }
        else
        {
            workspace.Selection.Clear();
            foreach (MidoraId id in created) workspace.Selection.Add(id, makePrimary: false);
        }
        if (createdParameter is { } parameter && workspace is TimelineWorkspaceViewModel timeline)
        {
            // Creating a Lane is explicit navigation. Ordinary selection or
            // passive rebuilds must not infer navigation from selected points.
            session.RefreshWorkspace(workspace);
            timeline.ActiveParameterLaneIndex = timeline.ParameterLaneOptions.ToList().FindIndex(value => value.ParameterId == parameter);
            timeline.PreferCurrentParameterLaneOnNextRebuild();
        }
        session.RefreshWorkspaceSelection(workspace);
        return;

        CompressedMidoraIdSet SegmentSelection(MidoraId segmentId)
        {
            if (TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { Segment: var logical })
            {
                var lanes = NewIds(logical.ParameterLanes, static value => value.Id, firstNewStableId);
                if (lanes.Count != 0) { createdParameter = logical.ParameterLanes.First(lane => lanes.Contains(lane.Id)).ParameterId; return lanes; }
                return CompressedMidoraIdSet.Create(CreatedIn(logical.Notes.CreateQuerySnapshot())
                    .Concat(logical.ParameterLanes.SelectMany(lane => CreatedIn(lane.Points.CreateQuerySnapshot()))));
            }
            if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { Segment: var midi })
                return CompressedMidoraIdSet.Empty;
            var notes = CompressedMidoraIdSet.Create(CreatedIn(midi.Notes.CreateObjectSource()));
            if (notes.Count != 0) return notes;
            var events = CompressedMidoraIdSet.Create(CreatedIn(midi.ChannelEvents.CreateObjectSource()));
            return events.Count != 0 ? events : CompressedMidoraIdSet.Create(CreatedIn(midi.OpaqueEvents.CreateObjectSource()));
        }

        IEnumerable<MidoraId> CreatedIn<T>(ITimelineObjectSource<T> source)
        {
            for (long value = firstNewStableId; value < project.NextStableId; value++)
            {
                MidoraId id = new(value);
                if (source.TryFindOrdinalById(id, out _)) yield return id;
            }
        }

        CompressedMidoraIdSet InstrumentSelection(MidoraId instrumentId)
        {
            EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId);
            if (instrument is null) return CompressedMidoraIdSet.Empty;
            CompressedMidoraIdSet preferred = NewIds(instrument.SubVoices, item => item.Id, firstNewStableId);
            if (preferred.Count != 0) return preferred;
            preferred = NewIds(instrument.LogicalParameters, item => item.Id, firstNewStableId);
            if (preferred.Count != 0) return preferred;
            preferred = NewIds(instrument.ParameterMappings, item => item.Id, firstNewStableId);
            if (preferred.Count != 0) return preferred;
            preferred = NewIds(instrument.MappingFunctions, item => item.Id, firstNewStableId);
            if (preferred.Count != 0) return preferred;
            preferred = NewIds(instrument.Envelopes, item => item.Id, firstNewStableId);
            if (preferred.Count != 0) return preferred;
            preferred = CompressedMidoraIdSet.Create(instrument.SubVoices.SelectMany(item => CreatedIn(item.Events.CreateQuerySnapshot())));
            if (preferred.Count != 0) return preferred;
            preferred = NewIds(instrument.SubVoices.SelectMany(item => item.Curves), item => item.Id, firstNewStableId);
            if (preferred.Count != 0) return preferred;
            preferred = CompressedMidoraIdSet.Create(instrument.SubVoices.SelectMany(item => item.Curves).SelectMany(item => CreatedIn(item.Points.CreateQuerySnapshot())));
            if (preferred.Count != 0) return preferred;
            return workspace is InstrumentWorkspaceViewModel instrumentWorkspace
                ? CompressedMidoraIdSet.Create(instrumentWorkspace.MappingSteps.Select(item => item.Id)
                    .Where(id => id.Value >= firstNewStableId))
                : CompressedMidoraIdSet.Empty;
        }
    }

    private void OnWorkspaceTabsLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _workspaceTabDragStart = null;
        _workspaceTabDragWorkspace = null;
        if (_session.IsMainWindowTaskLocked
            || FindVisualAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }
        TabItem? item = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not WorkspaceViewModel workspace) return;
        if (!workspace.CanReorder) return;
        _workspaceTabDragStart = e.GetPosition(WorkspaceTabs);
        _workspaceTabDragWorkspace = workspace;
    }

    private void OnWorkspaceTabsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || _workspaceTabDragStart is not Point origin
            || _workspaceTabDragWorkspace is not WorkspaceViewModel workspace
            || _session.IsMainWindowTaskLocked)
        {
            return;
        }
        Point current = e.GetPosition(WorkspaceTabs);
        if (!HasReachedUiReorderDragThreshold(origin, current))
        {
            return;
        }
        _workspaceTabDragStart = null;
        _workspaceTabDragWorkspace = null;
        _ = DragDrop.DoDragDrop(
            WorkspaceTabs,
            new DataObject(WorkspaceTabDragFormat, workspace),
            DragDropEffects.Move);
    }

    private void OnWorkspaceTabsDragOver(object sender, DragEventArgs e)
    {
        WorkspaceViewModel? source = e.Data.GetDataPresent(WorkspaceTabDragFormat)
            ? e.Data.GetData(WorkspaceTabDragFormat) as WorkspaceViewModel
            : null;
        WorkspaceViewModel? target = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext
            as WorkspaceViewModel;
        e.Effects = source is not null
            && source.CanReorder
            && target is not null
            && !ReferenceEquals(source, target)
            && !_session.IsMainWindowTaskLocked
                ? DragDropEffects.Move
                : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWorkspaceTabsDrop(object sender, DragEventArgs e)
    {
        WorkspaceViewModel? source = e.Data.GetDataPresent(WorkspaceTabDragFormat)
            ? e.Data.GetData(WorkspaceTabDragFormat) as WorkspaceViewModel
            : null;
        WorkspaceViewModel? target = FindVisualAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext
            as WorkspaceViewModel;
        e.Handled = true;
        if (source is null || target is null || ReferenceEquals(source, target) || _session.IsMainWindowTaskLocked)
        {
            return;
        }
        int targetIndex = _session.Workspaces.IndexOf(target);
        if (targetIndex >= 0) _session.ReorderWorkspace(source, targetIndex);
    }

    private void OnWorkspaceTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkspaceTabs)) return;
        _logicalTrackShortcutTrackId = null;
        _arrangementHeaderShortcut = null;
        SnapMenuItem.IsChecked = GetActiveEditorSettings().SnapEnabled;
        Dispatcher.BeginInvoke(() =>
        {
            if (WorkspaceTabs.ItemContainerGenerator.ContainerFromItem(WorkspaceTabs.SelectedItem)
                is TabItem selected)
            {
                selected.BringIntoView();
            }
            if (_session.ActiveWorkspace is DiagnosticsWorkspaceViewModel)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (_session.ActiveWorkspace is DiagnosticsWorkspaceViewModel
                        && FindWorkspaceElement<FrameworkElement>("DiagnosticsWorkspaceFocusTarget")
                            is { IsVisible: true, IsEnabled: true } focusTarget)
                    {
                        focusTarget.Focus();
                    }
                });
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnProjectTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            BeginTreeRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteSelectedTreeNode();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ProjectTree.SelectedItem is ProjectTreeNode node && !node.IsRenaming)
        {
            OnTreeOpenClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnTreeOpenClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        QueueOpenWorkspace(node);
    }

    private void OnProjectTreeContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        menu.Items.Clear();
        void Add(
            string header,
            RoutedEventHandler handler,
            string? gesture = null,
            bool enabled = true)
        {
            MenuItem item = new()
            {
                Header = header,
                InputGestureText = gesture ?? string.Empty,
                IsEnabled = enabled
            };
            item.Click += handler;
            SetTimelineCommandIcon(item);
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new Separator());

        switch (node.Kind)
        {
            case ProjectTreeNodeKind.InstrumentLibrary:
                Add("New Event Instrument", OnNewInstrumentClick);
                Separator();
                Add("Paste Event Instrument", OnPasteTreeInstrumentClick, "Ctrl+V");
                break;
            case ProjectTreeNodeKind.LogicalTracks:
                Add("New Logical Track", OnNewTrackClick);
                Separator();
                Add(
                    "Paste Logical Track",
                    OnPasteTreeLogicalTrackClick,
                    "Ctrl+V",
                    CanPasteLogicalTrack());
                break;
            case ProjectTreeNodeKind.LogicalTrack:
                Add("Rename", OnTreeRenameClick, "F2");
                Add("Bind Event Instrument…", OnTreeBindInstrumentClick);
                Separator();
                Add("Cut", OnCutTreeLogicalTrackClick, "Ctrl+X", _session.CanEditProject);
                Add("Copy", OnCopyTreeLogicalTrackClick, "Ctrl+C");
                Add("Paste", OnPasteTreeLogicalTrackClick, "Ctrl+V", CanPasteLogicalTrack());
                Add("Duplicate", OnDuplicateTreeLogicalTrackClick, "Ctrl+D", _session.CanEditProject);
                Separator();
                Add("Move Up", OnTreeMoveUpClick);
                Add("Move Down", OnTreeMoveDownClick);
                Separator();
                Add("Delete…", OnTreeDeleteClick);
                break;
            case ProjectTreeNodeKind.EventInstrument:
                Add("Open", OnTreeOpenClick);
                Add("Rename", OnTreeRenameClick, "F2");
                Separator();
                Add("Copy", OnCopyTreeInstrumentClick, "Ctrl+C");
                Add("Paste", OnPasteTreeInstrumentClick, "Ctrl+V");
                Add("Duplicate", OnDuplicateTreeInstrumentClick, "Ctrl+D");
                Separator();
                Add("Move Up", OnTreeMoveUpClick);
                Add("Move Down", OnTreeMoveDownClick);
                Separator();
                Add("Delete…", OnTreeDeleteClick);
                break;
            case ProjectTreeNodeKind.DamagedEventInstrument:
            case ProjectTreeNodeKind.DamagedLogicalTrack:
                Add("Delete damaged placeholder…", OnTreeDeleteClick);
                break;
            default:
                Add("Open", OnTreeOpenClick);
                break;
        }
    }

    private void OnTimelineContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu
            || menu.PlacementTarget is not TimelineSurface surface)
        {
            return;
        }

        _lastTimelineCommandSurface = surface;
        if (surface.IsContextTargetPending)
        {
            menu.Items.Clear();
            menu.Items.Add(new MenuItem { Header = "Locating…", IsEnabled = false });
            return;
        }
        menu.Items.Clear();
        MenuItem Add(string header, RoutedEventHandler handler, string? gesture = null, bool enabled = true)
        {
            MenuItem item = new()
            {
                Header = header,
                InputGestureText = gesture ?? string.Empty,
                IsEnabled = enabled
            };
            item.Click += handler;
            SetTimelineCommandIcon(item);
            menu.Items.Add(item);
            return item;
        }
        void Separator() => menu.Items.Add(new Separator());

        Point contextPoint = menu.Tag is TimelineSelectionAction
            ? new Point(surface.LaneHeaderWidth + 8, surface.TimelineRulerHeight + 8)
            : surface.ContextTargetPosition ?? Mouse.GetPosition(surface);
        if (menu.Tag is not TimelineSelectionAction && PopulateTemplateMarkerMenu(menu, surface, contextPoint)) return;
        double headerWidth = surface.LaneHeaderWidth;
        bool isLaneHeader = contextPoint.X < headerWidth;
        if (surface.SurfaceMode == TimelineSurfaceMode.Arrangement && menu.Tag is not TimelineSelectionAction)
        {
            if (surface.IsArrangementEmptyBackground(contextPoint)
                && _session.ActiveWorkspace is TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Arrangement
                } arrangementWorkspace)
            {
                ClearArrangementTrackSelection(arrangementWorkspace);
            }
            _trackHeaderContextLane = surface.TryGetArrangementLaneHeader(contextPoint, out int contextLane)
                ? contextLane
                : null;
            _arrangementSharedGroupContextId = surface.TryGetArrangementSharedGroupHeaderTarget(
                    contextPoint,
                    out MidoraId sharedGroupId)
                ? sharedGroupId
                : null;
            surface.SetArrangementSharedGroupContextHighlight(
                _arrangementSharedGroupContextId);
        }
        if (isLaneHeader)
        {
            switch (surface.SurfaceMode)
            {
                case TimelineSurfaceMode.Arrangement:
                    if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor header))
                    {
                        Add("New Logical Track", OnNewTrackClick, enabled: _session.CanEditProject);
                        Add("New Logical Track with Instrument…", OnNewTrackWithInstrumentClick,
                            enabled: _session.CanEditProject);
                        Add("New MIDI Track…", OnNewRawMidiTrackClick, enabled: _session.CanEditProject);
                        return;
                    }
                    if (header.Kind is ArrangementLaneKind.DamagedEventInstrument
                        or ArrangementLaneKind.DamagedMidiChannelRoot
                        or ArrangementLaneKind.DamagedLogicalTrack
                        or ArrangementLaneKind.DamagedPureMidiTrack)
                    {
                        Add(
                            "Delete damaged placeholder…",
                            OnArrangementHeaderDeleteClick,
                            enabled: _session.CanEditProject);
                        return;
                    }
                    bool editable = _session.CanEditProject && header.Kind != ArrangementLaneKind.Conductor;
                    if (_arrangementSharedGroupContextId is MidoraId groupId)
                    {
                        if (header.SharedGroupMemberCount > 1)
                        {
                            MenuItem muteGroup = Add(
                                "Mute Shared Group",
                                OnArrangementSharedGroupMuteClick,
                                enabled: true);
                            muteGroup.IsCheckable = true;
                            muteGroup.IsChecked = _session.IsSharedGroupMuted(groupId);
                            MenuItem soloGroup = Add(
                                "Solo Shared Group",
                                OnArrangementSharedGroupSoloClick,
                                enabled: true);
                            soloGroup.IsCheckable = true;
                            soloGroup.IsChecked = _session.IsSharedGroupSolo(groupId);
                        }
                        if (header.Kind == ArrangementLaneKind.LogicalTrack)
                        {
                            Add(
                                "Change Event Instrument for Shared Group…",
                                OnArrangementSharedGroupInstrumentClick,
                                enabled: editable && _session.Project?.EventInstruments.Count > 0);
                        }
                        else if (header.Kind == ArrangementLaneKind.PureMidiTrack)
                        {
                            Add("MIDI Channel Settings…", OnArrangementSharedRootSettingsClick, enabled: editable);
                        }
                        if (header.IsSharedGroup)
                        {
                            (bool groupUp, bool groupDown) = ArrangementSharedGroupMoveAvailability(groupId);
                            Add("Move Shared Group Up", OnArrangementSharedGroupMoveUpClick,
                                enabled: editable && groupUp);
                            Add("Move Shared Group Down", OnArrangementSharedGroupMoveDownClick,
                                enabled: editable && groupDown);
                            Add(
                                "Make All Tracks Independent",
                                OnArrangementSharedGroupMakeIndependentClick,
                                enabled: editable);
                        }
                        Separator();
                        if (header.IsSharedGroup) return;
                    }
                    if (header.Kind == ArrangementLaneKind.Conductor)
                        Add("Open", OnArrangementHeaderOpenClick);
                    if (header.Kind != ArrangementLaneKind.Conductor)
                        Add("Rename…", OnArrangementHeaderRenameClick, "F2", editable);
                    if (header.Kind is ArrangementLaneKind.LogicalTrack
                        or ArrangementLaneKind.PureMidiTrack)
                    {
                        Add(
                            "Properties…",
                            OnArrangementTrackPropertiesClick,
                            "Ctrl+P",
                            editable);
                    }
                    if (header.Kind == ArrangementLaneKind.LogicalTrack)
                    {
                        Add("Edit Event Instrument…", OnArrangementHeaderEditEventInstrumentClick,
                            enabled: header.ParentId.HasValue);
                        Add("Change Event Instrument…", OnTrackHeaderBindClick,
                            enabled: editable && _session.Project?.EventInstruments.Count > 0);
                        Add("Share Instrument State With…", OnLogicalTrackShareStateClick,
                            enabled: editable && _session.Project?.Tracks.Count > 1);
                        Add("Make Independent", OnLogicalTrackMakeIndependentClick,
                            enabled: editable && header.IsSharedGroup);
                    }
                    if (header.Kind == ArrangementLaneKind.PureMidiTrack)
                    {
                        Add("MIDI Route Settings…", OnArrangementTrackRouteSettingsClick, enabled: editable);
                        Add("Share MIDI Channel With…", OnMidiTrackShareChannelClick,
                            enabled: editable && _session.Project?.PureMidiTracks.Count > 1);
                        Add("Make Independent", OnMidiTrackMakeIndependentClick,
                            enabled: editable && header.IsSharedGroup);
                    }
                    if (header.Kind != ArrangementLaneKind.Conductor) Separator();
                    if (header.Kind != ArrangementLaneKind.Conductor)
                    {
                        Add("Cut", OnArrangementHeaderCutClick, "Ctrl+X", editable);
                        Add("Copy", OnArrangementHeaderCopyClick, "Ctrl+C", header.ObjectId is not null);
                        Add("Paste", OnArrangementHeaderPasteClick, "Ctrl+V",
                            _session.CanEditProject && CanPasteArrangementHeader(header));
                        Add("Duplicate", OnArrangementHeaderDuplicateClick, "Ctrl+D", editable);
                        if (header.Kind == ArrangementLaneKind.LogicalTrack)
                        {
                            Add("Duplicate and Share State",
                                OnArrangementHeaderDuplicateAndShareStateClick,
                                enabled: editable && header.ParentId.HasValue);
                        }
                        Separator();
                    }
                    if (header.Kind is ArrangementLaneKind.LogicalTrack or ArrangementLaneKind.PureMidiTrack)
                    {
                        bool hasSegments = ArrangementHeaderSegmentCount(header) > 0;
                        Add("Select All Segments on Track", OnTrackHeaderSelectSegmentsClick, enabled: hasSegments);
                        Add("Add Track Segments to Selection", OnTrackHeaderAddSegmentsToSelectionClick, enabled: hasSegments);
                        Separator();
                    }
                    if (header.Kind != ArrangementLaneKind.Conductor)
                    {
                        (bool canMoveUp, bool canMoveDown) = ArrangementHeaderMoveAvailability(header);
                        Add("Move Up", OnArrangementHeaderMoveUpClick, enabled: editable && canMoveUp);
                        Add("Move Down", OnArrangementHeaderMoveDownClick, enabled: editable && canMoveDown);
                        Separator();
                        Add("Delete…", OnArrangementHeaderDeleteClick, enabled: editable);
                        Separator();
                    }
                    if (header.Kind == ArrangementLaneKind.Conductor)
                    {
                        Add("New Logical Track", OnNewTrackClick, enabled: _session.CanEditProject);
                        Add("New Logical Track with Instrument…", OnNewTrackWithInstrumentClick,
                            enabled: _session.CanEditProject);
                        Add("New MIDI Track…", OnNewRawMidiTrackClick, enabled: _session.CanEditProject);
                    }
                    return;
                case TimelineSurfaceMode.EventLanes
                    when _session.ActiveWorkspace is TimelineWorkspaceViewModel
                    {
                        Mode: TimelineWorkspaceMode.Segment
                    }:
                    bool isMidiEventLane = _session.ActiveWorkspace is TimelineWorkspaceViewModel
                    { ObjectId: MidoraId laneSegmentId }
                        && _session.Project is MidoraProject laneProject
                        && TimelineWorkspaceViewModel.FindMidiSegment(laneProject, laneSegmentId) is not null;
                    Add(
                        isMidiEventLane ? "Add MIDI Event Lane…" : "Add Logical Parameter Lane…",
                        OnAddParameterLaneClick,
                        enabled: _session.CanEditProject);
                    return;
                case TimelineSurfaceMode.EventLanes
                    when _session.ActiveWorkspace is InstrumentWorkspaceViewModel instrumentWorkspace:
                    {
                        Add("Add Event…", OnAddTemplateEventClick, enabled: _session.CanEditProject);
                        Add(
                            "Delete Event Lane…",
                            OnDeleteSubVoiceEventLaneClick,
                            enabled: _session.CanEditProject
                                && instrumentWorkspace.GetRenderLane(
                                    instrumentWorkspace.ActiveRenderLaneIndex)?.EventMappingTarget is not null);
                        return;
                    }
                default:
                    menu.IsOpen = false;
                    return;
            }
        }

        bool canEdit = _session.CanEditProject;
        if (menu.Tag is not TimelineSelectionAction && surface.ContextTargetHasObject == false
            && _session.ActiveWorkspace?.Selection.Ids.Count is not > 0)
        {
            // Only a genuinely empty selection uses the container-only menu.
            // A blank hit preserves the frozen selection and its full commands.
            ClearTimelineCommandTargets();
            _lastTimelineCommandSurface = surface;
            WorkspaceTimelineSelectionSource? backgroundSource = _session.ActiveWorkspace is { } backgroundWorkspace
                ? GetTimelineSelectionSource(backgroundWorkspace, surface) : null;
            if (IsTimelineGenerationSource(backgroundSource))
                Add(IsTimelineNoteGenerationSource(backgroundSource) ? "Batch Create Notes…" : "Batch Create Events…",
                    OnBatchCreateTimelineObjectsClick, enabled: canEdit);
            Add("Paste", OnPasteClick, "Ctrl+V", canEdit);
            Separator();
            Add("Deselect All", OnDeselectAllTimelineObjectsClick, enabled: _session.ActiveWorkspace?.Selection.Ids.Count > 0);
            Add("Invert Selection", OnInvertTimelineSelectionClick, enabled: surface.Snapshot?.HasHitTestableItems == true);
            return;
        }
        if (_session.ActiveWorkspace is WorkspaceViewModel mixedWorkspace
            && mixedWorkspace.Selection.Ids.Count > 0
            && (mixedWorkspace.Selection.Ids.Count > 1 && mixedWorkspace.Selection.HomogeneousTimelineSource is null
                || menu.Tag is not TimelineSelectionAction && surface.ContextTargetHasObject == false
                    && (mixedWorkspace.Selection.HomogeneousTimelineSource is not { } selectedSource
                        || !IsTimelineSelectionSourceCompatible(mixedWorkspace, surface, selectedSource)))
            && TryGetTimelineObjectOwner(mixedWorkspace, out _))
        {
            // A blank click can be in a different lane from the selection.
            // Resolve the selected source, never reinterpret notes as values
            // (or one event target as another) using the clicked surface.
            _ = PopulateObjectListMenuAsync(menu, mixedWorkspace);
            return;
        }
        WorkspaceTimelineSelectionSource? creationSource = _session.ActiveWorkspace is WorkspaceViewModel creationWorkspace
            ? GetTimelineSelectionSource(creationWorkspace, surface) : null;
        if (IsTimelineGenerationSource(creationSource))
        {
            Add(IsTimelineNoteGenerationSource(creationSource) ? "Batch Create Notes…" : "Batch Create Events…",
                OnBatchCreateTimelineObjectsClick, enabled: canEdit);
            Separator();
        }
        if (surface.SurfaceMode == TimelineSurfaceMode.Arrangement)
        {
            Add("Open", OnOpenWorkspaceSelectionClick);
            Separator();
        }
        Add("Cut", OnCutClick, "Ctrl+X", canEdit);
        Add("Copy", OnCopyClick, "Ctrl+C");
        Add("Paste", OnPasteClick, "Ctrl+V", canEdit);
        Add("Duplicate", OnDuplicateClick, "Ctrl+D", canEdit);
        Separator();
        Add("Delete", OnDeleteWorkspaceSelectionClick, "Delete", canEdit);
        Separator();
        bool hasSelection = _session.ActiveWorkspace?.Selection.Ids.Count > 0;
        bool hasInvertibleItems = surface.Snapshot?.HasHitTestableItems == true;
        Add("Deselect All", OnDeselectAllTimelineObjectsClick, enabled: hasSelection);
        Add("Invert Selection", OnInvertTimelineSelectionClick, enabled: hasInvertibleItems);
        Separator();
        _timelineSelectionOperationContext = ResolveTimelineSelectionOperationContext(surface);
        _timelineQuantizeOperationContext = ResolveTimelineQuantizeSelectionOperationContext(
            surface,
            _timelineSelectionOperationContext);
        TimelineSelectionOperationContext? operationContext = _timelineSelectionOperationContext;
        if (operationContext is not null)
        {
            bool hasOperationSelection = operationContext.Ids.Count != 0;
            if (operationContext.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments)
            {
                MenuItem flipHorizontal = new()
                {
                    Header = "Flip Horizontal",
                    IsEnabled = canEdit && hasOperationSelection
                };
                MenuItem exposedOnly = new() { Header = "Exposed Content Only" };
                exposedOnly.Click += OnFlipSegmentsExposedContentHorizontalClick;
                flipHorizontal.Items.Add(exposedOnly);
                MenuItem contentAndSegments = new() { Header = "Exposed Content and Segments" };
                contentAndSegments.Click += OnFlipSegmentsAndContentHorizontalClick;
                flipHorizontal.Items.Add(contentAndSegments);
                SetTimelineCommandIcon(flipHorizontal);
                menu.Items.Add(flipHorizontal);
            }
            else
            {
                Add(
                    "Flip Horizontal",
                    OnFlipSelectionHorizontalClick,
                    enabled: canEdit && hasOperationSelection);
            }
            if (operationContext.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments
                or TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes)
            {
                Add(
                    "Flip Vertical",
                    OnFlipSelectionVerticalClick,
                    enabled: canEdit && hasOperationSelection);
            }
            Add(
                "Scale…",
                OnScaleSelectionClick,
                "Ctrl+Q",
                enabled: canEdit && hasOperationSelection);
            if (operationContext.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments
                or TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes)
            {
                Add(
                    "Transpose…",
                    OnTransposeSelectionClick,
                    "Ctrl+T",
                    enabled: canEdit && hasOperationSelection);
            }
            Add(
                "Batch Edit…",
                OnBatchEditSelectionClick,
                "Ctrl+E",
                enabled: canEdit && hasOperationSelection);
            if (operationContext.Kind is TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes)
            {
                Separator();
                Add(
                    "Humanize…",
                    OnHumanizeSelectionClick,
                    enabled: canEdit && hasOperationSelection);
                Add(
                    "Split…",
                    OnSplitNotesClick,
                    enabled: canEdit && hasOperationSelection);
                Add(
                    "Join…",
                    OnJoinNotesClick,
                    enabled: canEdit && hasOperationSelection);
                Add(
                    "Quantize…",
                    OnQuantizeSelectionClick,
                    enabled: canEdit
                        && _timelineQuantizeOperationContext is { Ids.Count: > 0 });
            }
            Separator();
        }
        if (_timelineQuantizeOperationContext is { Ids.Count: > 0 } quantizeContext
            && quantizeContext.Kind is TimelineSelectionObjectKind.LogicalParameterPoints
                or TimelineSelectionObjectKind.DirectMidiEventPoints
                or TimelineSelectionObjectKind.SubVoiceEventPoints)
        {
            Add("Quantize…", OnQuantizeSelectionClick, enabled: canEdit);
            Separator();
        }
        else if (hasSelection
                 && operationContext is null
                 && surface.SurfaceMode is TimelineSurfaceMode.PianoRoll or TimelineSurfaceMode.Velocity)
        {
            Add("Humanize…", OnHumanizeSelectionClick, enabled: false);
            Add("Split…", OnSplitNotesClick, enabled: false);
            Add("Join…", OnJoinNotesClick, enabled: false);
            Add("Quantize…", OnQuantizeSelectionClick, enabled: false);
            Separator();
        }
        else if (hasSelection
                 && surface.SurfaceMode == TimelineSurfaceMode.EventLanes
                 && _timelineQuantizeOperationContext is null)
        {
            Add("Quantize…", OnQuantizeSelectionClick, enabled: false);
            Separator();
        }
        if (_session.ActiveWorkspace is WorkspaceViewModel)
        {
            Add(
                "Properties…",
                OnEditTimelinePropertiesClick,
                "Ctrl+P",
                enabled: CanOpenTimelineProperties(surface));
            Separator();
        }
        bool canUseTimeRange = _session.ActiveWorkspace is TimelineWorkspaceViewModel;
        Add("Set Time Range from Object Selection", OnSetTimeRangeFromObjectsClick, enabled: canUseTimeRange);
        Add("Select Objects in Time Range", OnSelectObjectsInTimeRangeClick, enabled: canUseTimeRange);
        Add("Clear Time Range", OnClearTimeRangeClick, enabled: canUseTimeRange);
    }

    private async void OnEditTimelinePropertiesClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        await OpenWorkspacePropertiesAsync(workspace);
    }

    private async Task OpenWorkspacePropertiesAsync(WorkspaceViewModel workspace)
    {
        try { await OpenWorkspacePropertiesCoreAsync(workspace); }
        catch (Exception exception) { ShowError("Properties", exception.Message); }
    }

    private async Task OpenWorkspacePropertiesCoreAsync(WorkspaceViewModel workspace)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (!PrepareForModalSurface()) return;
        ObjectPropertiesViewModel? properties = null;
        if (workspace is TimelineWorkspaceViewModel { IsConductor: true }
            && workspace.Selection.Ids.Count == 1 && workspace.Selection.Primary is MidoraId conductorId
            && _session.Project is MidoraProject conductorProject)
        {
            ConductorTrack frozen = conductorProject.Conductor.CloneFrozen();
            long revision = _session.Document!.PublicationRevision;
            long selectionRevision = workspace.Selection.Revision;
            bool completed = await RunOperationAsync("Read Properties", async token =>
            {
                properties = await Task.Run(() => ObjectPropertiesProjection.ReadConductorSelection(frozen, conductorId, token), token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_session.Project, conductorProject)
                    || _session.Document!.PublicationRevision != revision
                    || workspace.Selection.Revision != selectionRevision || !_session.Workspaces.Contains(workspace))
                    throw new InvalidOperationException("The Properties source changed while it was being read.");
            }, canCancel: true);
            if (!completed) { RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface); return; }
        }
        else if (workspace.Selection.Ids.Count > 1 && _session.Project is MidoraProject project)
        {
            ObjectPropertiesSelectionContext selection = ObjectPropertiesSelectionContext.Capture(workspace);
            long revision = _session.Document!.PublicationRevision;
            bool completed = await RunOperationAsync("Read Properties", async token =>
            {
                using DispatcherCoalescingProgress<TimelineEditPreparationProgress> progress = new(
                    Dispatcher, TimeSpan.FromMilliseconds(100),
                    value => _session.ActiveForegroundTask?.Report(
                        FormatTimelineEditPreparationProgress(value), value.IsIndeterminate ? null : value.OverallFraction));
                properties = await Task.Run(() => ObjectPropertiesProjection.ReadMultiSelection(
                    project, selection, token, progress), token);
                progress.Flush();
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_session.Project, project)
                    || _session.Document!.PublicationRevision != revision)
                    throw new InvalidOperationException("The Properties source changed while it was being read.");
            }, canCancel: true);
            if (!completed)
            {
                RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);
                return;
            }
        }
        else properties = _session.CreateObjectProperties(workspace);
        if (properties is null) return;
        if (!ObjectPropertiesProjection.CanEditInPropertiesDialog(workspace, properties))
        {
            ShowUnavailable("Properties", "The current object has no available properties.");
            return;
        }
        ObjectPropertiesDialog dialog = new(_session, workspace, properties) { Owner = this };
        _ = ShowModalDialog(dialog);
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnTreeRenameClick(object sender, RoutedEventArgs e) => BeginTreeRename();

    private void BeginTreeRename()
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node
            || node.Kind is not (ProjectTreeNodeKind.LogicalTrack
                or ProjectTreeNodeKind.EventInstrument))
        {
            return;
        }
        string kind = node.Kind == ProjectTreeNodeKind.LogicalTrack
            ? "Logical Track"
            : "Event Instrument";
        TextInputDialog dialog = new(
            $"Rename {kind}",
            $"Enter the {kind} name.",
            node.Title)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) == true)
        {
            RunSynchronous($"Rename {kind}", () =>
                _session.RenameProjectTreeNode(node, dialog.Value));
        }
    }

    private void OnTreeRenameLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Visibility: Visibility.Visible } textBox)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void OnTreeRenameLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ProjectTreeNode { IsRenaming: true } } textBox)
        {
            CommitTreeRename(textBox);
        }
    }

    private void OnTreeRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProjectTreeNode node } textBox) return;
        if (e.Key == Key.Enter)
        {
            CommitTreeRename(textBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            node.EditText = node.Title;
            node.IsRenaming = false;
            ProjectTree.Focus();
            e.Handled = true;
        }
    }

    private void CommitTreeRename(TextBox textBox)
    {
        if (textBox.DataContext is not ProjectTreeNode { IsRenaming: true } node) return;
        try
        {
            node.IsRenaming = false;
            _session.RenameProjectTreeNode(node, node.EditText);
            _session.SetStatusMessage(null);
        }
        catch (Exception exception)
        {
            node.IsRenaming = true;
            textBox.BorderBrush = (Brush)FindResource("Brush.Red");
            textBox.ToolTip = exception.Message;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void OnTreeMoveUpClick(object sender, RoutedEventArgs e) => MoveSelectedTreeNode(-1);
    private void OnTreeMoveDownClick(object sender, RoutedEventArgs e) => MoveSelectedTreeNode(1);

    private void MoveSelectedTreeNode(int direction)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node) return;
        RunSynchronous("Reorder Project Object", () => _session.MoveProjectTreeNode(node, direction));
    }

    private void OnTreeDeleteClick(object sender, RoutedEventArgs e) => DeleteSelectedTreeNode();

    private void OnTreeBindInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode
            {
                Kind: ProjectTreeNodeKind.LogicalTrack,
                ObjectId: MidoraId trackId
            }
            || _session.Project is null)
        {
            return;
        }
        object unbound = new();
        List<SelectionDialogItem> options =
        [
            new(unbound, "Unbound", "Compilation reports the track as unbound until an Event Instrument is selected.")
        ];
        options.AddRange(_session.Project.EventInstruments.Select(instrument => new SelectionDialogItem(
            instrument.Id,
            string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name,
            $"{instrument.SubVoices.Count} SubVoice(s)")));
        SelectionDialog dialog = new(
            "Bind Logical Track",
            "Select the Event Instrument used by this Logical Track.",
            options)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true) return;
        MidoraId? instrumentId = ReferenceEquals(dialog.SelectedValue, unbound)
            ? null
            : (MidoraId?)dialog.SelectedValue;
        LogicalTrack track = _session.Project.Tracks.Single(item => item.Id == trackId);
        if (instrumentId is MidoraId selectedInstrumentId)
        {
            BindTrackToInstrument(track, selectedInstrumentId);
        }
        else
        {
            RunSynchronous(
                "Unbind Logical Track",
                () => _session.BindLogicalTrack(trackId, null));
        }
    }

    private void DeleteSelectedTreeNode()
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode node || _session.Project is null) return;
        string? detail = node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack when node.ObjectId is MidoraId id =>
                $"Delete Logical Track '{node.Title}' and its {_session.Project.Tracks.Single(item => item.Id == id).Segments.Count} Segment(s)?",
            ProjectTreeNodeKind.EventInstrument when node.ObjectId is MidoraId id =>
                $"Delete Event Instrument '{node.Title}'? {_session.Project.EventInstrumentUsages.Count(item => item.EventInstrumentId == id)} usage(s) still reference it.",
            ProjectTreeNodeKind.DamagedEventInstrument =>
                $"Permanently remove damaged Event Instrument placeholder '{node.Title}' from the Project? Bound Logical Tracks will become unbound and retain the last known instrument name. This operation is undoable until the Project closes.",
            ProjectTreeNodeKind.DamagedLogicalTrack =>
                $"Permanently remove damaged Logical Track placeholder '{node.Title}' from the Project? This operation is undoable until the Project closes.",
            _ => null
        };
        if (detail is null) return;
        if (MessageDialog.Show(this, detail, "Delete Project Object", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        if (node.ObjectId is not MidoraId objectId) return;
        StartWorkspaceEdit(node.Kind switch
        {
            ProjectTreeNodeKind.LogicalTrack => ProjectDomainEditCommands.DeleteLogicalTrack(objectId, true),
            ProjectTreeNodeKind.EventInstrument => ProjectDomainEditCommands.DeleteEventInstrument(objectId, true),
            ProjectTreeNodeKind.DamagedEventInstrument => ProjectDomainEditCommands.DeleteDamagedEventInstrument(objectId),
            ProjectTreeNodeKind.DamagedLogicalTrack => ProjectDomainEditCommands.DeleteDamagedLogicalTrack(objectId),
            _ => throw new InvalidOperationException("This Project node cannot be deleted.")
        });
    }

    private void OnInstrumentListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is InstrumentListItem instrument)
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(
                () => RunSynchronous("Open Event Instrument", () => _session.OpenInstrument(instrument.Id)),
                DispatcherPriority.Normal);
        }
    }

    private void OnInstrumentListMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _instrumentListDragStart = e.GetPosition((IInputElement)sender);
        _instrumentListDragId = FindListBoxItem(e.OriginalSource as DependencyObject)?.DataContext switch
        {
            InstrumentListItem item => item.Id,
            EventInstrumentBrowserRow row => row.Id,
            _ => null
        };
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? current)
    {
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        return current as ListBoxItem;
    }

    private void OnInstrumentListMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list
            || e.LeftButton != MouseButtonState.Pressed
            || _instrumentListDragStart is not Point start
            || _instrumentListDragId is not MidoraId instrumentId)
        {
            return;
        }
        Point current = e.GetPosition(list);
        if (!HasReachedUiReorderDragThreshold(start, current))
        {
            return;
        }
        _instrumentListDragStart = null;
        _instrumentListDragId = null;
        DataObject data = new(EventInstrumentDragFormat, instrumentId.Value);
        DragDrop.DoDragDrop(list, data, DragDropEffects.Link | DragDropEffects.Move);
    }

    private void OnEventInstrumentBrowserDragOver(object sender, DragEventArgs e)
    {
        ListBoxItem? targetContainer = null;
        bool insertAfter = false;
        bool accepted = false;
        if (sender is ListBox list)
        {
            accepted = TryResolveEventInstrumentBrowserDrop(
                list,
                e,
                out _,
                out _,
                out targetContainer,
                out insertAfter);
        }
        if (accepted)
        {
            SetEventInstrumentBrowserDropPreview(targetContainer, insertAfter);
        }
        else
        {
            ClearEventInstrumentBrowserDropPreview();
        }
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnEventInstrumentBrowserDragLeave(object sender, DragEventArgs e)
    {
        ClearEventInstrumentBrowserDropPreview();
        e.Handled = true;
    }

    private void OnEventInstrumentBrowserDrop(object sender, DragEventArgs e)
    {
        MidoraId instrumentId = default;
        int targetIndex = -1;
        bool accepted = false;
        if (sender is ListBox list)
        {
            accepted = TryResolveEventInstrumentBrowserDrop(
                list,
                e,
                out instrumentId,
                out targetIndex,
                out _,
                out _);
        }
        ClearEventInstrumentBrowserDropPreview();
        e.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        if (!accepted) return;
        RunSynchronous("Move Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.ReorderEventInstrument(instrumentId, targetIndex)));
    }

    private bool TryResolveEventInstrumentBrowserDrop(
        ListBox list,
        DragEventArgs e,
        out MidoraId instrumentId,
        out int targetIndex,
        out ListBoxItem? targetContainer,
        out bool insertAfter)
    {
        instrumentId = default;
        targetIndex = -1;
        targetContainer = null;
        insertAfter = false;
        if (!_session.CanEditProject
            || _session.Project is not MidoraProject project
            || FindVisualAncestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null
            || !e.Data.GetDataPresent(EventInstrumentDragFormat)
            || e.Data.GetData(EventInstrumentDragFormat) is not long rawId
            || rawId <= 0)
        {
            return false;
        }
        MidoraId candidate = new(rawId);
        int sourceIndex = project.EventInstruments.FindIndex(value => value.Id == candidate);
        if (sourceIndex < 0) return false;

        int boundaryIndex;
        targetContainer = FindListBoxItem(e.OriginalSource as DependencyObject);
        if (targetContainer?.DataContext is EventInstrumentBrowserRow targetRow)
        {
            int itemIndex = project.EventInstruments.FindIndex(value => value.Id == targetRow.Id);
            if (itemIndex < 0) return false;
            insertAfter = e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;
            boundaryIndex = itemIndex + (insertAfter ? 1 : 0);
        }
        else
        {
            Point point = e.GetPosition(list);
            if (point.Y < 0 || point.Y >= list.ActualHeight) return false;
            boundaryIndex = project.EventInstruments.Count;
            if (project.EventInstruments.Count != 0
                && list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
            {
                double firstTop = first.TranslatePoint(new Point(0, 0), list).Y;
                if (point.Y < firstTop)
                {
                    boundaryIndex = 0;
                    targetContainer = first;
                    insertAfter = false;
                }
                else if (list.ItemContainerGenerator.ContainerFromIndex(
                             project.EventInstruments.Count - 1) is ListBoxItem last)
                {
                    targetContainer = last;
                    insertAfter = true;
                }
            }
        }

        if (boundaryIndex > sourceIndex) boundaryIndex--;
        instrumentId = candidate;
        targetIndex = Math.Clamp(boundaryIndex, 0, project.EventInstruments.Count - 1);
        return true;
    }

    private void SetEventInstrumentBrowserDropPreview(
        ListBoxItem? targetContainer,
        bool insertAfter)
    {
        if (ReferenceEquals(_instrumentBrowserDropContainer, targetContainer)
            && _instrumentBrowserDropAfter == insertAfter)
        {
            return;
        }
        ClearEventInstrumentBrowserDropPreview();
        _instrumentBrowserDropContainer = targetContainer;
        _instrumentBrowserDropAfter = insertAfter;
        if (targetContainer is null) return;
        targetContainer.SetResourceReference(Control.BorderBrushProperty, "Brush.Info");
        targetContainer.BorderThickness = insertAfter
            ? new Thickness(0, 0, 0, 2)
            : new Thickness(0, 2, 0, 0);
    }

    private void ClearEventInstrumentBrowserDropPreview()
    {
        if (_instrumentBrowserDropContainer is not null)
        {
            _instrumentBrowserDropContainer.ClearValue(Control.BorderBrushProperty);
            _instrumentBrowserDropContainer.ClearValue(Control.BorderThicknessProperty);
        }
        _instrumentBrowserDropContainer = null;
        _instrumentBrowserDropAfter = false;
    }

    private void OnTimelineInstrumentDragOver(object sender, DragEventArgs e)
    {
        bool accepted = TryResolveInstrumentDrop(
            sender,
            e,
            out LogicalTrack? track,
            out _,
            out int? insertionIndex);
        if (sender is TimelineSurface surface)
        {
            surface.SetExternalArrangementInsertionPreview(
                accepted && track is null ? insertionIndex : null);
        }
        e.Effects = accepted ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTimelineInstrumentDragLeave(object sender, DragEventArgs e)
    {
        if (sender is TimelineSurface surface)
        {
            surface.SetExternalArrangementInsertionPreview(null);
        }
        e.Handled = true;
    }

    private void OnTimelineInstrumentDrop(object sender, DragEventArgs e)
    {
        if (sender is TimelineSurface surface)
        {
            surface.SetExternalArrangementInsertionPreview(null);
        }
        if (!TryResolveInstrumentDrop(
                sender,
                e,
                out LogicalTrack? track,
                out MidoraId instrumentId,
                out int? insertionIndex))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Link;
        e.Handled = true;
        MidoraId? trackId = track?.Id;
        int? trackInsertionIndex = insertionIndex;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () =>
            {
                if (trackId is MidoraId existingTrackId)
                {
                    LogicalTrack? current = _session.Project?.Tracks
                        .FirstOrDefault(value => value.Id == existingTrackId);
                    if (current is not null) BindTrackToInstrument(current, instrumentId);
                }
                else
                {
                    RunSynchronous("Create Logical Track", () => _session.Execute(
                        ProjectDomainEditCommands.CreateLogicalTrack(
                            eventInstrumentId: instrumentId,
                            insertionIndex: trackInsertionIndex)));
                }
            });
    }

    private bool TryResolveInstrumentDrop(
        object sender,
        DragEventArgs e,
        out LogicalTrack? track,
        out MidoraId instrumentId,
        out int? insertionIndex)
    {
        track = null;
        instrumentId = default;
        insertionIndex = null;
        if (sender is not TimelineSurface surface
            || surface.SurfaceMode != TimelineSurfaceMode.Arrangement
            || !_session.CanEditProject
            || _session.Project is not MidoraProject project
            || !e.Data.GetDataPresent(EventInstrumentDragFormat)
            || e.Data.GetData(EventInstrumentDragFormat) is not long rawId
            || rawId <= 0
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel arrangement)
        {
            return false;
        }
        MidoraId candidateInstrumentId = new(rawId);
        if (!project.EventInstruments.Any(item => item.Id == candidateInstrumentId)) return false;
        instrumentId = candidateInstrumentId;
        Point point = e.GetPosition(surface);
        if (surface.TryGetArrangementLaneHeader(point, out int lane)
            && arrangement.GetArrangementLane(lane) is
            { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId logicalTrackId })
        {
            track = project.Tracks.FirstOrDefault(value => value.Id == logicalTrackId);
            return track is not null;
        }
        if (!surface.TryGetArrangementTrackInsertionIndex(point, out int candidateInsertionIndex))
        {
            return false;
        }
        insertionIndex = Math.Clamp(candidateInsertionIndex, 0, project.ArrangementTracks.Count);
        return true;
    }

    private void BindTrackToInstrument(LogicalTrack track, MidoraId instrumentId)
    {
        if (_session.Project is not MidoraProject project) return;
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId);
        MidoraId? currentInstrumentId = project.ResolveEventInstrumentDefinitionId(track);
        if (instrument is null || currentInstrumentId == instrumentId) return;
        if (currentInstrumentId is not null
            && MessageDialog.Show(
                this,
                $"Rebind Logical Track '{TimelineWorkspaceViewModel.TrackDisplayName(project, track)}' to Event Instrument '{instrument.Name}'?",
                "Rebind Logical Track",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Bind Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, instrumentId)));
    }

    private void OnListBoxRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
            if (ItemsControl.ItemsControlFromItemContainer(item) is ListBox list
                && list.DataContext is InstrumentWorkspaceViewModel workspace
                && TryGetInstrumentStructureItemId(item.DataContext, out _))
            {
                SelectInstrumentStructureItem(list, workspace, item.DataContext);
            }
        }
    }

    private void OnOpenLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            _session.OpenInstrument(selected.Id);
        }
    }

    private void OnDeleteLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            return;
        }
        int bindings = project.EventInstrumentUsages.Count(
            usage => usage.EventInstrumentId == selected.Id);
        if (MessageDialog.Show(
                this,
                $"Delete Event Instrument '{selected.Name}'? {bindings} usage(s) still reference it.",
                "Delete Event Instrument",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        StartWorkspaceEdit(ProjectDomainEditCommands.DeleteEventInstrument(
            selected.Id, referencedDeletionConfirmed: true));
    }

    private void OnDuplicateLibraryInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            return;
        }
        StartWorkspaceEdit(ProjectDomainEditCommands.DuplicateEventInstrument(selected.Id));
    }

    private void OnCopyLibraryInstrumentClick(object sender, RoutedEventArgs e) =>
        CopySelectedEventInstrument();

    private void OnPasteLibraryInstrumentClick(object sender, RoutedEventArgs e) =>
        PasteEventInstrumentClipboard();

    private bool CopySelectedEventInstrument()
    {
        if (!TryGetSelectedEventInstrumentId(out MidoraId instrumentId))
        {
            return false;
        }
        return CopyEventInstrument(instrumentId);
    }

    private bool CopyEventInstrument(MidoraId instrumentId)
    {
        if (_session.Document is not ProjectDocumentSession document) return false;
        StartClipboardTransfer(document, () => ProjectObjectClipboard.CopyEventInstrument(document, instrumentId));
        return true;
    }

    private bool PasteEventInstrumentClipboard(bool allowSelectedTreeTarget = false)
    {
        if (!_session.CanEditProject
            || _session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _projectClipboard is not ProjectObjectClipboardPayload
            { Kind: ProjectObjectClipboardKind.EventInstrument } payload
            || !ReferenceEquals(document, _clipboardDocument)
            || !IsEventInstrumentClipboardTarget(allowSelectedTreeTarget))
        {
            return false;
        }
        long firstNewStableId = project.NextStableId;
        StartWorkspaceEdit(ProjectObjectClipboard.CreatePasteEventInstrumentCommand(document, payload), () =>
        {
            if (_session.ActiveWorkspace is LibraryWorkspaceViewModel library)
            {
                library.SelectedInstrument = library.Instruments
                    .FirstOrDefault(value => value.Id.Value >= firstNewStableId);
            }
            _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}.");
        });
        return true;
    }

    private bool TryGetSelectedEventInstrumentId(out MidoraId instrumentId)
    {
        if (_session.ActiveWorkspace is LibraryWorkspaceViewModel
            {
                SelectedInstrument: InstrumentListItem selected
            })
        {
            instrumentId = selected.Id;
            return true;
        }
        if (ProjectTree.IsKeyboardFocusWithin
            && ProjectTree.SelectedItem is ProjectTreeNode
            { Kind: ProjectTreeNodeKind.EventInstrument, ObjectId: MidoraId selectedId })
        {
            instrumentId = selectedId;
            return true;
        }
        instrumentId = default;
        return false;
    }

    private bool IsEventInstrumentClipboardTarget(bool allowSelectedTreeTarget) =>
        _session.ActiveWorkspace is LibraryWorkspaceViewModel
        || (ProjectTree.IsKeyboardFocusWithin || allowSelectedTreeTarget)
            && ProjectTree.SelectedItem is ProjectTreeNode
            {
                Kind: ProjectTreeNodeKind.InstrumentLibrary
                    or ProjectTreeNodeKind.EventInstrument
            };

    private void OnCopyTreeInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is ProjectTreeNode
            { Kind: ProjectTreeNodeKind.EventInstrument, ObjectId: MidoraId instrumentId })
        {
            _ = CopyEventInstrument(instrumentId);
        }
    }

    private void OnPasteTreeInstrumentClick(object sender, RoutedEventArgs e) =>
        _ = PasteEventInstrumentClipboard(allowSelectedTreeTarget: true);

    private void OnDuplicateTreeInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode
            { Kind: ProjectTreeNodeKind.EventInstrument, ObjectId: MidoraId instrumentId })
        {
            return;
        }
        StartWorkspaceEdit(
            ProjectDomainEditCommands.DuplicateEventInstrument(instrumentId));
    }

    private void OnCutTreeLogicalTrackClick(object sender, RoutedEventArgs e) =>
        CutOrCopySelectedLogicalTrack(cut: true, requireTreeSelection: true);

    private void OnCopyTreeLogicalTrackClick(object sender, RoutedEventArgs e) =>
        CutOrCopySelectedLogicalTrack(cut: false, requireTreeSelection: true);

    private void OnPasteTreeLogicalTrackClick(object sender, RoutedEventArgs e) =>
        PasteLogicalTrackClipboard(ResolveLogicalTrackPasteIndex(preferTreeSelection: true));

    private void OnDuplicateTreeLogicalTrackClick(object sender, RoutedEventArgs e)
    {
        if (ProjectTree.SelectedItem is not ProjectTreeNode
            { Kind: ProjectTreeNodeKind.LogicalTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        StartWorkspaceEdit(
            ProjectDomainEditCommands.DuplicateLogicalTrack(trackId));
    }

    private bool CutOrCopySelectedLogicalTrack(bool cut, bool requireTreeSelection = false)
    {
        if (!TryGetSelectedLogicalTrack(out LogicalTrack? track, out _, requireTreeSelection)
            || _session.Document is not ProjectDocumentSession document
            || cut && !_session.CanEditProject)
        {
            return false;
        }
        MidoraId trackId = track.Id;
        StartClipboardTransfer(document, () => ProjectObjectClipboard.CopyLogicalTrack(document, trackId),
            cut ? ProjectDomainEditCommands.DeleteLogicalTrack(trackId, nonEmptyDeletionConfirmed: true) : null);
        return true;
    }

    private bool CanPasteLogicalTrack() =>
        _session.CanEditProject
        && _session.Document is ProjectDocumentSession document
        && _projectClipboard is { Kind: ProjectObjectClipboardKind.LogicalTrack }
        && ReferenceEquals(document, _clipboardDocument);

    private bool PasteLogicalTrackClipboard(int insertionIndex)
    {
        if (!CanPasteLogicalTrack()
            || _session.Document is not ProjectDocumentSession document
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || _session.Project is not MidoraProject project)
        {
            return false;
        }
        MidoraId? targetInstrumentId = ResolveLogicalTrackPasteTarget(project);
        if (targetInstrumentId is null) return false;
        int targetIndex = Math.Clamp(insertionIndex, 0, project.ArrangementTracks.Count);
        StartWorkspaceEdit(ProjectObjectClipboard.CreatePasteLogicalTrackCommand(
            document, payload, targetInstrumentId.Value, targetIndex),
            () => _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}."));
        return true;
    }

    private MidoraId? ResolveLogicalTrackPasteTarget(MidoraProject project)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } arrangement)
        {
            int? lane = _trackHeaderContextLane ?? arrangement.ActiveLane;
            if (lane is int laneIndex
                && arrangement.GetArrangementLane(laneIndex) is ArrangementLaneDescriptor descriptor)
            {
                if (descriptor.Kind == ArrangementLaneKind.LogicalTrack
                    && descriptor.ParentId is MidoraId id
                    && project.EventInstruments.Any(value => value.Id == id))
                {
                    return id;
                }
            }
        }
        if (project.EventInstruments.Count == 1)
            return project.EventInstruments[0].Id;
        if (project.EventInstruments.Count == 0)
        {
            ShowUnavailable("Paste Logical Track", "Create an Event Instrument first.");
            return null;
        }
        SelectionDialog dialog = new(
            "Paste Logical Track",
            "Select the Event Instrument Definition for the pasted independent usage.",
            project.EventInstruments.Select(value => new SelectionDialogItem(
                value.Id,
                string.IsNullOrWhiteSpace(value.Name) ? "Unnamed Event Instrument" : value.Name,
                $"{value.SubVoices.Count} SubVoice(s)")))
        {
            Owner = this
        };
        return ShowModalDialog(dialog) == true && dialog.SelectedValue is MidoraId selected
            ? selected
            : null;
    }

    private int ResolveLogicalTrackPasteIndex(
        bool preferTreeSelection = false,
        bool preferTrackHeaderContext = false)
    {
        if (_session.Project is not MidoraProject project)
        {
            return 0;
        }
        if ((preferTreeSelection || ProjectTree.IsKeyboardFocusWithin)
            && ProjectTree.SelectedItem is ProjectTreeNode treeNode)
        {
            if (treeNode is { Kind: ProjectTreeNodeKind.LogicalTrack, ObjectId: MidoraId trackId })
            {
                int index = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
                if (index >= 0) return index + 1;
            }
            if (treeNode.Kind == ProjectTreeNodeKind.LogicalTracks)
            {
                return project.ArrangementTracks.Count;
            }
        }
        if (preferTrackHeaderContext
            && _trackHeaderContextLane is int contextLane
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel arrangement
            && arrangement.GetArrangementLane(contextLane) is { ObjectId: MidoraId contextTrackId })
        {
            int index = project.ArrangementTracks.FindIndex(value => value.TrackId == contextTrackId);
            if (index >= 0) return index + 1;
        }
        if (_logicalTrackShortcutTrackId is MidoraId shortcutTrackId
            && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement })
        {
            int shortcutIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == shortcutTrackId);
            if (shortcutIndex >= 0) return shortcutIndex + 1;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement, ActiveLane: int activeLane } timeline)
        {
            ArrangementLaneDescriptor? lane = timeline.GetArrangementLane(activeLane);
            int index = lane?.ObjectId is MidoraId activeTrackId
                ? project.ArrangementTracks.FindIndex(value => value.TrackId == activeTrackId)
                : -1;
            return index < 0 ? project.ArrangementTracks.Count : index + 1;
        }
        return project.ArrangementTracks.Count;
    }

    private bool TryGetSelectedLogicalTrack(
        out LogicalTrack track,
        out int index,
        bool requireTreeSelection = false)
    {
        track = null!;
        index = -1;
        if (_session.Project is not MidoraProject project)
        {
            return false;
        }
        if (ProjectTree.SelectedItem is ProjectTreeNode
            { Kind: ProjectTreeNodeKind.LogicalTrack, ObjectId: MidoraId trackId }
            && (ProjectTree.IsKeyboardFocusWithin || requireTreeSelection))
        {
            index = project.Tracks.FindIndex(value => value.Id == trackId);
        }
        else if (!requireTreeSelection
            && _logicalTrackShortcutTrackId is MidoraId shortcutTrackId
            && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement })
        {
            index = project.Tracks.FindIndex(value => value.Id == shortcutTrackId);
        }
        if ((uint)index >= (uint)project.Tracks.Count)
        {
            return false;
        }
        track = project.Tracks[index];
        return true;
    }

    private void OnAddSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } workspace }) return;
        RunSynchronous("Create SubVoice", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateSubVoice(instrumentId), workspace));
    }

    private void OnDuplicateSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId subVoiceId
            }
            || _session.Project?.EventInstruments
                .FirstOrDefault(item => item.Id == instrumentId)?.SubVoices
                .Any(item => item.Id == subVoiceId) != true)
        {
            _session.SetStatusMessage("Select one SubVoice to duplicate.", isError: true);
            return;
        }
        InstrumentWorkspaceViewModel workspace = (InstrumentWorkspaceViewModel)_session.ActiveWorkspace;
        long firstId = _session.Project!.NextStableId;
        StartWorkspaceEdit(ProjectDomainEditCommands.DuplicateSubVoice(instrumentId, subVoiceId),
            () => SelectCreatedWorkspaceObjects(workspace, firstId));
    }

    private void OnInstrumentStructureCutClick(object sender, RoutedEventArgs e) =>
        CutOrCopyProjectSelection(cut: true);

    private void OnInstrumentStructureCopyClick(object sender, RoutedEventArgs e) =>
        CutOrCopyProjectSelection(cut: false);

    private void OnInstrumentStructurePasteClick(object sender, RoutedEventArgs e) =>
        PasteProjectSelection();

    private void OnInstrumentStructureDeleteClick(object sender, RoutedEventArgs e) =>
        DeleteWorkspaceSelection();

    private void OnMoveSubVoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string directionText }
            || !int.TryParse(directionText, out int direction)
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId subVoiceId
            }
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            _session.SetStatusMessage("Select one SubVoice to reorder.", isError: true);
            return;
        }
        int oldIndex = instrument.SubVoices.FindIndex(item => item.Id == subVoiceId);
        int newIndex = Math.Clamp(oldIndex + direction, 0, instrument.SubVoices.Count - 1);
        if (oldIndex < 0 || newIndex == oldIndex) return;
        RunSynchronous("Reorder SubVoice", () => _session.Execute(
            ProjectDomainEditCommands.ReorderSubVoice(instrumentId, subVoiceId, newIndex)));
    }

    private void OnInstrumentIsolationClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: bool enabled } checkBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        if (instrument.RequiresChannelIsolation == enabled) return;
        if (!_session.CanEditProject)
        {
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }
        if (!RunSynchronous("Change Event Instrument Isolation", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentIsolation(instrumentId, enabled))))
        {
            _session.RefreshWorkspace(workspace);
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }

    private void OnInstrumentConfigurationLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (field == "PreRollTicks" && string.IsNullOrWhiteSpace(workspace.InstrumentPreRollTicksText))
        {
            // Normalize the draft too: an already-zero value is a command no-op
            // and therefore does not cause a workspace refresh.
            workspace.InstrumentPreRollTicksText = "0";
        }
        if (!RunSynchronous("Update Event Instrument configuration", () =>
        {
            IProjectEditCommand command = field switch
            {
                "Name" => ProjectDomainEditCommands.RenameEventInstrument(
                    instrumentId,
                    workspace.InstrumentNameText),
                "Description" => ProjectDomainEditCommands.UpdateEventInstrumentDescription(
                    instrumentId,
                    string.IsNullOrWhiteSpace(workspace.InstrumentDescriptionText)
                        ? null
                        : workspace.InstrumentDescriptionText),
                "RootNote" => ProjectDomainEditCommands.UpdateEventInstrumentRootNote(
                    instrumentId,
                    int.Parse(workspace.InstrumentRootNoteText, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                "TemplateLength" => ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(
                    instrumentId,
                    long.Parse(workspace.InstrumentTemplateLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                "PreRollTicks" => ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(
                    instrumentId,
                    long.Parse(workspace.InstrumentPreRollTicksText, NumberStyles.Integer, CultureInfo.InvariantCulture)),
                _ => throw new InvalidOperationException("Unknown Event Instrument configuration field.")
            };
            _session.Execute(command);
        }))
        {
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnSelectInstrumentColorClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditProject
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId }
            || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        ColorPickerDialog dialog = new(
            instrument.Color.Red,
            instrument.Color.Green,
            instrument.Color.Blue)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous("Change Event Instrument Color", () => _session.Execute(
            ProjectDomainEditCommands.UpdateEventInstrumentColor(
                instrumentId,
                new MidoraColor(dialog.Red, dialog.Green, dialog.Blue))));
    }

    private void OnInstrumentInitialStateFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: PropertyField field } textBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        RunSynchronous("Update Event Instrument Initial State", () =>
        {
            MidiValueTarget target = ParseConfigurationMidiTarget(field.Key);
            int? value = ParseAndClampInitialStateValue(
                textBox.Text,
                target,
                "Event Instrument Initial State");
            _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
                instrumentId,
                target,
                value));
        });
        _session.RefreshWorkspace(workspace);
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private void OnSubVoiceConfigurationLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string field }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument
            || instrument.SubVoices.FirstOrDefault(item => item.Id == subVoiceId)
                is not SubVoice subVoice)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        bool succeeded = RunSynchronous("Update SubVoice configuration", () =>
        {
            IProjectEditCommand? command = field switch
            {
                "Name" when !string.Equals(subVoice.Name ?? string.Empty,
                    workspace.ActiveSubVoiceNameText, StringComparison.Ordinal) =>
                    ProjectDomainEditCommands.UpdateSubVoiceName(
                        instrumentId,
                        subVoiceId,
                        workspace.ActiveSubVoiceNameText),
                "RootNote" => CreateRootNoteCommand(),
                _ => null
            };
            if (command is not null) _session.Execute(command);
        });
        if (!succeeded) _session.RefreshWorkspace(workspace);

        IProjectEditCommand? CreateRootNoteCommand()
        {
            string text = workspace.ActiveSubVoiceRootNoteText.Trim();
            int? root = text.Length == 0
                ? null
                : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return root == subVoice.RootNoteOverride
                ? null
                : ProjectDomainEditCommands.UpdateSubVoiceRootNote(
                    instrumentId,
                    subVoiceId,
                    root);
        }
    }

    private void OnSubVoiceInitialStateFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: PropertyField property } textBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } workspace)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        bool succeeded = RunSynchronous("Update SubVoice Initial State", () =>
        {
            MidiValueTarget target = ParseConfigurationMidiTarget(property.Key);
            int? value = ParseAndClampInitialStateValue(
                textBox.Text,
                target,
                "SubVoice Initial State");
            _session.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
                instrumentId,
                subVoiceId,
                target,
                value));
        });
        _session.RefreshWorkspace(workspace);
        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private int? ParseAndClampInitialStateValue(
        string source,
        MidiValueTarget target,
        string displayName)
    {
        string text = source.Trim();
        if (text.Length == 0) return null;
        int entered = int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        int raw = MidiEditingValueDomain.ClampInitialState(target, entered);
        int clamped = raw + MidiEditingValueDomain.Offset(target);
        if (clamped != entered)
        {
            _session.SetStatusMessage(
                $"{displayName}: {entered.ToString(CultureInfo.InvariantCulture)} was clamped to {clamped.ToString(CultureInfo.InvariantCulture)}.");
        }
        return raw;
    }

    private static MidiValueTarget ParseConfigurationMidiTarget(string key)
    {
        if (key == "bankMsb") return MidiValueTarget.BankMsb;
        if (key == "bankLsb") return MidiValueTarget.BankLsb;
        if (key == "program") return MidiValueTarget.Program;
        if (key == "pitchBend") return MidiValueTarget.PitchBend;
        if (key == "pitchRangeSemitones") return MidiValueTarget.PitchBendRangeSemitones;
        if (key == "pitchRangeCents") return MidiValueTarget.PitchBendRangeCents;
        if (TryNumber("cc.", MidiValueKind.ControlChange, out MidiValueTarget target)
            || TryNumber("rpn.", MidiValueKind.RegisteredParameter, out target)
            || TryNumber("nrpn.", MidiValueKind.NonRegisteredParameter, out target))
        {
            return target;
        }
        throw new InvalidOperationException("Unknown Event Instrument Initial State target.");

        bool TryNumber(string prefix, MidiValueKind kind, out MidiValueTarget result)
        {
            result = default;
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                return false;
            }
            result = new(kind, number);
            return true;
        }
    }

    private void OnInstrumentLifecycleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string field } comboBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        ShortNoteLifecycle shortLifecycle = field == "Short" && comboBox.SelectedItem is ShortNoteLifecycle selectedShort
            ? selectedShort
            : instrument.ShortLifecycle;
        LongNoteLifecycle longLifecycle = field == "Long" && comboBox.SelectedItem is LongNoteLifecycle selectedLong
            ? selectedLong
            : instrument.LongLifecycle;
        if (shortLifecycle == instrument.ShortLifecycle && longLifecycle == instrument.LongLifecycle) return;
        if (!_session.CanEditProject)
        {
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
            return;
        }
        if (!RunSynchronous("Change Event Instrument Lifecycle", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
                    instrumentId,
                    shortLifecycle,
                    longLifecycle))))
        {
            _session.RefreshWorkspace(workspace);
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        }
    }

    private void OnInstrumentOverlapSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string field } comboBox
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        OverlapPolicy policy = field == "Policy" && comboBox.SelectedItem is OverlapPolicy selectedPolicy
            ? selectedPolicy
            : instrument.OverlapPolicy;
        OverlapScope scope = field == "Scope" && comboBox.SelectedItem is OverlapScope selectedScope
            ? selectedScope
            : instrument.OverlapScope;
        if (policy == instrument.OverlapPolicy && scope == instrument.OverlapScope) return;
        if (!_session.CanEditProject)
        {
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
            return;
        }
        if (!RunSynchronous("Change Event Instrument Overlap", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentOverlap(instrumentId, policy, scope))))
        {
            _session.RefreshWorkspace(workspace);
            comboBox.GetBindingExpression(Selector.SelectedItemProperty)?.UpdateTarget();
        }
    }

    private void OnInstrumentSectionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TabControl tabs) return;
        RestoreInstrumentConfigurationShortcutFocus(tabs);
    }

    private void OnInstrumentSectionsLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TabControl tabs
            || tabs.Items.OfType<TabItem>().FirstOrDefault(item =>
                string.Equals(item.Header as string, "Configurations", StringComparison.Ordinal))
                is not TabItem configurations)
        {
            return;
        }
        if (tabs.Items.IndexOf(configurations) != 0)
        {
            int requestedIndex = tabs.DataContext is InstrumentWorkspaceViewModel workspace
                ? workspace.ActiveSectionIndex
                : 0;
            tabs.Items.Remove(configurations);
            tabs.Items.Insert(0, configurations);
            tabs.SelectedIndex = Math.Clamp(requestedIndex, 0, tabs.Items.Count - 1);
        }
        RestoreInstrumentConfigurationShortcutFocus(tabs);
    }

    private void RestoreInstrumentConfigurationShortcutFocus(TabControl tabs)
    {
        if (tabs.SelectedItem is not TabItem { Header: "Configurations" }) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (tabs.IsVisible
                && tabs.IsEnabled
                && tabs.SelectedItem is TabItem { Header: "Configurations" })
            {
                tabs.Focus();
            }
        });
    }

    private void OnInstrumentNavigationScrollLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer
            {
                DataContext: InstrumentWorkspaceViewModel workspace
            } scrollViewer)
        {
            scrollViewer.ScrollToVerticalOffset(workspace.LeftPaneVerticalOffset);
        }
    }

    private void OnInstrumentNavigationScrollChanged(
        object sender,
        ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer
            {
                DataContext: InstrumentWorkspaceViewModel workspace
            } scrollViewer
            && e.VerticalChange != 0)
        {
            workspace.LeftPaneVerticalOffset = scrollViewer.VerticalOffset;
        }
    }

    private void OnInstrumentNavigationPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || e.Handled) return;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (FindVisualAncestor<ListBox>(source) is not null)
        {
            return;
        }

        ScrollBar? scrollBar = FindVisualAncestor<ScrollBar>(source);
        if (scrollBar is { Orientation: Orientation.Vertical }
            && FindVisualAncestor<ListBox>(scrollBar) is null)
        {
            return;
        }

        e.Handled = true;
    }

    private void OnInstrumentStructureListPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox || e.Handled) return;
        ListBoxWheelScroll.ScrollOneItemPerNotch(listBox, e);
        e.Handled = true;
    }

    private void OnInstrumentLoopLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitInstrumentLoop();

    private async void OnInstrumentLoopTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace)
        {
            return;
        }

        CancellationTokenSource delay = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _instrumentLoopCommitDelay,
            delay);
        previous?.Cancel();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), delay.Token);
            if (delay.IsCancellationRequested
                || !_session.CanEditProject
                || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                    is not EventInstrument instrument
                || !TryParseLoopDraft(workspace, instrument, out long? start, out long? end))
            {
                return;
            }
            ExecuteInstrumentLoopUpdate(workspace, instrumentId, instrument, start, end);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _instrumentLoopCommitDelay,
                null,
                delay);
            delay.Dispose();
        }
    }

    private void OnInstrumentLoopKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitInstrumentLoop();
        e.Handled = true;
    }

    private void CommitInstrumentLoop()
    {
        CancellationTokenSource? pending = Interlocked.Exchange(
            ref _instrumentLoopCommitDelay,
            null);
        pending?.Cancel();
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                is not EventInstrument instrument)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (!RunSynchronous("Change Event Instrument Loop", () =>
        {
            long? start = ParseOptionalTick(workspace.LoopStartText, "Loop Start");
            long? end = ParseOptionalTick(workspace.LoopEndText, "Loop End");
            if (instrument.LoopStartTick == start && instrument.LoopEndTick == end)
            {
                return;
            }
            _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentLoop(
                instrumentId,
                start,
                end));
        }))
        {
            _session.RefreshWorkspace(workspace);
        }
    }

    private void ExecuteInstrumentLoopUpdate(
        InstrumentWorkspaceViewModel workspace,
        MidoraId instrumentId,
        EventInstrument instrument,
        long? start,
        long? end)
    {
        if (instrument.LoopStartTick == start && instrument.LoopEndTick == end) return;
        if (!_session.CanEditProject)
        {
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (!RunSynchronous("Change Event Instrument Loop", () => _session.Execute(
                ProjectDomainEditCommands.UpdateEventInstrumentLoop(instrumentId, start, end))))
        {
            _session.RefreshWorkspace(workspace);
        }
    }

    private static bool TryParseLoopDraft(
        InstrumentWorkspaceViewModel workspace,
        EventInstrument instrument,
        out long? start,
        out long? end)
    {
        start = null;
        end = null;
        string startText = workspace.LoopStartText.Trim();
        string endText = workspace.LoopEndText.Trim();
        if (startText.Length == 0 && endText.Length == 0) return true;
        if (!instrument.RequiresChannelIsolation)
        {
            return false;
        }

        if (startText.Length > 0)
        {
            if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedStart)
                || parsedStart < 0
                || parsedStart >= instrument.TemplateLengthTicks)
            {
                return false;
            }
            start = parsedStart;
        }
        if (endText.Length > 0)
        {
            if (!long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedEnd)
                || parsedEnd <= 0
                || parsedEnd > instrument.TemplateLengthTicks)
            {
                return false;
            }
            end = parsedEnd;
        }
        if (start.HasValue && end.HasValue && end.Value <= start.Value)
        {
            return false;
        }
        return true;
    }

    private T? FindWorkspaceElement<T>(object tag) where T : FrameworkElement
    {
        return FindDescendant<T>(WorkspaceTabs, element =>
            (Equals(element.Tag, tag) || Equals(tag, "PrimaryTimeline") && Equals(element.Tag, "ConductorTempo"))
            && element.IsVisible
            && ReferenceEquals(element.DataContext, _session.ActiveWorkspace));
    }

    private static T? FindDescendant<T>(DependencyObject root, Predicate<T> predicate)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate)) return candidate;
            T? nested = FindDescendant(child, predicate);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<T> EnumerateDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate) yield return candidate;
            foreach (T nested in EnumerateDescendants<T>(child)) yield return nested;
        }
    }

    private static long? ParseOptionalTick(string value, string label)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return long.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out long tick)
            ? tick
            : throw new FormatException($"{label} must be blank or a base-10 integer.");
    }

    private void OnAddInstrumentStateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            })
        {
            return;
        }
        MidiStateEntryDialog dialog = new() { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Target is not MidiValueTarget target) return;
        RunSynchronous("Add Instrument MIDI State", () =>
        {
            MidoraId? selectedSubVoice = sender switch
            {
                FrameworkElement { Tag: "ActiveSubVoice" } => workspace.ActiveSubVoiceId,
                FrameworkElement { Tag: "Instrument" } => null,
                _ => workspace.Selection.Primary
            };
            if (selectedSubVoice is MidoraId selected
                && _session.Project!.EventInstruments.Single(item => item.Id == instrumentId)
                    .SubVoices.Any(item => item.Id == selected))
            {
                _session.Execute(ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
                    instrumentId, selected, target, dialog.Value));
            }
            else
            {
                _session.Execute(ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
                    instrumentId, target, dialog.Value));
            }
        });
    }

    private void OnPreviewNotePressed(object? sender, PianoKeyEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || _session.Project is null)
        {
            return;
        }
        if (workspace.IsPreviewMuted)
        {
            _session.SetStatusMessage("Preview is muted for this Event Instrument Workspace.", isError: true);
            return;
        }
        MidoraId? subVoiceId = workspace.PreviewMode == InstrumentPreviewMode.SelectedSubVoice
            || workspace.IsPreviewSoloSelected
            ? workspace.ActiveSubVoiceId
            : null;
        if ((workspace.PreviewMode == InstrumentPreviewMode.SelectedSubVoice
             || workspace.IsPreviewSoloSelected)
            && subVoiceId is null)
        {
            _session.SetStatusMessage(
                "Create or select a SubVoice before using Selected SubVoice Preview.",
                isError: true);
            return;
        }
        RunSynchronous("Start Held Preview", () => _session.StartHeldEventInstrumentPreview(
            new EventInstrumentPreviewRequest(
                instrumentId,
                subVoiceId,
                e.Note,
                e.Velocity,
                GateLengthTicks: null,
                CursorTick: _session.CurrentTick)));
    }

    private void OnPreviewNoteReleased(object? sender, PianoKeyEventArgs e) =>
        RunSynchronous("End Held Preview", _session.EndHeldPreviewGate);

    private void OnAddLogicalParameterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        string name = UniqueName("Parameter", instrument.LogicalParameters.Select(item => item.Name));
        LogicalParameterDefinitionDialog dialog = new(name) { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.DefinitionEdit is not { } edit) return;
        RunSynchronous("Create Logical Parameter", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalParameter(
                instrumentId,
                dialog.ParameterName,
                edit.Type,
                edit.Minimum,
                edit.Maximum,
                edit.DisplayMinimum,
                edit.DisplayMaximum,
                edit.DefaultValue,
                edit.UsesExplicitEnumValues,
                enumItems: edit.EnumItems),
            workspace));
    }

    private void OnAddLogicalParameterEventBindingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || _session.Project is null)
        {
            return;
        }

        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterEventBindingDialog dialog = new(
            instrument,
            workspace.ActiveSubVoiceId,
            request => TrySubmitDialogEdit(() => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateLogicalParameterEventBinding(
                    instrumentId,
                    request),
                workspace)))
        {
            Owner = this
        };
        _ = ShowModalDialog(dialog);
    }

    private void OnAddEnumItemClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection: { Primary: MidoraId parameterId }
            }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterDefinition? parameter = instrument.LogicalParameters.FirstOrDefault(item => item.Id == parameterId);
        if (parameter is null || parameter.Type != LogicalParameterType.Enum)
        {
            ShowUnavailable("Add Enum Item", "Select an Enum Logical Parameter first.");
            return;
        }
        string name = UniqueName("Item", parameter.EnumItems.Select(item => item.Name));
        int? explicitValue = null;
        if (parameter.UsesExplicitEnumValues)
        {
            int start = checked((int)Math.Ceiling(parameter.Minimum));
            int end = checked((int)Math.Floor(parameter.Maximum));
            HashSet<int> used = parameter.EnumItems.Select(item => item.Value).ToHashSet();
            for (int offset = 0; offset <= parameter.EnumItems.Count; offset++)
            {
                long candidate = (long)start + offset;
                if (candidate > end) break;
                if (!used.Contains((int)candidate))
                {
                    explicitValue = (int)candidate;
                    break;
                }
            }
            if (!explicitValue.HasValue)
            {
                ShowUnavailable("Add Enum Item", "The current explicit Enum legal range has no unused integer value.");
                return;
            }
        }
        RunSynchronous("Create Enum Item", () => _session.Execute(
            ProjectDomainEditCommands.CreateLogicalParameterEnumItem(
                instrumentId, parameterId, name, explicitValue)));
    }

    private async void OnEditLogicalParameterDefinitionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection: { Primary: MidoraId parameterId }
            }
            || _session.Project is null)
        {
            ShowUnavailable("Edit Logical Parameter Definition", "Select a Logical Parameter first.");
            return;
        }
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterDefinition? parameter = instrument.LogicalParameters.FirstOrDefault(item => item.Id == parameterId);
        if (parameter is null)
        {
            ShowUnavailable("Edit Logical Parameter Definition", "The current selection is not a Logical Parameter.");
            return;
        }
        MidoraProject project = _session.Project;
        ProjectDocumentSession document = _session.Document!;
        long revision = document.PublicationRevision;
        int laneCount = 0, pointCount = 0;
        if (!await RunOperationAsync("Read Logical Parameter References", async token =>
        {
            (laneCount, pointCount) = await Task.Run(() =>
            {
                int lanes = 0, points = 0;
                foreach (var track in project.Tracks)
                    foreach (var segment in track.Segments)
                        foreach (var lane in segment.ParameterLanes)
                        {
                            token.ThrowIfCancellationRequested();
                            if (lane.ParameterId != parameterId) continue;
                            lanes = checked(lanes + 1);
                            points = checked(points + lane.Points.Count);
                        }
                return (lanes, points);
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session.Document, document) || document.PublicationRevision != revision)
                throw new InvalidOperationException("The Project changed while reading parameter references.");
        }, canCancel: true)) return;
        LogicalParameterDefinitionDialog dialog = new(parameter, laneCount, pointCount)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.DefinitionEdit is null) return;
        await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.MigrateLogicalParameterDefinition(
                instrumentId,
                parameterId,
                dialog.DefinitionEdit,
                dialog.MigrationMode,
                dialog.EnumSemanticWarningAcknowledged,
                dialog.ParameterName));
    }

    private void OnAddMappingFunctionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId } }
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        string name = UniqueName("Mapping", instrument.MappingFunctions.Select(item => item.Name));
        ShowMappingFunctionDialog(instrumentId, functionId: null, name, "value");
    }

    private void OnMappingFunctionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox
            {
                DataContext: InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId },
                SelectedItem: MappingFunctionListItem function
            })
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(
                () => ShowMappingFunctionDialog(instrumentId, function.Id),
                DispatcherPriority.Normal);
        }
    }

    private void OnEditMappingFunctionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId functionId
            }
            || _session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
                ?.MappingFunctions.Any(value => value.Id == functionId) != true)
        {
            ShowUnavailable("Mapping Function Properties", "Select a Mapping Function first.");
            return;
        }
        ShowMappingFunctionDialog(instrumentId, functionId);
        e.Handled = true;
    }

    private void ShowMappingFunctionDialog(
        MidoraId instrumentId,
        MidoraId? functionId,
        string? initialName = null,
        string? initialExpression = null)
    {
        if (_session.Project?.EventInstruments.FirstOrDefault(value => value.Id == instrumentId)
            is not EventInstrument instrument)
        {
            ShowUnavailable("Mapping Function", "The Event Instrument no longer exists.");
            return;
        }

        CSharpMappingFunction? function = functionId is MidoraId id
            ? instrument.MappingFunctions.FirstOrDefault(value => value.Id == id)
            : null;
        if (functionId.HasValue && function is null)
        {
            ShowUnavailable("Mapping Function", "The Mapping Function no longer exists.");
            return;
        }

        HashSet<MidoraId> before = instrument.MappingFunctions.Select(value => value.Id).ToHashSet();
        MappingFunctionDialog dialog = new(
            function is null ? "New Mapping Function" : "Mapping Function Properties",
            function?.Name ?? initialName ?? UniqueName("Mapping", instrument.MappingFunctions.Select(value => value.Name)),
            function?.Body ?? initialExpression ?? "value",
            submission =>
            {
                try
                {
                    if (function is null)
                    {
                        _session.Execute(ProjectDomainEditCommands.CreateMappingFunction(
                            instrumentId,
                            submission.Name,
                            submission.Expression,
                            submission.ReferencedContextFields));
                        CSharpMappingFunction created = instrument.MappingFunctions.Single(value => !before.Contains(value.Id));
                        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel workspace)
                        {
                            workspace.Selection.Replace(created.Id);
                            _session.RefreshWorkspace(workspace);
                        }
                    }
                    else
                    {
                        _session.Execute(ProjectDomainEditCommands.UpdateMappingFunction(
                            instrumentId,
                            function.Id,
                            submission.Name,
                            submission.Expression,
                            submission.ReferencedContextFields));
                    }
                    return null;
                }
                catch (Exception exception)
                {
                    return exception.Message;
                }
            })
        {
            Owner = this
        };
        _ = ShowModalDialog(dialog);
    }

    private void OnInstrumentStructureSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingInstrumentStructureSelection
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || sender is not ListBox list
            || list.SelectedItem is null)
        {
            return;
        }
        SelectInstrumentStructureItem(list, workspace, list.SelectedItem);
        if (list.SelectedItem is SubVoiceListItem selected)
        {
            _session.ActivateSubVoiceEditor(workspace, selected.Id);
            SetInstrumentStructureVisualSelection(workspace, selected.Id);
        }
    }

    private void SelectInstrumentStructureItem(
        ListBox list,
        InstrumentWorkspaceViewModel workspace,
        object item)
    {
        if (!TryGetInstrumentStructureItemId(item, out MidoraId selected)) return;
        workspace.SelectedMappingStepId = item is MappingStepListItem ? selected : null;
        _synchronizingInstrumentStructureSelection = true;
        try
        {
            foreach (ListBox candidate in EnumerateDescendants<ListBox>(WorkspaceTabs))
            {
                if (ReferenceEquals(candidate, list)
                    || !ReferenceEquals(candidate.DataContext, workspace)
                    || candidate.SelectedItem is null
                    || !TryGetInstrumentStructureItemId(candidate.SelectedItem, out _))
                {
                    continue;
                }
                candidate.UnselectAll();
            }
        }
        finally
        {
            _synchronizingInstrumentStructureSelection = false;
        }
        _session.SelectWorkspaceObject(workspace, selected);
    }

    private static bool TryGetInstrumentStructureItemId(object? item, out MidoraId id)
    {
        MidoraId? candidate = item switch
        {
            SubVoiceListItem value => value.Id,
            LogicalParameterListItem value => value.Id,
            MappingFunctionListItem value => value.Id,
            ParameterMappingListItem value => value.Id,
            MappingChainListItem value => value.Id,
            MappingStepListItem value => value.Id,
            EnvelopeListItem value => value.Id,
            _ => null
        };
        if (candidate is MidoraId resolved)
        {
            id = resolved;
            return true;
        }
        id = default;
        return false;
    }

    private void ClearInstrumentStructureVisualSelection(InstrumentWorkspaceViewModel workspace)
    {
        workspace.SelectedMappingStepId = null;
        _synchronizingInstrumentStructureSelection = true;
        try
        {
            foreach (ListBox candidate in EnumerateDescendants<ListBox>(WorkspaceTabs))
            {
                if (ReferenceEquals(candidate.DataContext, workspace)
                    && candidate.SelectedItem is not null
                    && TryGetInstrumentStructureItemId(candidate.SelectedItem, out _))
                {
                    candidate.UnselectAll();
                }
            }
        }
        finally
        {
            _synchronizingInstrumentStructureSelection = false;
        }
    }

    private void SetInstrumentStructureVisualSelection(
        InstrumentWorkspaceViewModel workspace,
        MidoraId selectedId)
    {
        workspace.SelectedMappingStepId = workspace.MappingSteps.Any(value => value.Id == selectedId)
            ? selectedId
            : null;
        _synchronizingInstrumentStructureSelection = true;
        try
        {
            bool selectedOne = false;
            foreach (ListBox candidate in EnumerateDescendants<ListBox>(WorkspaceTabs))
            {
                if (!ReferenceEquals(candidate.DataContext, workspace)) continue;
                object? matchingItem = candidate.Items.Cast<object>()
                    .FirstOrDefault(value =>
                        TryGetInstrumentStructureItemId(value, out MidoraId id)
                        && id == selectedId);
                if (!selectedOne && matchingItem is not null)
                {
                    candidate.SelectedItem = matchingItem;
                    selectedOne = true;
                }
                else if (candidate.SelectedItem is not null
                    && TryGetInstrumentStructureItemId(candidate.SelectedItem, out _))
                {
                    candidate.UnselectAll();
                }
            }
        }
        finally
        {
            _synchronizingInstrumentStructureSelection = false;
        }
    }

    private static string UniqueName(string basis, IEnumerable<string> existing)
    {
        HashSet<string> names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; ; index++)
        {
            string candidate = $"{basis} {index}";
            if (!names.Contains(candidate)) return candidate;
        }
    }

    private void OnOpenArrangementClick(object sender, RoutedEventArgs e)
    {
        if (_session.HasProject) _session.OpenArrangement();
    }

    private void OnOpenDiagnosticsClick(object sender, RoutedEventArgs e) => OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);

    private void OnDiagnosticSummaryMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);
        e.Handled = true;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        RunAfterMenuClosed(sender, () => OpenTreeWorkspace(ProjectTreeNodeKind.ProjectSettings));
    }

    private void OnAddProjectStateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string scope }) return;
        MidiStateEntryDialog dialog = new() { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Target is not MidiValueTarget target) return;
        RunSynchronous("Add Project MIDI State", () => _session.Execute(
            scope == "reset"
                ? ProjectDomainEditCommands.UpdateProjectResetDefaultValue(target, dialog.Value)
                : ProjectDomainEditCommands.UpdateProjectInitialStateValue(target, dialog.Value)));
    }

    private void OnToggleProjectPanelClick(object sender, RoutedEventArgs e)
    {
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { ProjectPanelVisible = ProjectPanelMenuItem.IsChecked }
        };
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnToggleSnapClick(object sender, RoutedEventArgs e)
    {
        GetActiveEditorSettings().SnapEnabled = SnapMenuItem.IsChecked;
    }

    private void OnToggleFollowPlaybackClick(object sender, RoutedEventArgs e)
    {
        SetFollowPlaybackEnabled(FollowPlaybackMenuItem.IsChecked);
    }

    private void OnFollowPlaybackToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            SetFollowPlaybackEnabled(toggle.IsChecked == true);
        }
    }

    private void SetFollowPlaybackEnabled(bool enabled)
    {
        _followPlaybackViewportInteractionActive = false;
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with { FollowPlayback = enabled }
        };
        FollowPlaybackMenuItem.IsChecked = enabled;
        FollowPlaybackToggleButton.IsChecked = enabled;
        SaveDesktopPreferences();
        if (enabled)
        {
            FollowActivePlayback(force: true);
        }
    }

    private void OnResetLayoutClick(object sender, RoutedEventArgs e)
    {
        DesktopUiPreferences defaults = DesktopUiPreferences.Default;
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with
            {
                ProjectPanelWidth = defaults.ProjectPanelWidth,
                ProjectPanelVisible = true
            }
        };
        ProjectPanelColumn.Width = new(defaults.ProjectPanelWidth);
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnResetEditorPreferencesClick(object sender, RoutedEventArgs e)
    {
        foreach (TimelineWorkspaceViewModel workspace in _session.Workspaces.OfType<TimelineWorkspaceViewModel>())
        {
            workspace.ResetViewport();
        }
        if (_session.Project is not null)
        {
            _session.ArrangementEditorSettings.Reset(true, _session.Project.TicksPerQuarterNote);
            _session.ResetPianoEditorPreferences();
        }
        _preferences = _preferences with
        {
            DesktopUi = _preferences.DesktopUi with
            {
                FollowPlayback = DesktopUiPreferences.Default.FollowPlayback
            }
        };
        ApplyPanelVisibility();
        SaveDesktopPreferences();
    }

    private void OnResetAllUiPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (MessageDialog.Show(
                this,
                "Reset all local UI preferences, including panel layout, timeline Snap, and Follow Playback? Audio preferences are not affected.",
                "Reset All UI Preferences",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        _preferences = _preferences with { DesktopUi = DesktopUiPreferences.Default };
        OnResetLayoutClick(sender, e);
        OnResetEditorPreferencesClick(sender, e);
    }

    private void OpenTreeWorkspace(ProjectTreeNodeKind kind)
    {
        ProjectTreeNode? node = _session.ProjectTree.FirstOrDefault(item => item.Kind == kind);
        if (node is not null) _session.OpenWorkspace(node);
    }

    private void OnCloseWorkspaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceViewModel workspace })
        {
            _session.CloseWorkspace(workspace);
            e.Handled = true;
        }
    }

    private void OnCloseOtherWorkspacesClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceViewModel keep }) return;
        foreach (WorkspaceViewModel workspace in _session.Workspaces.Where(item => !ReferenceEquals(item, keep)).ToArray())
        {
            _session.CloseWorkspace(workspace);
        }
        _session.ActiveWorkspace = keep;
    }

    private void OnTimelineItemInvoked(object? sender, TimelineItemEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        if (workspace is TimelineWorkspaceViewModel { IsConductor: true })
            _conductorListSelectionCancellation.Cancel();
        // The clicked lane is part of the selection's formal presentation
        // source. Publish it before deriving the lightweight routing context so
        // a click that also switches lanes cannot inherit the previous lane.
        workspace.ActiveLane = e.Item.Lane;
        WorkspaceTimelineSelectionSource? timelineSource = sender is TimelineSurface sourceSurface
            ? GetTimelineSelectionSource(workspace, sourceSurface, e.Item)
            : null;
        void AddSelection(MidoraId id)
        {
            if (timelineSource is WorkspaceTimelineSelectionSource source)
                workspace.Selection.Add(id, source);
            else
                workspace.Selection.Add(id);
        }
        void ReplaceSelection(MidoraId id)
        {
            if (timelineSource is WorkspaceTimelineSelectionSource source)
                workspace.Selection.Replace(id, source);
            else
                workspace.Selection.Replace(id);
        }
        void ToggleSelection(MidoraId id)
        {
            if (timelineSource is WorkspaceTimelineSelectionSource source)
                workspace.Selection.Toggle(id, source);
            else
                workspace.Selection.Toggle(id);
        }
        if (e.IsContextRequest)
        {
            if (!workspace.Selection.IdSet.Contains(e.Item.Id))
            {
                ReplaceSelection(e.Item.Id);
                _session.RefreshWorkspaceSelection(workspace);
            }
            return;
        }
        bool replaceDrawSegmentSelection = ShouldReplaceDrawSegmentSelection(
            (sender as TimelineSurface)?.ToolMode,
            e.Item.Kind,
            e.Modifiers,
            e.PreserveSelectionForPotentialCopyDrag);
        if (e.PreserveExistingSelection)
        {
            long selectionRevision = workspace.Selection.Revision;
            AddSelection(e.Item.Id);
            if (workspace.Selection.Revision != selectionRevision)
            {
                _session.RefreshWorkspaceSelection(workspace);
            }
        }
        else if (replaceDrawSegmentSelection)
        {
            long selectionRevision = workspace.Selection.Revision;
            ReplaceSelection(e.Item.Id);
            if (workspace.Selection.Revision != selectionRevision)
            {
                _session.RefreshWorkspaceSelection(workspace);
            }
        }
        else if (!e.PreserveSelectionForPotentialCopyDrag)
        {
            long selectionRevision = workspace.Selection.Revision;
            if (e.IsCopyDragStart) AddSelection(e.Item.Id);
            else if ((e.Modifiers & ModifierKeys.Control) != 0) ToggleSelection(e.Item.Id);
            else if ((e.Modifiers & ModifierKeys.Shift) != 0) AddSelection(e.Item.Id);
            else if (workspace.Selection.Ids.Contains(e.Item.Id)) AddSelection(e.Item.Id);
            else ReplaceSelection(e.Item.Id);
            if (workspace.Selection.Revision != selectionRevision)
            {
                _session.RefreshWorkspaceSelection(workspace);
            }
        }
        if (sender is TimelineSurface { ToolMode: TimelineToolMode.Draw }
            && e.Item.Kind is TimelineItemKind.LogicalNote
                or TimelineItemKind.DirectMidiNote
                or TimelineItemKind.TemplateNote)
        {
            GetActiveEditorSettings().DefaultLengthTicks = Math.Max(1, e.Item.Length);
        }
        if (sender is TimelineSurface { ToolMode: TimelineToolMode.Erase }
            && _session.CanEditProject)
        {
            DeleteWorkspaceSelection();
            return;
        }
        if (e.IsDoubleClick && e.Item.Kind == TimelineItemKind.Segment)
        {
            _session.OpenSegment(e.Item.Id);
        }
    }

    private void OnOpenWorkspaceSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement,
                Selection.Primary: MidoraId segmentId
            }
            && _session.Project?.Tracks.SelectMany(track => track.Segments)
                .Any(segment => segment.Id == segmentId) == true)
        {
            _session.OpenSegment(segmentId);
            return;
        }
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId functionId
            }
            && _session.Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                ?.MappingFunctions.Any(function => function.Id == functionId) == true)
        {
            ShowMappingFunctionDialog(instrumentId, functionId);
        }
    }

    private void OnDeleteWorkspaceSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.CanEditProject) DeleteWorkspaceSelection();
    }

    private void OnTimelineToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton
            {
                Tag: string value,
                DataContext: TimelineWorkspaceViewModel workspace
            } button
            || !Enum.TryParse(value, out TimelineToolMode mode))
        {
            return;
        }
        workspace.ToolMode = mode;
        button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
    }

    private void OnInstrumentTimelineToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton
            {
                Tag: string value,
                DataContext: InstrumentWorkspaceViewModel workspace
            } button
            && Enum.TryParse(value, out TimelineToolMode mode))
        {
            workspace.ToolMode = mode;
            button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        }
    }

    private void OnFollowInstanceVelocityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox
            {
                IsChecked: bool follows,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    ActiveSubVoiceId: MidoraId subVoiceId
                } workspace
            } checkBox)
        {
            return;
        }
        if (!_session.CanEditProject)
        {
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
            return;
        }

        if (!RunSynchronous(
                "Change Follow Instance Velocity",
                () => _session.Execute(
                    ProjectDomainEditCommands.SetSubVoiceFollowInstanceVelocity(
                        instrumentId,
                        subVoiceId,
                        follows))))
        {
            _session.RefreshWorkspace(workspace);
            checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        }
    }

    private void OnSelectInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            })
        {
            workspace.Selection.Replace(instrumentId);
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnTimelineSnapClick(object sender, RoutedEventArgs e)
    {
        SnapMenuItem.IsChecked = !GetActiveEditorSettings().SnapEnabled;
        OnToggleSnapClick(SnapMenuItem, e);
    }

    private void OnTimelineZoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } source) return;
        double factor = direction == "In" ? 0.8 : 1.25;
        if (source.DataContext is InstrumentWorkspaceViewModel)
        {
            TimelineSurface? surface = FindWorkspaceElement<TimelineSurface>("SubVoiceNotes");
            if (surface is null) return;
            long nextSpan = TimelineTickMath.ScaleSpan(surface.TickSpan, factor);
            long centerTick = surface.StartTick <= long.MaxValue - surface.TickSpan / 2
                ? surface.StartTick + surface.TickSpan / 2
                : long.MaxValue;
            surface.TickSpan = nextSpan;
            surface.StartTick = Math.Max(0, centerTick - nextSpan / 2);
            return;
        }
        if (source.DataContext is not TimelineWorkspaceViewModel workspace) return;
        long span = TimelineTickMath.ScaleSpan(workspace.TickSpan, factor);
        long center = workspace.StartTick <= long.MaxValue - workspace.TickSpan / 2
            ? workspace.StartTick + workspace.TickSpan / 2
            : long.MaxValue;
        workspace.TickSpan = span;
        workspace.StartTick = Math.Max(0, center - span / 2);
    }

    private void OnTimelineVerticalZoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string direction } source) return;
        object timelineTag = source.DataContext is InstrumentWorkspaceViewModel
            ? "SubVoiceNotes"
            : "PrimaryTimeline";
        FindWorkspaceElement<TimelineSurface>(timelineTag)
            ?.AdjustVerticalZoom(string.Equals(direction, "In", StringComparison.Ordinal));
    }

    private void OnSubdivisionComboBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string role } comboBox) return;
        bool laneOperation = string.Equals(role, "LaneOperation", StringComparison.Ordinal);
        TimelineEditorSettings? settings = comboBox.DataContext switch
        {
            TimelineWorkspaceViewModel timeline => laneOperation
                ? timeline.LaneEditorSettings
                : timeline.EditorSettings,
            InstrumentWorkspaceViewModel instrument => laneOperation
                ? instrument.EventLaneEditorSettings
                : instrument.EditorSettings,
            _ => null
        };
        if (settings is null) return;
        if (TimelineSubdivision.TryParse(comboBox.Text, out TimelineSubdivision parsed))
        {
            TimelineSubdivision selection = settings.SubdivisionPresets.FirstOrDefault(candidate =>
                candidate.IsBar == parsed.IsBar
                && candidate.Numerator == parsed.Numerator
                && candidate.Denominator == parsed.Denominator);
            if (selection == default) selection = parsed;
            if (string.Equals(role, "Display", StringComparison.Ordinal))
            {
                settings.DisplaySubdivision = selection;
            }
            else
            {
                settings.OperationSubdivision = selection;
            }
        }
        comboBox.SelectedItem = string.Equals(role, "Display", StringComparison.Ordinal)
            ? settings.DisplaySubdivision
            : settings.OperationSubdivision;
        comboBox.Text = string.Equals(role, "Display", StringComparison.Ordinal)
            ? settings.DisplaySubdivisionText
            : settings.OperationSubdivisionText;
    }

    private void OnTimelineGridDivisionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string value }
            || _session.Project is null)
        {
            return;
        }
        TimelineSubdivision subdivision;
        if (TimelineSubdivision.TryParse(value, out TimelineSubdivision parsed))
        {
            subdivision = parsed;
        }
        else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int legacyDivisions)
            && legacyDivisions > 0)
        {
            int denominator = checked(legacyDivisions * 4);
            subdivision = new(1, denominator, $"1/{denominator}");
        }
        else
        {
            return;
        }
        GetActiveEditorSettings().DisplaySubdivision = subdivision;
    }

    private void OnTimelineLaneHeaderCommandInvoked(
        object? sender,
        TimelineLaneHeaderCommandEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace)
        {
            return;
        }
        ArrangementLaneDescriptor? lane = workspace.GetArrangementLane(e.Lane);
        if (lane is null) return;
        if (e.Command == TimelineLaneHeaderCommand.ToggleExpanded) return;
        if (lane.Value.ObjectId is not MidoraId objectId
            || lane.Value.Kind == ArrangementLaneKind.Conductor)
        {
            return;
        }
        RunSynchronous(
            e.Command == TimelineLaneHeaderCommand.ToggleMute ? "Toggle Mute" : "Toggle Solo",
            () =>
            {
                if (e.Command == TimelineLaneHeaderCommand.ToggleMute)
                {
                    _session.SetTrackMuted(objectId, !_session.IsTrackMuted(objectId));
                }
                else
                {
                    _session.SetTrackSolo(objectId, !_session.IsTrackSolo(objectId));
                }
            });
    }

    private void OnTimelineLaneHeaderInvoked(object? sender, TimelineLaneHeaderEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
            && arrangement.GetArrangementLane(e.Lane) is ArrangementLaneDescriptor arrangementLane)
        {
            if (e.IsSharedGroupTarget) return;
            _trackHeaderContextLane = e.Lane;
            bool alreadySelected = arrangementLane.Kind == ArrangementLaneKind.Conductor
                ? arrangement.IsConductorTrackSelected
                : arrangementLane.ObjectId is MidoraId trackId
                    && arrangement.SelectedArrangementTrackId == trackId;
            if (alreadySelected)
            {
                ClearArrangementTrackSelection(arrangement);
            }
            else
            {
                SelectArrangementTrack(arrangement, arrangementLane);
            }
        }
        else
        {
            _trackHeaderContextLane = e.Lane;
            workspace.ActiveLane = e.Lane;
        }
        if (sender is TimelineSurface { Tag: "ParameterLanes" }
            && workspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timelineWorkspace
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located
            && timelineWorkspace.GetActiveParameterLaneOption()?.LaneId is MidoraId activeLaneId)
        {
            workspace.Selection.Replace(activeLaneId);
            _session.RefreshWorkspace(workspace);
            return;
        }
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.GetRenderLane(e.Lane) is InstrumentRenderLane lane)
        {
            workspace.Selection.Replace(
                lane.ValueCurveId
                ?? lane.EventMappingChainId
                ?? lane.SubVoiceId);
            _session.RefreshWorkspace(workspace);
        }
    }

    private void OnArrangementAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: ContextMenu menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void OnArrangementInstrumentInvoked(
        object? sender,
        TimelineArrangementInstrumentEventArgs e)
    {
        _session.OpenInstrument(e.InstrumentId);
    }

    private void OnArrangementMidiRouteInvoked(
        object? sender,
        TimelineArrangementMidiRouteEventArgs e) =>
        ShowMidiRouteSettings(e.TrackId, e.RootId);

    private void OnOpenInstrumentPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (OpenInstrumentProperties()) e.Handled = true;
    }

    private bool OpenInstrumentProperties()
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.Selection.Primary is null)
        {
            ShowUnavailable(
                "Properties",
                "Select an Event Instrument object first.");
            return false;
        }
        _ = OpenWorkspacePropertiesAsync(workspace);
        return true;
    }

    private void OnTimelineContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: TimelineSurface surface })
        {
            surface.SetArrangementSharedGroupContextHighlight(null);
        }
    }

    private void OnResetAllTrackMonitoringClick(object sender, RoutedEventArgs e) =>
        RunSynchronous("Reset Track Monitoring", _session.ResetAllTrackMonitoringStates);

    private static EventInstrumentBrowserRow? EventInstrumentBrowserRowFrom(object sender) =>
        sender switch
        {
            ListBox { SelectedItem: EventInstrumentBrowserRow row } => row,
            FrameworkElement { DataContext: EventInstrumentBrowserRow row } => row,
            _ => null
        };

    private void OnEventInstrumentBrowserRightButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current is not null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
        {
            list.SelectedItem = item.DataContext;
        }
        else
        {
            list.SelectedItem = null;
        }
    }

    private void OnEventInstrumentBrowserDoubleClick(object sender, MouseButtonEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        if (sender is ListBox { ContextMenu: { } menu }) menu.IsOpen = false;
        e.Handled = true;
        _session.OpenInstrument(row.Id);
    }

    internal static bool ShouldReplaceDrawSegmentSelection(
        TimelineToolMode? toolMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers,
        bool preserveSelectionForPotentialDrag) =>
        toolMode == TimelineToolMode.Draw
        && itemKind == TimelineItemKind.Segment
        && modifiers == ModifierKeys.None
        && !preserveSelectionForPotentialDrag;

    private void OnEventInstrumentBrowserEditClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is not null) _session.OpenInstrument(row.Id);
    }

    private void OnEventInstrumentBrowserAddTrackClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        RunSynchronous("Create Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.CreateLogicalTrack(
                eventInstrumentId: row.Id)));
    }

    private void OnEventInstrumentBrowserDuplicateClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        StartWorkspaceEdit(ProjectDomainEditCommands.DuplicateEventInstrumentOnly(row.Id));
    }

    private void OnEventInstrumentBrowserCopyClick(object sender, RoutedEventArgs e) =>
        CopyEventInstrumentBrowserItem(sender, cut: false);

    private void OnEventInstrumentBrowserCutClick(object sender, RoutedEventArgs e) =>
        CopyEventInstrumentBrowserItem(sender, cut: true);

    private void CopyEventInstrumentBrowserItem(object sender, bool cut)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null
            || _session.Document is not ProjectDocumentSession document
            || cut && (!_session.CanEditProject || row.UsageCount != 0))
        {
            return;
        }
        MidoraId instrumentId = row.Id;
        StartClipboardTransfer(document, () => ProjectObjectClipboard.CopyEventInstrument(document, instrumentId),
            cut ? ProjectDomainEditCommands.DeleteEventInstrument(instrumentId, true) : null);
    }

    private void OnEventInstrumentBrowserPasteClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditProject
            || _session.Project is not MidoraProject project
            || _session.Document is not ProjectDocumentSession document
            || _projectClipboard is not ProjectObjectClipboardPayload
            {
                Kind: ProjectObjectClipboardKind.EventInstrument
            } payload
            || !ReferenceEquals(document, _clipboardDocument))
        {
            return;
        }
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        int insertionIndex = row is null
            ? project.EventInstruments.Count
            : project.EventInstruments.FindIndex(value => value.Id == row.Id) + 1;
        StartWorkspaceEdit(
            ProjectObjectClipboard.CreatePasteEventInstrumentCommand(
                document,
                payload,
                insertionIndex: insertionIndex));
    }

    private void OnEventInstrumentBrowserMoveUpClick(object sender, RoutedEventArgs e) =>
        MoveEventInstrumentBrowserItem(sender, -1);

    private void OnEventInstrumentBrowserMoveDownClick(object sender, RoutedEventArgs e) =>
        MoveEventInstrumentBrowserItem(sender, 1);

    private void MoveEventInstrumentBrowserItem(object sender, int direction)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null || _session.Project is not MidoraProject project) return;
        int index = project.EventInstruments.FindIndex(value => value.Id == row.Id);
        int target = index + direction;
        if (target < 0 || target >= project.EventInstruments.Count) return;
        RunSynchronous("Move Event Instrument", () => _session.Execute(
            ProjectDomainEditCommands.ReorderEventInstrument(row.Id, target)));
    }

    private void OnEventInstrumentBrowserRenameClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        TextInputDialog dialog = new(
            "Rename Event Instrument",
            "Enter the Event Instrument name.",
            row.Name)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous("Rename Event Instrument", () =>
            _session.Execute(ProjectDomainEditCommands.RenameEventInstrument(row.Id, dialog.Value)));
    }

    private void OnEventInstrumentBrowserDeleteClick(object sender, RoutedEventArgs e)
    {
        EventInstrumentBrowserRow? row = EventInstrumentBrowserRowFrom(sender);
        if (row is null) return;
        if (row.UsageCount != 0)
        {
            MessageDialog.Show(
                this,
                "This Event Instrument is still referenced by one or more usages. Remove or rebind those Logical Tracks first.",
                "Delete Event Instrument",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (MessageDialog.Show(
                this,
                $"Delete Event Instrument '{row.Name}'?",
                "Delete Event Instrument",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        StartWorkspaceEdit(ProjectDomainEditCommands.DeleteEventInstrument(row.Id, false));
    }

    private void OnTimelineLaneHeaderDoubleInvoked(object? sender, TimelineLaneHeaderEventArgs e)
    {
        if (e.IsSharedGroupTarget) return;
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || workspace.GetArrangementLane(e.Lane) is not ArrangementLaneDescriptor lane)
        {
            return;
        }
        if (lane.Kind == ArrangementLaneKind.Conductor)
        {
            _session.OpenWorkspace(new ProjectTreeNode(ProjectTreeNodeKind.Conductor, "Conductor Track"));
        }
        else if (lane.Kind == ArrangementLaneKind.EventInstrument
                 && lane.ObjectId is MidoraId instrumentId)
        {
            _session.OpenInstrument(instrumentId);
        }
    }

    private async void OnOpenMidiAsNewProjectClick(object sender, RoutedEventArgs e)
    {
        if (!StopPlaybackForProjectCommand("Open MIDI as New Project")
            || !await ConfirmCloseCurrentProjectAsync()) return;
        OpenFileDialog dialog = new()
        {
            Title = "Open MIDI as New Midora Project",
            Filter = "Standard MIDI File (*.mid;*.midi)|*.mid;*.midi|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.OpenProject)
        };
        if (dialog.ShowDialog(this) != true) return;

        IReadOnlyDictionary<byte, byte>? portMap = null;
        IReadOnlyList<MidiProjectImportDiagnostic>? diagnostics = null;
        Exception? audioInitializationFailure = null;
        while (true)
        {
            MidiImportPortMappingRequiredException? mappingRequired = null;
            using DispatcherCoalescingProgress<MidiProjectImportProgress> progress = new(
                Dispatcher,
                TimeSpan.FromMilliseconds(100),
                value =>
                {
                    DesktopTaskViewModel? active = _session.ActiveForegroundTask;
                    if (active is not null)
                    {
                        _session.ReportTask(
                            active,
                            FormatMidiImportProgress(value),
                            value.Fraction);
                    }
                });
            bool succeeded = await RunOperationAsync(
                "Open MIDI as New Project",
                async cancellationToken =>
                {
                    diagnostics = await _session.ImportMidiAsNewProjectAsync(
                        dialog.FileName,
                        portMap,
                        cancellationToken,
                        progress);
                    _session.ActiveForegroundTask?.SetCancellationAvailable(false);
                    audioInitializationFailure =
                        await InitializeAudioWorkerForActiveProjectAsync(CancellationToken.None);
                },
                canCancel: true,
                handledException: exception =>
                {
                    mappingRequired = exception as MidiImportPortMappingRequiredException;
                    return mappingRequired is null
                        ? null
                        : "MIDI Port mapping review is required before import can continue.";
                });
            if (succeeded) break;
            if (mappingRequired is null) return;
            portMap = ReviewMidiImportPortMapping(mappingRequired.SourcePorts);
            if (portMap is null) return;
        }
        RecordRecentDirectory(RecentDirectoryPurpose.OpenProject, Path.GetDirectoryName(dialog.FileName));
        if (diagnostics is { Count: > 0 })
        {
            int warningCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Warning);
            int informationCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Info);
            string report = BuildMidiImportReport(diagnostics);
            _session.SetStatusMessage(
                $"MIDI import completed with {warningCount} warning(s) and {informationCount} information notice(s).",
                isError: false,
                details: report,
                detailsTitle: "MIDI Import Report");
            ShowMidiImportReport(report);
        }
        ReportAudioWorkerInitializationFailure(audioInitializationFailure);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            RestorePlaybackShortcutFocus);
    }

    private static string FormatMidiImportProgress(MidiProjectImportProgress value) =>
        value.Phase switch
        {
            MidiProjectImportPhase.ScanningSource =>
                $"Scanning MIDI source: {value.ProcessedEventCount:N0} event(s) found, "
                + $"{value.ProcessedSourceBytes:N0}/{value.TotalSourceBytes:N0} bytes",
            MidiProjectImportPhase.ImportingEvents =>
                $"Importing MIDI events: {value.ProcessedEventCount:N0}/{value.TotalEventCount:N0}",
            MidiProjectImportPhase.ValidatingProject => "Validating imported Project",
            MidiProjectImportPhase.FinalizingProject => "Finalizing imported Project",
            MidiProjectImportPhase.Completed =>
                $"Imported {value.TotalEventCount:N0} MIDI event(s)",
            _ => "Importing MIDI"
        };

    private static string BuildMidiImportReport(IReadOnlyList<MidiProjectImportDiagnostic> diagnostics)
    {
        int warningCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Warning);
        int informationCount = diagnostics.Count(value => value.Severity == DiagnosticSeverity.Info);
        StringBuilder report = new();
        report.AppendLine("The MIDI file was imported successfully after applying compatibility or preservation handling.");
        report.AppendLine();
        report.Append("Warnings: ").AppendLine(warningCount.ToString(CultureInfo.InvariantCulture));
        report.Append("Information: ").AppendLine(informationCount.ToString(CultureInfo.InvariantCulture));

        foreach (MidiProjectImportDiagnostic diagnostic in diagnostics)
        {
            report.AppendLine();
            report.Append('[').Append(diagnostic.Severity).Append("] ").AppendLine(diagnostic.Code);
            report.AppendLine(diagnostic.Message);
            List<string> location = [];
            if (diagnostic.SourceTrackIndex is int trackIndex)
            {
                location.Add($"MTrk {trackIndex}");
            }
            if (diagnostic.SourceByteOffset is int byteOffset)
            {
                location.Add($"byte {byteOffset}");
            }
            if (diagnostic.Tick is long tick)
            {
                location.Add($"tick {tick}");
            }
            if (diagnostic.ZeroBasedPort is byte port)
            {
                location.Add($"Port {port + 1}");
            }
            if (diagnostic.ZeroBasedChannel is byte channel)
            {
                location.Add($"Channel {channel + 1}");
            }
            if (location.Count != 0)
            {
                report.Append("Source: ").AppendLine(string.Join(", ", location));
            }
        }

        return report.ToString().TrimEnd();
    }

    private void ShowMidiImportReport(string report)
    {
        TextDetailsDialog dialog = new("MIDI Import Report", report)
        {
            Owner = this
        };
        _ = ShowModalDialog(dialog);
    }

    private IReadOnlyDictionary<byte, byte>? ReviewMidiImportPortMapping(
        IReadOnlyList<byte> sourcePorts)
    {
        if (sourcePorts.Count > 16)
        {
            ShowError(
                "MIDI Port Mapping",
                $"The source uses {sourcePorts.Count} distinct MIDI Ports; Midora supports at most 16.");
            return null;
        }
        Dictionary<byte, byte> result = [];
        HashSet<byte> used = [];
        foreach (byte sourcePort in sourcePorts.Order())
        {
            SelectionDialog selection = new(
                "Review MIDI Port Mapping",
                $"Map source Port {sourcePort + 1} to one unused Midora Port (1–16).",
                Enumerable.Range(0, 16)
                    .Select(value => checked((byte)value))
                    .Where(value => !used.Contains(value))
                    .Select(value => new SelectionDialogItem(
                        value,
                        $"Midora Port {value + 1}",
                        value == sourcePort && value < 16 ? "Same Port number" : string.Empty)))
            {
                Owner = this
            };
            if (ShowModalDialog(selection) != true || selection.SelectedValue is not byte targetPort)
                return null;
            result.Add(sourcePort, targetPort);
            used.Add(targetPort);
        }
        return result;
    }

    private void OnTimelineLaneHeaderContextRequested(object? sender, TimelineLaneHeaderEventArgs e)
    {
        _trackHeaderContextLane = e.Lane;
        if (_session.ActiveWorkspace is WorkspaceViewModel workspace)
        {
            if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                && arrangement.GetArrangementLane(e.Lane) is ArrangementLaneDescriptor arrangementLane)
            {
                if (!e.IsSharedGroupTarget)
                {
                    SelectArrangementTrack(arrangement, arrangementLane);
                }
            }
            else
            {
                workspace.ActiveLane = e.Lane;
            }
        }
    }

    private void OnTimelineLaneHeaderReorderCompleted(
        object? sender,
        TimelineLaneHeaderReorderEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || workspace.GetArrangementLane(e.SourceLane) is not ArrangementLaneDescriptor source
            || workspace.GetArrangementLane(e.TargetLane) is not ArrangementLaneDescriptor target
            || source.Kind == ArrangementLaneKind.Conductor)
        {
            return;
        }
        if (source.ObjectId is not MidoraId sourceTrackId
            || source.Kind is not (ArrangementLaneKind.LogicalTrack
                or ArrangementLaneKind.PureMidiTrack))
        {
            return;
        }

        ArrangementTrackReference sourceReference = project.ArrangementTracks
            .Single(value => value.TrackId == sourceTrackId);
        if (e.MovesWholeGroup && source is { IsSharedGroup: true, SharedGroupId: MidoraId groupId })
        {
            ArrangementTrackReference? targetReference = target.ObjectId is MidoraId wholeGroupTargetTrackId
                ? project.ArrangementTracks.FirstOrDefault(value => value.TrackId == wholeGroupTargetTrackId)
                : project.ArrangementTracks.FirstOrDefault(value => value.TrackId != sourceTrackId
                    && (value.Kind != sourceReference.Kind
                        || ResolveDescriptorSharedGroup(workspace, value.TrackId) != groupId));
            if (targetReference is not ArrangementTrackReference targetValue || targetValue == default) return;
            StartDetachedWorkspaceEdit(ProjectDomainEditCommands.MoveArrangementSharedGroup(
                    groupId,
                    sourceReference.Kind,
                    targetValue.TrackId,
                    target.Kind == ArrangementLaneKind.Conductor ? false : e.InsertsAfterTarget));
            return;
        }

        if (e.JoinsTargetGroup && target.ObjectId is MidoraId groupTargetTrackId)
        {
            if (source.Kind == ArrangementLaneKind.LogicalTrack
                && source.ParentId != target.ParentId)
            {
                LogicalTrack movingTrack = project.Tracks.Single(value => value.Id == sourceTrackId);
                EventInstrument targetInstrument = project.EventInstruments.Single(
                    value => value.Id == target.ParentId);
                if (MessageDialog.Show(
                        this,
                        $"Move Logical Track '{TimelineWorkspaceViewModel.TrackDisplayName(project, movingTrack)}' into the shared state group using Event Instrument '{targetInstrument.Name}' and change its binding?",
                        "Change Event Instrument",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            StartDetachedWorkspaceEdit(ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(
                    sourceTrackId,
                    groupTargetTrackId), () => _logicalTrackShortcutTrackId =
                        source.Kind == ArrangementLaneKind.LogicalTrack ? sourceTrackId : null);
            return;
        }

        int sourceIndex = project.ArrangementTracks.IndexOf(sourceReference);
        int finalIndex;
        if (target.Kind == ArrangementLaneKind.Conductor || target.ObjectId is not MidoraId targetTrackId)
        {
            finalIndex = 0;
        }
        else
        {
            int targetIndex = project.ArrangementTracks.FindIndex(value => value.TrackId == targetTrackId);
            if (targetIndex < 0) return;
            int targetAfterRemoval = targetIndex - (sourceIndex < targetIndex ? 1 : 0);
            finalIndex = targetAfterRemoval + (e.InsertsAfterTarget ? 1 : 0);
            finalIndex = Math.Clamp(finalIndex, 0, project.ArrangementTracks.Count - 1);
        }

        bool remainsInSameSharedGroup = !e.DetachesFromSourceGroup
            && source.IsSharedGroup
            && target.SharedGroupId == source.SharedGroupId;
        IProjectEditCommand command = remainsInSameSharedGroup
            || (!source.IsSharedGroup && !e.DetachesFromSourceGroup)
            ? ProjectDomainEditCommands.MoveArrangementTrack(sourceTrackId, finalIndex)
            : ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(sourceTrackId, finalIndex);
        StartDetachedWorkspaceEdit(command, () => _logicalTrackShortcutTrackId =
            source.Kind == ArrangementLaneKind.LogicalTrack ? sourceTrackId : null);
    }

    private static MidoraId? ResolveDescriptorSharedGroup(
        TimelineWorkspaceViewModel workspace,
        MidoraId trackId) => workspace.Snapshot?.ArrangementLanes
        .FirstOrDefault(value => value.ObjectId == trackId)
        .SharedGroupId;

    private bool TryGetTrackHeaderContext(out LogicalTrack track, out int index)
    {
        track = null!;
        index = -1;
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor lane)
            || lane.Kind != ArrangementLaneKind.LogicalTrack
            || lane.ObjectId is not MidoraId trackId)
        {
            return false;
        }
        track = project.Tracks.SingleOrDefault(value => value.Id == trackId)!;
        index = project.ArrangementTracks.FindIndex(value =>
            value.Kind == ArrangementTrackKind.LogicalTrack
            && value.TrackId == trackId);
        return track is not null && index >= 0;
    }

    private bool TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
    {
        descriptor = default;
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement } workspace)
        {
            return false;
        }
        int? lane = _trackHeaderContextLane;
        if (lane is not int laneIndex || workspace.GetArrangementLane(laneIndex) is not { } value)
            return false;
        descriptor = value;
        return true;
    }

    private int ArrangementHeaderSegmentCount(ArrangementLaneDescriptor descriptor)
    {
        if (_session.Project is not MidoraProject project || descriptor.ObjectId is not MidoraId id) return 0;
        return descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.FirstOrDefault(value => value.Id == id)?.Segments.Count ?? 0,
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.FirstOrDefault(value => value.Id == id)?.Segments.Count ?? 0,
            _ => 0
        };
    }

    private (bool Up, bool Down) ArrangementHeaderMoveAvailability(ArrangementLaneDescriptor descriptor)
    {
        if (_session.Project is not MidoraProject project || descriptor.ObjectId is not MidoraId id)
            return (false, false);
        ArrangementTrackKind kind = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ArrangementTrackKind.LogicalTrack,
            ArrangementLaneKind.PureMidiTrack => ArrangementTrackKind.PureMidiTrack,
            _ => (ArrangementTrackKind)(-1)
        };
        int index = Enum.IsDefined(kind)
            ? project.ArrangementTracks.FindIndex(value => value.Kind == kind && value.TrackId == id)
            : -1;
        return (index > 0, index >= 0 && index < project.ArrangementTracks.Count - 1);
    }

    private (bool Up, bool Down) ArrangementSharedGroupMoveAvailability(MidoraId groupId)
    {
        if (_session.Project is not MidoraProject project) return (false, false);
        int first = project.ArrangementTracks.FindIndex(value =>
            ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        int last = project.ArrangementTracks.FindLastIndex(value =>
            ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        return (first > 0, last >= 0 && last < project.ArrangementTracks.Count - 1);
    }

    private static bool ArrangementTrackBelongsToSharedGroup(
        MidoraProject project,
        ArrangementTrackReference reference,
        MidoraId groupId) => reference.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => project.Tracks.Single(
                value => value.Id == reference.TrackId).EventInstrumentUsageId == groupId,
            ArrangementTrackKind.PureMidiTrack => project.PureMidiTracks.Single(
                value => value.Id == reference.TrackId).MidiChannelRootId == groupId,
            _ => false
        };

    private bool CanPasteArrangementHeader(ArrangementLaneDescriptor descriptor)
    {
        if (_session.Document is not ProjectDocumentSession document
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || !ReferenceEquals(document, _clipboardDocument))
        {
            return false;
        }
        return payload.Kind switch
        {
            ProjectObjectClipboardKind.LogicalTrack => descriptor.Kind == ArrangementLaneKind.LogicalTrack,
            ProjectObjectClipboardKind.PureMidiTrack => descriptor.Kind == ArrangementLaneKind.PureMidiTrack,
            _ => false
        };
    }

    private void OnArrangementHeaderOpenClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)) return;
        if (descriptor.Kind == ArrangementLaneKind.Conductor)
            _session.OpenWorkspace(new ProjectTreeNode(ProjectTreeNodeKind.Conductor, "Conductor Track"));
    }

    private void OnArrangementHeaderEditEventInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.Kind != ArrangementLaneKind.LogicalTrack
            || descriptor.ParentId is not MidoraId instrumentId
            || !project.EventInstruments.Any(value => value.Id == instrumentId))
        {
            return;
        }
        RunAfterMenuClosed(sender, () =>
        {
            if (_session.Project?.EventInstruments.Any(value => value.Id == instrumentId) == true)
            {
                _session.OpenInstrument(instrumentId);
            }
        });
    }

    private void OnArrangementHeaderRenameClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id)
        {
            return;
        }
        string current = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.Single(value => value.Id == id).Name,
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.Single(value => value.Id == id).Name,
            _ => string.Empty
        };
        TextInputDialog dialog = new("Rename Arrangement Object", "Enter the new name.", current) { Owner = this };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous("Rename Arrangement Object", () =>
        {
            IProjectEditCommand command = descriptor.Kind switch
            {
                ArrangementLaneKind.LogicalTrack => ProjectDomainEditCommands.RenameLogicalTrack(id, dialog.Value),
                ArrangementLaneKind.PureMidiTrack => ProjectDomainEditCommands.RenamePureMidiTrack(id, dialog.Value),
                _ => throw new InvalidOperationException("The selected Arrangement row cannot be renamed.")
            };
            _session.Execute(command);
        });
    }

    private void OnArrangementTrackPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId trackId)
        {
            return;
        }
        _ = OpenArrangementTrackProperties(descriptor.Kind, trackId);
    }

    private bool OpenArrangementTrackProperties(
        ArrangementLaneKind kind,
        MidoraId trackId)
    {
        if (!_session.CanEditProject || _session.Project is not MidoraProject project)
        {
            return false;
        }

        string? Submit(string name, MidoraColor? color) => TrySubmitDialogEdit(() =>
        {
            IProjectEditCommand command = kind switch
            {
                ArrangementLaneKind.LogicalTrack =>
                    ProjectDomainEditCommands.UpdateLogicalTrackProperties(
                        trackId,
                        name,
                        color),
                ArrangementLaneKind.PureMidiTrack =>
                    ProjectDomainEditCommands.UpdatePureMidiTrackProperties(
                        trackId,
                        name,
                        color),
                _ => throw new InvalidOperationException(
                    "The selected Arrangement row has no Track properties.")
            };
            _session.Execute(command);
        });

        TrackPropertiesDialog dialog;
        if (kind == ArrangementLaneKind.LogicalTrack)
        {
            LogicalTrack? track = project.Tracks.FirstOrDefault(value => value.Id == trackId);
            if (track is null) return false;
            dialog = new(
                "Logical Track Properties",
                track.Name,
                track.ColorOverride,
                ProjectTrackColorPolicy.ResolveDisplayColor(project, track),
                supportsInheritedColor: true,
                submit: Submit)
            {
                Owner = this
            };
        }
        else if (kind == ArrangementLaneKind.PureMidiTrack)
        {
            PureMidiTrack? track = project.PureMidiTracks.FirstOrDefault(value => value.Id == trackId);
            if (track is null) return false;
            dialog = new(
                "MIDI Track Properties",
                track.Name,
                track.Color,
                ProjectTrackColorPolicy.ResolveDisplayColor(track),
                supportsInheritedColor: false,
                submit: Submit)
            {
                Owner = this
            };
        }
        else
        {
            return false;
        }

        _ = ShowModalDialog(dialog);
        return true;
    }

    private void OnArrangementTrackRouteSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not
            {
                Kind: ArrangementLaneKind.PureMidiTrack,
                ObjectId: MidoraId trackId,
                ParentId: MidoraId rootId
            })
        {
            return;
        }
        ShowMidiRouteSettings(trackId, rootId);
    }

    private void ShowMidiRouteSettings(MidoraId trackId, MidoraId rootId)
    {
        if (_session.Project is not MidoraProject project
            || project.PureMidiTracks.All(value => value.Id != trackId))
        {
            return;
        }
        MidiChannelRoot root = project.MidiChannelRoots.Single(value => value.Id == rootId);
        MidiChannelRootSettingsDialog dialog = new(root) { Owner = this };
        if (ShowModalDialog(dialog) != true) return;
        int memberCount = project.PureMidiTracks.Count(
            value => value.MidiChannelRootId == root.Id);
        bool changesSharedFixedChannelMode = memberCount > 1
            && root.RoutingMode == MidiChannelRootRoutingMode.Fixed
            && dialog.RoutingMode == MidiChannelRootRoutingMode.Fixed
            && dialog.OneBasedPort == root.FixedZeroBasedPort + 1
            && dialog.OneBasedChannel == root.FixedZeroBasedChannel + 1
            && dialog.ChannelMode != root.ChannelMode;
        if (changesSharedFixedChannelMode
            && MessageDialog.Show(
                this,
                $"This Fixed MIDI channel is used by {memberCount} Tracks. "
                    + $"Changing its Channel Mode to {dialog.ChannelMode} will affect all of them. Continue?",
                "Change Shared MIDI Channel Mode",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        RunSynchronous("Configure MIDI Route", () => _session.Execute(
            ProjectDomainEditCommands.ConfigurePureMidiTrackRoute(
                trackId, dialog.RoutingMode,
                dialog.OneBasedPort, dialog.OneBasedChannel, dialog.ChannelMode)));
    }

    private void OnArrangementSharedRootSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _arrangementSharedGroupContextId is not MidoraId rootId
            || project.MidiChannelRoots.SingleOrDefault(value => value.Id == rootId)
                is not MidiChannelRoot root)
        {
            return;
        }
        int members = project.PureMidiTracks.Count(value => value.MidiChannelRootId == root.Id);
        if (members > 1
            && MessageDialog.Show(
                this,
                $"This route is shared by {members} MIDI Tracks. Apply the settings to all members?",
                "Shared MIDI Route",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        MidiChannelRootSettingsDialog dialog = new(root) { Owner = this };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous("Configure Shared MIDI Route", () => _session.Execute(
            ProjectDomainEditCommands.ConfigureMidiChannelRoot(
                root.Id,
                root.Name,
                dialog.RoutingMode,
                dialog.OneBasedPort,
                dialog.OneBasedChannel,
                dialog.ChannelMode)));
    }

    private void OnArrangementSharedGroupMuteClick(object sender, RoutedEventArgs e)
    {
        if (_arrangementSharedGroupContextId is not MidoraId groupId) return;
        RunSynchronous("Toggle Shared Group Mute", () =>
            _session.SetSharedGroupMuted(groupId, !_session.IsSharedGroupMuted(groupId)));
    }

    private void OnArrangementSharedGroupSoloClick(object sender, RoutedEventArgs e)
    {
        if (_arrangementSharedGroupContextId is not MidoraId groupId) return;
        RunSynchronous("Toggle Shared Group Solo", () =>
            _session.SetSharedGroupSolo(groupId, !_session.IsSharedGroupSolo(groupId)));
    }

    private void OnArrangementSharedGroupInstrumentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _arrangementSharedGroupContextId is not MidoraId usageId
            || project.EventInstrumentUsages.SingleOrDefault(value => value.Id == usageId)
                is not EventInstrumentUsage usage)
        {
            return;
        }
        SelectionDialog dialog = new(
            "Change Shared Event Instrument",
            "Select the Event Instrument Definition used by every Track in this shared state group.",
            project.EventInstruments.Select(instrument => new SelectionDialogItem(
                instrument.Id,
                string.IsNullOrWhiteSpace(instrument.Name)
                    ? "Unnamed Event Instrument"
                    : instrument.Name,
                instrument.Id == usage.EventInstrumentId
                    ? "Current definition"
                    : $"{instrument.SubVoices.Count} SubVoice(s)")))
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true
            || dialog.SelectedValue is not MidoraId eventInstrumentId
            || eventInstrumentId == usage.EventInstrumentId)
        {
            return;
        }
        StartDetachedWorkspaceEdit(ProjectDomainEditCommands.RebindEventInstrumentUsage(usage.Id, eventInstrumentId));
    }

    private void OnArrangementSharedGroupMoveUpClick(object sender, RoutedEventArgs e) =>
        MoveArrangementSharedGroup(-1);

    private void OnArrangementSharedGroupMoveDownClick(object sender, RoutedEventArgs e) =>
        MoveArrangementSharedGroup(1);

    private void MoveArrangementSharedGroup(int direction)
    {
        if (_session.Project is not MidoraProject project
            || _arrangementSharedGroupContextId is not MidoraId groupId
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor))
        {
            return;
        }
        ArrangementTrackKind kind = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ArrangementTrackKind.LogicalTrack,
            ArrangementLaneKind.PureMidiTrack => ArrangementTrackKind.PureMidiTrack,
            _ => throw new InvalidOperationException("The selected row has no movable shared group.")
        };
        int first = project.ArrangementTracks.FindIndex(value =>
            value.Kind == kind && ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        int last = project.ArrangementTracks.FindLastIndex(value =>
            value.Kind == kind && ArrangementTrackBelongsToSharedGroup(project, value, groupId));
        int targetIndex = direction < 0 ? first - 1 : last + 1;
        if (first < 0 || (uint)targetIndex >= (uint)project.ArrangementTracks.Count) return;
        ArrangementTrackReference target = project.ArrangementTracks[targetIndex];
        StartDetachedWorkspaceEdit(ProjectDomainEditCommands.MoveArrangementSharedGroup(
                groupId,
                kind,
                target.TrackId,
                insertAfter: direction > 0));
    }

    private void OnArrangementSharedGroupMakeIndependentClick(object sender, RoutedEventArgs e)
    {
        if (_arrangementSharedGroupContextId is not MidoraId groupId
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor))
        {
            return;
        }
        ArrangementTrackKind kind = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ArrangementTrackKind.LogicalTrack,
            ArrangementLaneKind.PureMidiTrack => ArrangementTrackKind.PureMidiTrack,
            _ => throw new InvalidOperationException("The selected row has no shared Track group.")
        };
        StartDetachedWorkspaceEdit(ProjectDomainEditCommands.MakeArrangementSharedGroupIndependent(groupId, kind));
    }

    private void OnLogicalTrackShareStateClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        LogicalTrack source = project.Tracks.Single(value => value.Id == trackId);
        SelectionDialog dialog = new(
            "Share Instrument State",
            "Select the Logical Track whose Event Instrument state should be shared.",
            project.LogicalTracksInArrangementOrder()
                .Where(value => value.Id != source.Id
                    && value.EventInstrumentUsageId.HasValue)
                .Select(value => new SelectionDialogItem(
                    value.Id,
                    TimelineWorkspaceViewModel.TrackDisplayName(project, value),
                    TimelineWorkspaceViewModel.BoundInstrumentDisplayName(project, value))))
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.SelectedValue is not MidoraId targetTrackId)
            return;
        LogicalTrack target = project.Tracks.Single(value => value.Id == targetTrackId);
        if (project.ResolveEventInstrumentDefinitionId(source)
            != project.ResolveEventInstrumentDefinitionId(target)
            && MessageDialog.Show(
                this,
                "The selected Track uses a different Event Instrument. Change the binding and join its shared state group?",
                "Change Event Instrument",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        StartDetachedWorkspaceEdit(ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(
                source.Id,
                target.Id));
    }

    private void OnLogicalTrackMakeIndependentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        int index = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
        StartDetachedWorkspaceEdit(
            ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(trackId, index));
    }

    private void OnMidiTrackShareChannelClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        PureMidiTrack source = project.PureMidiTracks.Single(value => value.Id == trackId);
        SelectionDialog dialog = new(
            "Share MIDI Channel",
            "Select a MIDI Track whose Channel state and route should be shared.",
            project.PureMidiTracksInArrangementOrder()
                .Where(value => value.Id != source.Id)
                .Select(value =>
                {
                    MidiChannelRoot root = project.MidiChannelRoots.Single(
                        root => root.Id == value.MidiChannelRootId);
                    string route = root.RoutingMode == MidiChannelRootRoutingMode.Auto
                        ? $"Auto · {root.ChannelMode}"
                        : $"P{root.FixedZeroBasedPort + 1} Ch{root.FixedZeroBasedChannel + 1} · {root.ChannelMode}";
                    return new SelectionDialogItem(value.Id, value.Name, route);
                }))
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.SelectedValue is not MidoraId targetTrackId)
            return;
        PureMidiTrack target = project.PureMidiTracks.Single(value => value.Id == targetTrackId);
        MidiChannelRoot targetRoot = project.MidiChannelRoots.Single(
            value => value.Id == target.MidiChannelRootId);
        IProjectEditCommand command = targetRoot.RoutingMode == MidiChannelRootRoutingMode.Auto
            ? ProjectDomainEditCommands.MoveArrangementTrackIntoSharedGroup(source.Id, target.Id)
            : ProjectDomainEditCommands.MovePureMidiTrack(
                source.Id,
                targetRoot.Id,
                project.ArrangementTracks.FindIndex(value => value.TrackId == source.Id));
        StartDetachedWorkspaceEdit(command);
    }

    private void OnMidiTrackMakeIndependentClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor is not { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId trackId })
        {
            return;
        }
        int index = project.ArrangementTracks.FindIndex(value => value.TrackId == trackId);
        StartDetachedWorkspaceEdit(
            ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(trackId, index));
    }

    private void OnArrangementHeaderCutClick(object sender, RoutedEventArgs e) => CopyArrangementHeader(cut: true);
    private void OnArrangementHeaderCopyClick(object sender, RoutedEventArgs e) => CopyArrangementHeader(cut: false);

    private void CopyArrangementHeader(bool cut)
    {
        if (_session.Document is not ProjectDocumentSession document
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id || cut && !_session.CanEditProject) return;
        if (descriptor.Kind == ArrangementLaneKind.LogicalTrack)
            StartClipboardTransfer(document, () => ProjectObjectClipboard.CopyLogicalTrack(document, id),
                cut ? ProjectDomainEditCommands.DeleteLogicalTrack(id, true) : null);
        else if (descriptor.Kind == ArrangementLaneKind.PureMidiTrack)
            StartClipboardTransfer(document, () => ProjectObjectClipboard.CopyPureMidiTrack(document, id),
                cut ? ProjectDomainEditCommands.DeletePureMidiTrack(id, true) : null);
    }

    private void OnArrangementHeaderPasteClick(object sender, RoutedEventArgs e)
    {
        if (!_session.CanEditProject
            || _session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || !ReferenceEquals(document, _clipboardDocument)
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor))
        {
            return;
        }
        try
        {
            IProjectEditCommand command = payload.Kind switch
            {
                ProjectObjectClipboardKind.LogicalTrack => CreateLogicalTrackHeaderPasteCommand(document, payload, project, descriptor),
                ProjectObjectClipboardKind.PureMidiTrack => CreatePureMidiTrackHeaderPasteCommand(document, payload, project, descriptor),
                _ => throw new InvalidOperationException("The clipboard object cannot be pasted at this Arrangement row.")
            };
            StartWorkspaceEdit(command, () => _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}."));
        }
        catch (Exception exception) { _session.SetStatusMessage($"Paste Arrangement Object: {exception.Message}", isError: true); }
    }

    private static IProjectEditCommand CreateLogicalTrackHeaderPasteCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraProject project,
        ArrangementLaneDescriptor descriptor)
    {
        LogicalTrack target = project.Tracks.Single(
            value => value.Id == descriptor.ObjectId!.Value);
        int index = ArrangementInsertionAfterTargetGroup(project, target.Id);
        return target.EventInstrumentUsageId is MidoraId usageId
            ? ProjectObjectClipboard.CreatePasteLogicalTrackIntoUsageCommand(
                document,
                payload,
                usageId,
                index)
            : ProjectObjectClipboard.CreatePasteLogicalTrackIndependentCommand(
                document,
                payload,
                index);
    }

    private static IProjectEditCommand CreatePureMidiTrackHeaderPasteCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        MidoraProject project,
        ArrangementLaneDescriptor descriptor)
    {
        PureMidiTrack target = project.PureMidiTracks.Single(
            value => value.Id == descriptor.ObjectId!.Value);
        MidiChannelRoot root = project.MidiChannelRoots.Single(
            value => value.Id == target.MidiChannelRootId);
        int index = root.RoutingMode == MidiChannelRootRoutingMode.Auto
            ? ArrangementInsertionAfterTargetGroup(project, target.Id)
            : project.ArrangementTracks.FindIndex(value => value.TrackId == target.Id) + 1;
        return ProjectObjectClipboard.CreatePastePureMidiTrackCommand(
            document,
            payload,
            root.Id,
            index);
    }

    private static int ArrangementInsertionAfterTargetGroup(
        MidoraProject project,
        MidoraId targetTrackId)
    {
        ArrangementTrackReference target = project.ArrangementTracks.Single(
            value => value.TrackId == targetTrackId);
        MidoraId? groupId = target.Kind switch
        {
            ArrangementTrackKind.LogicalTrack => project.Tracks.Single(
                value => value.Id == targetTrackId).EventInstrumentUsageId,
            ArrangementTrackKind.PureMidiTrack => project.PureMidiTracks.Single(
                value => value.Id == targetTrackId).MidiChannelRootId,
            _ => null
        };
        if (groupId is null)
        {
            return project.ArrangementTracks.IndexOf(target) + 1;
        }
        int last = project.ArrangementTracks.FindLastIndex(reference =>
            reference.Kind == target.Kind
            && (reference.Kind == ArrangementTrackKind.LogicalTrack
                ? project.Tracks.Single(track => track.Id == reference.TrackId)
                    .EventInstrumentUsageId == groupId
                : project.PureMidiTracks.Single(track => track.Id == reference.TrackId)
                    .MidiChannelRootId == groupId));
        return last + 1;
    }

    private void OnArrangementHeaderDuplicateClick(object sender, RoutedEventArgs e) =>
        DuplicateArrangementHeader(shareInstrumentState: false);

    private void OnArrangementHeaderDuplicateAndShareStateClick(object sender, RoutedEventArgs e) =>
        DuplicateArrangementHeader(shareInstrumentState: true);

    private void DuplicateArrangementHeader(bool shareInstrumentState)
    {
        if (!TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id) return;
        try
        {
            IProjectEditCommand command = descriptor.Kind switch
            {
                ArrangementLaneKind.LogicalTrack when shareInstrumentState =>
                    ProjectDomainEditCommands.DuplicateLogicalTrackAndShareState(id),
                ArrangementLaneKind.LogicalTrack =>
                    ProjectDomainEditCommands.DuplicateLogicalTrack(id),
                ArrangementLaneKind.PureMidiTrack when !shareInstrumentState =>
                    ProjectDomainEditCommands.DuplicatePureMidiTrack(id),
                _ => throw new InvalidOperationException(
                    "The selected Arrangement row cannot be duplicated with the requested state sharing.")
            };
            StartWorkspaceEdit(command);
        }
        catch (Exception exception) { _session.SetStatusMessage($"Duplicate Arrangement Object: {exception.Message}", isError: true); }
    }

    private void OnArrangementHeaderMoveUpClick(object sender, RoutedEventArgs e) => MoveArrangementHeader(-1);
    private void OnArrangementHeaderMoveDownClick(object sender, RoutedEventArgs e) => MoveArrangementHeader(1);

    private void MoveArrangementHeader(int direction)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id) return;
        int index = project.ArrangementTracks.FindIndex(value => value.TrackId == id);
        int targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= project.ArrangementTracks.Count) return;
        ArrangementTrackReference target = project.ArrangementTracks[targetIndex];
        MidoraId? sourceGroup = descriptor.SharedGroupId;
        MidoraId? targetGroup = _session.ActiveWorkspace is TimelineWorkspaceViewModel workspace
            ? ResolveDescriptorSharedGroup(workspace, target.TrackId)
            : null;
        IProjectEditCommand command = descriptor.IsSharedGroup && sourceGroup != targetGroup
            ? ProjectDomainEditCommands.MoveArrangementTrackOutsideSharedGroup(id, targetIndex)
            : ProjectDomainEditCommands.MoveArrangementTrack(id, targetIndex);
        StartDetachedWorkspaceEdit(command);
    }

    private void OnArrangementHeaderDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId id) return;
        string message = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => $"Delete Logical Track '{project.Tracks.Single(value => value.Id == id).Name}' and all of its Segments?",
            ArrangementLaneKind.PureMidiTrack => $"Delete MIDI Track '{project.PureMidiTracks.Single(value => value.Id == id).Name}' and all of its Segments?",
            ArrangementLaneKind.DamagedEventInstrument => "Delete this damaged Event Instrument placeholder and its retained Logical Track subtree?",
            ArrangementLaneKind.DamagedMidiChannelRoot => "Delete this damaged MIDI Channel Root placeholder and its retained MIDI Track subtree?",
            ArrangementLaneKind.DamagedLogicalTrack => "Delete this damaged Logical Track placeholder?",
            ArrangementLaneKind.DamagedPureMidiTrack => "Delete this damaged Pure MIDI Track placeholder?",
            _ => string.Empty
        };
        if (message.Length == 0 || MessageDialog.Show(this, message, "Delete Arrangement Object",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        StartWorkspaceEdit(descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => ProjectDomainEditCommands.DeleteLogicalTrack(id, true),
            ArrangementLaneKind.PureMidiTrack => ProjectDomainEditCommands.DeletePureMidiTrack(id, true),
            ArrangementLaneKind.DamagedEventInstrument => ProjectDomainEditCommands.DeleteDamagedEventInstrument(id),
            ArrangementLaneKind.DamagedMidiChannelRoot => ProjectDomainEditCommands.DeleteDamagedMidiChannelRoot(id),
            ArrangementLaneKind.DamagedLogicalTrack => ProjectDomainEditCommands.DeleteDamagedLogicalTrack(id),
            ArrangementLaneKind.DamagedPureMidiTrack => ProjectDomainEditCommands.DeleteDamagedPureMidiTrack(id),
            _ => throw new InvalidOperationException("The selected Arrangement row cannot be deleted.")
        });
    }

    private void OnArrangementHeaderNewMidiTrackClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)) return;
        if (descriptor is not
            {
                Kind: ArrangementLaneKind.PureMidiTrack,
                ObjectId: MidoraId trackId,
                ParentId: MidoraId rootId
            }) return;
        MidiChannelRoot root = project.MidiChannelRoots.Single(value => value.Id == rootId);
        int index = root.RoutingMode == MidiChannelRootRoutingMode.Auto
            ? ArrangementInsertionAfterTargetGroup(project, trackId)
            : project.ArrangementTracks.FindIndex(value => value.TrackId == trackId) + 1;
        RunSynchronous("Create MIDI Track", () => _session.Execute(
            ProjectDomainEditCommands.CreatePureMidiTrack(rootId, insertionIndex: index)));
    }

    private void OnTrackHeaderRenameClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        TextInputDialog dialog = new(
            "Rename Logical Track",
            "Enter the Logical Track name.",
            track.Name)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous("Rename Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.RenameLogicalTrack(track.Id, dialog.Value)));
    }

    private void OnTrackHeaderBindClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        SelectionDialog dialog = new(
            "Bind Logical Track",
            "Select the Event Instrument used by this Logical Track.",
            project.EventInstruments.Select(instrument => new SelectionDialogItem(
                instrument.Id,
                string.IsNullOrWhiteSpace(instrument.Name) ? "Unnamed Event Instrument" : instrument.Name,
                $"{instrument.SubVoices.Count} SubVoice(s)")))
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) == true && dialog.SelectedValue is MidoraId instrumentId)
        {
            BindTrackToInstrument(track, instrumentId);
        }
    }

    private void OnTrackHeaderUnbindClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        RunSynchronous("Unbind Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.BindLogicalTrack(track.Id, null)));
    }

    private void OnTrackHeaderCutClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        CutOrCopyLogicalTrack(track, cut: true);
    }

    private void OnTrackHeaderCopyClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        CutOrCopyLogicalTrack(track, cut: false);
    }

    private void OnTrackHeaderPasteClick(object sender, RoutedEventArgs e) =>
        PasteLogicalTrackClipboard(ResolveLogicalTrackPasteIndex(preferTrackHeaderContext: true));

    private void OnTrackHeaderDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        StartWorkspaceEdit(
            ProjectDomainEditCommands.DuplicateLogicalTrack(track.Id));
    }

    private void CutOrCopyLogicalTrack(LogicalTrack track, bool cut)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_session.Document is not ProjectDocumentSession document || cut && !_session.CanEditProject) return;
        MidoraId id = track.Id;
        StartClipboardTransfer(document, () => ProjectObjectClipboard.CopyLogicalTrack(document, id),
            cut ? ProjectDomainEditCommands.DeleteLogicalTrack(id, true) : null);
    }

    private void OnTrackHeaderSelectSegmentsClick(object sender, RoutedEventArgs e) =>
        SelectTrackHeaderSegments(WorkspaceSelectionRangeMode.Replace);

    private void OnTrackHeaderAddSegmentsToSelectionClick(object sender, RoutedEventArgs e) =>
        SelectTrackHeaderSegments(WorkspaceSelectionRangeMode.Add);

    private void SelectTrackHeaderSegments(WorkspaceSelectionRangeMode mode)
    {
        if (_session.Project is not MidoraProject project
            || !TryGetArrangementHeaderContext(out ArrangementLaneDescriptor descriptor)
            || descriptor.ObjectId is not MidoraId trackId
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace)
        {
            return;
        }
        IEnumerable<MidoraId> segmentIds = descriptor.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.Single(value => value.Id == trackId)
                .Segments.Select(segment => segment.Id),
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.Single(value => value.Id == trackId)
                .Segments.Select(segment => segment.Id),
            _ => []
        };
        workspace.Selection.ApplyRange(
            segmentIds,
            mode,
            new WorkspaceTimelineSelectionSource(
                WorkspaceTimelineSelectionKind.ArrangementSegment));
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnTrackHeaderMoveUpClick(object sender, RoutedEventArgs e) => MoveTrackHeader(-1);
    private void OnTrackHeaderMoveDownClick(object sender, RoutedEventArgs e) => MoveTrackHeader(1);

    private void MoveTrackHeader(int direction)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out int index)
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        int target = index + direction;
        if ((uint)target >= (uint)project.Tracks.Count) return;
        RunSynchronous("Reorder Logical Track", () =>
            _session.Execute(ProjectDomainEditCommands.ReorderLogicalTrack(track.Id, target)));
    }

    private void OnTrackHeaderDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetTrackHeaderContext(out LogicalTrack track, out _)) return;
        if (MessageDialog.Show(
                this,
                $"Delete Logical Track '{TimelineWorkspaceViewModel.TrackDisplayName(_session.Project!, track)}' and its {track.Segments.Count} Segment(s)?",
                "Delete Logical Track",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        StartWorkspaceEdit(ProjectDomainEditCommands.DeleteLogicalTrack(track.Id, nonEmptyDeletionConfirmed: true));
    }

    private void OnTimelineLanePreviewPressed(object? sender, TimelineLanePreviewEventArgs e)
    {
        RunSynchronous(
            "Start Pitch Audition",
            () => _session.BeginPitchAudition(e.Pitch, e.Velocity));
    }

    private void OnActiveEditorLaneDropDownClosed(object sender, EventArgs e)
    {
        ComboBox? comboBox = sender as ComboBox;
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel selectedTimeline
            && selectedTimeline.GetActiveParameterLaneOption()?.IsDirectMidiLane != true)
        {
            selectedTimeline.PreferCurrentParameterLaneOnNextRebuild();
        }
        else if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel selectedInstrument)
        {
            selectedInstrument.PreferCurrentRenderLaneOnNextRebuild();
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel directTimeline
            && directTimeline.GetActiveParameterLaneOption()?.IsDirectMidiLane == true)
        {
            _session.RefreshWorkspace(directTimeline);
            RestoreEditorLaneTimelineFocus(comboBox, directTimeline);
            return;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timeline
            && timeline.GetActiveParameterLaneOption() is ParameterLaneOption option
            && ShouldCreateLogicalParameterLane(option))
        {
            RunSynchronous("Create Logical Parameter Lane", () => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateLogicalParameterLane(segmentId, option.ParameterId),
                timeline));
            RestoreEditorLaneTimelineFocus(comboBox, timeline);
            return;
        }
        if (_session.ActiveWorkspace is WorkspaceViewModel workspace)
        {
            _session.RefreshWorkspace(workspace);
            RestoreEditorLaneTimelineFocus(comboBox, workspace);
        }
    }

    internal static bool ShouldCreateLogicalParameterLane(ParameterLaneOption option) =>
        !option.IsDirectMidiLane
        && option.LaneId is null
        && !option.IsBroken;

    private void RestoreEditorLaneTimelineFocus(
        ComboBox? comboBox,
        WorkspaceViewModel workspace)
    {
        object? timelineTag = workspace switch
        {
            InstrumentWorkspaceViewModel => "SubVoiceEvents",
            TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } => "ParameterLanes",
            _ => null
        };
        if (comboBox is null || timelineTag is null) return;

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!IsActive
                    || !ReferenceEquals(_session.ActiveWorkspace, workspace)
                    || !comboBox.IsKeyboardFocusWithin)
                {
                    return;
                }

                TimelineSurface? timeline = FindWorkspaceElement<TimelineSurface>(timelineTag);
                if (timeline is { IsVisible: true, IsEnabled: true, Focusable: true })
                {
                    timeline.Focus();
                }
            }));
    }

    private async void OnTimelineVelocityEditCompleted(object? sender, TimelineVelocityEditEventArgs e)
    {
        if (e.Velocities.Count == 0) return;
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timeline)
        {
            await ExecuteStagedProjectOperationAsync("Paint Note Velocities",
                _session.Project is not null
                && TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is not null
                    ? ProjectDomainEditCommands.PaintDirectMidiNoteVelocities(segmentId, e.Velocities)
                    : ProjectDomainEditCommands.PaintLogicalNoteVelocities(segmentId, e.Velocities),
                timeline, sender as TimelineSurface);
            return;
        }
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } instrument)
        {
            await ExecuteStagedProjectOperationAsync("Paint Template Note Velocities",
                ProjectDomainEditCommands.PaintTemplateNoteVelocities(
                    instrumentId,
                    subVoiceId,
                    e.Velocities), instrument, sender as TimelineSurface);
        }
    }

    private void OnTimelineLanePreviewReleased(object? sender, TimelineLanePreviewEventArgs e) =>
        RunSynchronous("End Pitch Audition", _session.EndPitchAudition);

    private void OnTimelinePitchPreviewRequested(object? sender, TimelinePitchPreviewEventArgs e) =>
        RunSynchronous(
            "Update Pitch Audition",
            () => _session.BeginPitchAudition(e.Pitch, e.Velocity));

    private void OnTimelinePitchPreviewReleased(object? sender, EventArgs e) =>
        RunSynchronous("End Pitch Audition", _session.EndPitchAudition);

    private void OnTimelineNotePlacementStarted(object? sender, TimelineNotePlacementEventArgs e)
    {
        if (sender is TimelineSurface { Tag: "SubVoiceNotes", DataContext: InstrumentWorkspaceViewModel instrumentWorkspace }
            && instrumentWorkspace.ObjectId is MidoraId instrumentId
            && instrumentWorkspace.ActiveSubVoiceId is MidoraId subVoiceId)
        {
            long templateStart = instrumentWorkspace.EditorSettings.SnapAbsolute(e.StartTick);
            _templateNotePlacement = (instrumentId, subVoiceId, templateStart, e.Pitch, e.Velocity);
            RunSynchronous(
                "Start Note Placement Audition",
                () => _session.BeginPitchAudition(e.Pitch, e.Velocity));
            return;
        }
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } workspace)
        {
            return;
        }
        if (TimelineWorkspaceViewModel.FindSegment(project, segmentId) is null
            && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is null) return;
        long start = workspace.EditorSettings.SnapAbsolute(e.StartTick);
        _notePlacement = (segmentId, start, e.Pitch, e.Velocity);
        RunSynchronous(
            "Start Note Placement Audition",
            () => _session.BeginPitchAudition(e.Pitch, e.Velocity));
    }

    private void OnTimelineNotePlacementCompleted(object? sender, TimelineNotePlacementEventArgs e)
    {
        RunSynchronous("End Note Placement Audition", _session.EndPitchAudition);
        if (_templateNotePlacement is { } templatePlacement
            && _session.ActiveWorkspace is InstrumentWorkspaceViewModel instrumentWorkspace)
        {
            _templateNotePlacement = null;
            long rawTemplateLength = Math.Max(1, checked(e.EndTick - templatePlacement.StartTick));
            long templateLength = Math.Max(
                1,
                instrumentWorkspace.EditorSettings.SnapDelta(rawTemplateLength, e.EndTick));
            RunSynchronous("Place Template Note", () => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateTemplateNote(
                    templatePlacement.InstrumentId,
                    templatePlacement.SubVoiceId,
                    templatePlacement.StartTick,
                    templateLength,
                    e.Pitch,
                    templatePlacement.Velocity),
                instrumentWorkspace));
            return;
        }
        if (_notePlacement is not { } placement
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace)
        {
            return;
        }
        _notePlacement = null;
        long rawLength = Math.Max(1, checked(e.EndTick - placement.StartTick));
        long length = Math.Max(1, workspace.EditorSettings.SnapDelta(rawLength, e.EndTick));
        RunSynchronous("Place Note", () => ExecuteAndSelectCreated(
            _session.Project is not null
            && TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, placement.SegmentId) is not null
                ? ProjectDomainEditCommands.CreateDirectMidiNote(
                    placement.SegmentId,
                    placement.StartTick,
                    length,
                    e.Pitch,
                    placement.Velocity)
                : ProjectDomainEditCommands.CreateLogicalNote(
                    placement.SegmentId,
                    placement.StartTick,
                    length,
                    e.Pitch,
                    placement.Velocity),
            workspace));
    }

    private void OnTimelineNotePlacementCancelled(object? sender, EventArgs e)
    {
        RunSynchronous("End Note Placement Audition", _session.EndPitchAudition);
        _notePlacement = null;
        _templateNotePlacement = null;
    }

    private void OnTimelineSegmentPlacementCompleted(
        object? sender,
        TimelineSegmentPlacementEventArgs e)
    {
        if (_session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || workspace.GetArrangementLane(e.Lane) is not
            { CanContainSegments: true, ObjectId: MidoraId trackId } lane)
        {
            return;
        }

        long end = e.EndTick;
        long nextStart;
        bool occupied;
        if (lane.Kind == ArrangementLaneKind.LogicalTrack)
        {
            LogicalTrack track = project.Tracks.Single(value => value.Id == trackId);
            nextStart = track.Segments.Where(item => item.ProjectStartTick > e.StartTick)
                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
            occupied = track.Segments.Any(item => e.StartTick >= item.ProjectStartTick && e.StartTick < item.ProjectRange.EndTick);
        }
        else
        {
            PureMidiTrack track = project.PureMidiTracks.Single(value => value.Id == trackId);
            nextStart = track.Segments.Where(item => item.ProjectStartTick > e.StartTick)
                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
            occupied = track.Segments.Any(item => e.StartTick >= item.ProjectStartTick && e.StartTick < item.ProjectRange.EndTick);
        }
        if (occupied)
        {
            return;
        }
        if (nextStart != long.MaxValue)
        {
            end = Math.Min(end, nextStart);
        }
        if (end <= e.StartTick) return;

        RunSynchronous("Create Segment", () => ExecuteAndSelectCreated(
            lane.Kind == ArrangementLaneKind.LogicalTrack
                ? ProjectDomainEditCommands.CreateSegment(
                    trackId,
                    e.StartTick,
                    checked(end - e.StartTick))
                : ProjectDomainEditCommands.CreateMidiSegment(
                    trackId,
                    e.StartTick,
                    checked(end - e.StartTick)),
            workspace));
    }

    private void OnProjectSettingLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox textBox) CommitProjectSetting(textBox, restoreOnFailure: true);
    }

    private void OnProjectSettingKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox) return;
        if (e.Key == Key.Enter)
        {
            CommitProjectSetting(textBox, restoreOnFailure: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            textBox.ClearValue(Control.BorderBrushProperty);
            textBox.ToolTip = null;
            e.Handled = true;
        }
    }

    private void OnProjectSettingChoiceDropDownClosed(object sender, EventArgs e)
    {
        if (sender is not ComboBox { Tag: PropertyField field }
            || !field.IsEditable
            || !IsCurrentProjectSettingField(field))
        {
            return;
        }

        try
        {
            _session.ApplyProjectSettingsField(field);
        }
        catch (Exception exception)
        {
            if (_session.Workspaces.OfType<SettingsWorkspaceViewModel>().FirstOrDefault()
                is SettingsWorkspaceViewModel settings)
            {
                _session.RefreshWorkspace(settings);
            }
            _session.SetStatusMessage(exception.Message, isError: true);
        }
    }

    private void CommitProjectSetting(TextBox textBox, bool restoreOnFailure)
    {
        if (textBox.IsReadOnly
            || textBox.Tag is not PropertyField field
            || !IsCurrentProjectSettingField(field))
        {
            return;
        }
        string originalValue = field.Value;
        if (string.Equals(textBox.Text, originalValue, StringComparison.Ordinal)) return;
        BindingExpression? binding = textBox.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource();
        try
        {
            _session.ApplyProjectSettingsField(field);
            textBox.ClearValue(Control.BorderBrushProperty);
            textBox.ToolTip = null;
        }
        catch (Exception exception)
        {
            if (restoreOnFailure)
            {
                field.Value = originalValue;
                binding?.UpdateTarget();
                textBox.ClearValue(Control.BorderBrushProperty);
                textBox.ToolTip = exception.Message;
                _session.SetStatusMessage(exception.Message, isError: true);
            }
            else
            {
                textBox.BorderBrush = (Brush)FindResource("Brush.Red");
                textBox.ToolTip = exception.Message;
                textBox.Focus();
                textBox.SelectAll();
            }
        }
    }

    private bool IsCurrentProjectSettingField(PropertyField field)
    {
        if (_session.Workspaces.OfType<SettingsWorkspaceViewModel>().FirstOrDefault() is not SettingsWorkspaceViewModel settings)
        {
            return false;
        }
        return settings.GeneralFields.Contains(field)
            || settings.InitialStateFields.Contains(field)
            || settings.ResetDefaultFields.Contains(field);
    }

    private void OnTimelineSelectionReplacementStarted(
        object? sender,
        TimelineSelectionReplacementEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceViewModel workspace)
            return;
        if (workspace is TimelineWorkspaceViewModel { IsConductor: true })
            _conductorListSelectionCancellation.Cancel();
        if (_session.Project is null || !_session.Workspaces.Contains(workspace)) return;
        e.BaseSelection = _session.BeginWorkspaceSelectionReplacement(workspace);
    }

    private void OnTimelineMarqueeCompleted(object? sender, TimelineMarqueeEventArgs e)
    {
        // A large out-of-core marquee finishes asynchronously.  Resolve its
        // owning workspace from the originating surface rather than whichever
        // tab happens to be active when the background scan completes.
        if ((sender as FrameworkElement)?.DataContext is not WorkspaceViewModel workspace)
            return;
        if (_session.Project is null || !_session.Workspaces.Contains(workspace)) return;
        if (sender is TimelineSurface surface
            && GetTimelineSelectionSource(workspace, surface)
                is WorkspaceTimelineSelectionSource source)
        {
            workspace.Selection.RegisterTimelineMaterialization(
                e.Materialization.Ids,
                source,
                TimelineToolPolicy.ResolveMarqueeSelectionMode(e.Modifiers),
                e.Materialization.RangeCount,
                e.Materialization.BaseIntersectionCount);
        }
        _session.TryApplyMaterializedWorkspaceSelection(
            workspace,
            e.Materialization);
    }

    private void OnTimelineRulerClicked(object? sender, TimelineRulerEventArgs e)
    {
        if (_session.Project is not MidoraProject project)
        {
            return;
        }
        TimelineWorkspaceViewModel? timeline = (sender as FrameworkElement)?.DataContext
            as TimelineWorkspaceViewModel;
        if (e.IsEditCursor)
        {
            if (timeline is not null) timeline.EditCursorTick = e.Tick;
            else if ((sender as FrameworkElement)?.DataContext is InstrumentWorkspaceViewModel instrument)
                instrument.EditCursorTick = e.Tick;
            (sender as TimelineSurface)?.Focus();
            return;
        }
        RunSynchronous("Set Playback Cursor", () => _session.SetPlaybackCursor(
            timeline?.ToProjectTick(project, e.Tick) ?? e.Tick));
    }

    private void OnTimelineTimeRangeSelected(object? sender, TimelineTimeRangeEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace) return;
        workspace.SetTimeRange(e.StartTick, e.EndTick);
        if (_session.IsLoopEnabled)
        {
            RunSynchronous("Update Loop Range", () => _session.SetLoopRange(
                workspace.ToProjectRange(_session.Project!, e.StartTick, e.EndTick)));
        }
    }

    private void OnTimelineSegmentSplitRequested(object? sender, TimelineItemEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } workspace
            || e.Item.Kind != TimelineItemKind.Segment)
        {
            return;
        }
        long splitTick = workspace.EditorSettings.SnapAbsolute(e.Tick);
        bool isMidiSegment = _session.Project!.PureMidiTracks.Any(track =>
            track.Segments.Any(segment => segment.Id == e.Item.Id));
        long firstId = _session.Project.NextStableId;
        StartWorkspaceEdit(
            isMidiSegment
                ? ProjectDomainEditCommands.SplitMidiSegment(e.Item.Id, splitTick)
                : ProjectDomainEditCommands.SplitSegment(e.Item.Id, splitTick),
            () => SelectCreatedWorkspaceObjects(workspace, firstId));
    }

    private void OnDeselectAllTimelineObjectsClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        workspace.Selection.Clear();
        _session.RefreshWorkspaceSelection(workspace);
    }

    private async void OnInvertTimelineSelectionClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace
            || GetTimelineContextSurface(sender) is not TimelineSurface surface
            || surface.Snapshot is not TimelineRenderSnapshot snapshot)
        {
            return;
        }
        await MaterializeTimelineSelectionAsync(
            workspace,
            surface,
            snapshot,
            invert: true,
            lane: null);
    }

    private TimelineSelectionOperationContext? ResolveTimelineSelectionOperationContext(
        TimelineSurface surface)
    {
        if (_session.ActiveWorkspace is WorkspaceViewModel listWorkspace
            && FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject) is not null
            && GetCachedObjectListSelection(listWorkspace) is { HomogeneousSource: { } listSource } listSelection)
            return CreateTimelineSelectionOperationContext(listSource, listSelection.Ids);
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0
            || workspace.Selection.HomogeneousTimelineSource
                is not WorkspaceTimelineSelectionSource source
            || !IsTimelineSelectionSourceCompatible(workspace, surface, source))
        {
            return null;
        }
        return CreateTimelineSelectionOperationContext(
            source,
            workspace.Selection.SharedIds);
    }

    private static TimelineSelectionOperationContext?
        CreateTimelineSelectionOperationContext(
            WorkspaceTimelineSelectionSource source,
            CompressedMidoraIdSet ids)
    {
        if (ids.Count == 0) return null;
        return source.Kind switch
        {
            WorkspaceTimelineSelectionKind.ArrangementSegment => new(
                TimelineSelectionObjectKind.MixedSegments,
                ids),
            WorkspaceTimelineSelectionKind.LogicalNote => new(
                TimelineSelectionObjectKind.LogicalNotes,
                ids,
                source.OwnerId),
            WorkspaceTimelineSelectionKind.LogicalParameterPoint => new(
                TimelineSelectionObjectKind.LogicalParameterPoints,
                ids,
                source.OwnerId,
                source.SecondaryOwnerId,
                PointMinimum: source.PointMinimum,
                PointMaximum: source.PointMaximum),
            WorkspaceTimelineSelectionKind.DirectMidiNote => new(
                TimelineSelectionObjectKind.DirectMidiNotes,
                ids,
                source.OwnerId),
            WorkspaceTimelineSelectionKind.DirectMidiEventPoint => new(
                TimelineSelectionObjectKind.DirectMidiEventPoints,
                ids,
                source.OwnerId,
                DirectMidiTarget: source.DirectMidiEventKind is DirectMidiChannelEventKind kind
                    ? new DirectMidiEventLaneTarget(kind, source.DirectMidiData1)
                    : null,
                PointMinimum: source.PointMinimum,
                PointMaximum: source.PointMaximum),
            WorkspaceTimelineSelectionKind.TemplateNote => new(
                TimelineSelectionObjectKind.TemplateNotes,
                ids,
                source.OwnerId,
                source.SecondaryOwnerId),
            WorkspaceTimelineSelectionKind.SubVoiceEventPoint => new(
                TimelineSelectionObjectKind.SubVoiceEventPoints,
                ids,
                source.OwnerId,
                source.SecondaryOwnerId,
                source.MidiTarget,
                PointMinimum: source.PointMinimum,
                PointMaximum: source.PointMaximum),
            _ => null
        };
    }

    private void OnFlipSegmentsExposedContentHorizontalClick(object sender, RoutedEventArgs e) =>
        ExecuteHorizontalFlip(SegmentSelectionTransformScope.ExposedContentOnly);

    private void OnFlipSegmentsAndContentHorizontalClick(object sender, RoutedEventArgs e) =>
        ExecuteHorizontalFlip(SegmentSelectionTransformScope.ExposedContentAndSegments);

    private void OnFlipSelectionHorizontalClick(object sender, RoutedEventArgs e) =>
        ExecuteHorizontalFlip(SegmentSelectionTransformScope.ExposedContentOnly);

    private async void ExecuteHorizontalFlip(SegmentSelectionTransformScope segmentScope)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        await ExecuteSelectionOperationAsync(context, "Flip Selection Horizontally", context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => ProjectDomainEditCommands.FlipSegmentsHorizontal(
                context.Ids,
                segmentScope),
            TimelineSelectionObjectKind.MidiSegments => ProjectDomainEditCommands.FlipMidiSegmentsHorizontal(
                context.Ids,
                segmentScope),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.FlipArrangementSegmentsHorizontal(
                    context.Ids,
                    segmentScope),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.FlipLogicalNotesHorizontal(
                context.OwnerId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.LogicalParameterPoints =>
                ProjectDomainEditCommands.FlipLogicalParameterPointsHorizontal(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids),
            TimelineSelectionObjectKind.DirectMidiNotes =>
                ProjectDomainEditCommands.FlipDirectMidiNotesHorizontal(
                    context.OwnerId!.Value,
                    context.Ids),
            TimelineSelectionObjectKind.DirectMidiEventPoints =>
                ProjectDomainEditCommands.FlipDirectMidiEventPointsHorizontal(
                    context.OwnerId!.Value,
                    context.Ids),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.FlipTemplateNotesHorizontal(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.SubVoiceEventPoints =>
                ProjectDomainEditCommands.FlipSubVoiceEventPointsHorizontal(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    context.MidiTarget!.Value),
            _ => throw new ArgumentOutOfRangeException()
        }, workspace, _lastTimelineCommandSurface);
    }

    private async void OnFlipSelectionVerticalClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        await ExecuteSelectionOperationAsync(context, "Flip Selection Vertically", context.Kind switch
        {
            TimelineSelectionObjectKind.Segments =>
                ProjectDomainEditCommands.FlipSegmentsVertical(context.Ids),
            TimelineSelectionObjectKind.MidiSegments =>
                ProjectDomainEditCommands.FlipMidiSegmentsVertical(context.Ids),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.FlipArrangementSegmentsVertical(context.Ids),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.FlipLogicalNotesVertical(
                context.OwnerId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.FlipDirectMidiNotesVertical(
                context.OwnerId!.Value,
                context.Ids),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.FlipTemplateNotesVertical(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids),
            _ => throw new InvalidOperationException(
                "Vertical flip is unavailable for the current selection type.")
        }, workspace, _lastTimelineCommandSurface);
    }

    private async void OnScaleSelectionClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }
        ProjectDocumentSession document = _session.Document!;
        long revision = document.PublicationRevision;
        if (!TryGetSelectionSpan(document, workspace, context, out long currentLength))
        {
            var read = await ReadSelectionInputAsync((readProject, token) => GetTimelineSelectionSpan(readProject, context, token));
            if (!read.Completed) return;
            currentLength = read.Value;
            _selectionSpanCache = (new(document), revision, context, currentLength);
        }
        if (currentLength <= 0)
        {
            ShowUnavailable(
                "Scale Selection",
                "The selection must span more than one Tick before it can be scaled.");
            return;
        }
        ScaleSelectionDialog dialog = new(
            currentLength,
            context.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true) return;
        await ExecuteSelectionOperationAsync(context, "Scale Selection", context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => ProjectDomainEditCommands.ScaleSegments(
                context.Ids,
                dialog.ScaleFactor,
                dialog.Scope),
            TimelineSelectionObjectKind.MidiSegments => ProjectDomainEditCommands.ScaleMidiSegments(
                context.Ids,
                dialog.ScaleFactor,
                dialog.Scope),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.ScaleArrangementSegments(
                    context.Ids,
                    dialog.ScaleFactor,
                    dialog.Scope),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.ScaleLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.ScaleFactor),
            TimelineSelectionObjectKind.LogicalParameterPoints =>
                ProjectDomainEditCommands.ScaleLogicalParameterPoints(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    dialog.ScaleFactor),
            TimelineSelectionObjectKind.DirectMidiNotes =>
                ProjectDomainEditCommands.ScaleDirectMidiNotes(
                    context.OwnerId!.Value,
                    context.Ids,
                    dialog.ScaleFactor),
            TimelineSelectionObjectKind.DirectMidiEventPoints =>
                ProjectDomainEditCommands.ScaleDirectMidiEventPoints(
                    context.OwnerId!.Value,
                    context.Ids,
                    dialog.ScaleFactor),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.ScaleTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                dialog.ScaleFactor),
            TimelineSelectionObjectKind.SubVoiceEventPoints =>
                ProjectDomainEditCommands.ScaleSubVoiceEventPoints(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    context.MidiTarget!.Value,
                    dialog.ScaleFactor),
            _ => throw new ArgumentOutOfRangeException()
        }, workspace, _lastTimelineCommandSurface);
    }

    private async void OnTransposeSelectionClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        TransposeSelectionDialog dialog = new() { Owner = this };
        if (ShowModalDialog(dialog) != true) return;
        await ExecuteSelectionOperationAsync(context, "Transpose Selection", context.Kind switch
        {
            TimelineSelectionObjectKind.Segments =>
                ProjectDomainEditCommands.TransposeSegments(context.Ids, dialog.Semitones),
            TimelineSelectionObjectKind.MidiSegments =>
                ProjectDomainEditCommands.TransposeMidiSegments(context.Ids, dialog.Semitones),
            TimelineSelectionObjectKind.MixedSegments =>
                ProjectDomainEditCommands.TransposeArrangementSegments(
                    context.Ids,
                    dialog.Semitones),
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.TransposeLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.Semitones),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.TransposeDirectMidiNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.Semitones),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.TransposeTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                dialog.Semitones),
            _ => throw new InvalidOperationException(
                "Transpose is unavailable for the current selection type.")
        }, workspace, _lastTimelineCommandSurface);
    }

    private async void OnBatchEditSelectionClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        bool pointContext = context.Kind is TimelineSelectionObjectKind.LogicalParameterPoints
            or TimelineSelectionObjectKind.DirectMidiEventPoints
            or TimelineSelectionObjectKind.SubVoiceEventPoints;
        BatchEditDialog dialog = new(
            pointContext ? BatchEditPresetKind.Event : BatchEditPresetKind.Note,
            context.PointMinimum,
            context.PointMaximum)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.Program is not BatchEditExpressionProgram program)
        {
            return;
        }
        using (program)
        {
            await ExecuteSelectionOperationAsync(context, "Batch Edit Selection", context.Kind switch
            {
                TimelineSelectionObjectKind.Segments =>
                    ProjectDomainEditCommands.BatchEditSegmentExposedNotes(context.Ids, program),
                TimelineSelectionObjectKind.MidiSegments =>
                    ProjectDomainEditCommands.BatchEditMidiSegmentExposedNotes(context.Ids, program),
                TimelineSelectionObjectKind.MixedSegments =>
                    ProjectDomainEditCommands.BatchEditArrangementSegmentExposedNotes(
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.BatchEditLogicalNotes(
                    context.OwnerId!.Value,
                    context.Ids,
                    program),
                TimelineSelectionObjectKind.LogicalParameterPoints =>
                    ProjectDomainEditCommands.BatchEditLogicalParameterPoints(
                        context.OwnerId!.Value,
                        context.SecondaryId!.Value,
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.DirectMidiNotes =>
                    ProjectDomainEditCommands.BatchEditDirectMidiNotes(
                        context.OwnerId!.Value,
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.DirectMidiEventPoints =>
                    ProjectDomainEditCommands.BatchEditDirectMidiEventPoints(
                        context.OwnerId!.Value,
                        context.Ids,
                        program),
                TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.BatchEditTemplateNotes(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    program),
                TimelineSelectionObjectKind.SubVoiceEventPoints =>
                    ProjectDomainEditCommands.BatchEditSubVoiceEventPoints(
                        context.OwnerId!.Value,
                        context.SecondaryId!.Value,
                        context.Ids,
                        context.MidiTarget!.Value,
                        program),
                _ => throw new ArgumentOutOfRangeException()
            }, workspace, _lastTimelineCommandSurface);
        }
    }

    private TimelineSelectionOperationContext? ResolveTimelineQuantizeSelectionOperationContext(
        TimelineSurface surface,
        TimelineSelectionOperationContext? ordinary)
    {
        if (ordinary?.Kind is TimelineSelectionObjectKind.LogicalNotes
            or TimelineSelectionObjectKind.DirectMidiNotes
            or TimelineSelectionObjectKind.TemplateNotes
            or TimelineSelectionObjectKind.LogicalParameterPoints
            or TimelineSelectionObjectKind.DirectMidiEventPoints
            or TimelineSelectionObjectKind.SubVoiceEventPoints)
        {
            return ordinary;
        }
        if (surface.SurfaceMode != TimelineSurfaceMode.EventLanes
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0
            || workspace.Selection.HomogeneousTimelineQuantizeScope
                is not WorkspaceTimelineSelectionSource source
            || !IsTimelineSelectionQuantizeScopeCompatible(workspace, surface, source))
        {
            return null;
        }
        return CreateTimelineSelectionOperationContext(
            source,
            workspace.Selection.SharedIds);
    }

    internal static bool IsSubVoiceNoteOperationSurface(
        TimelineSurfaceMode surfaceMode,
        object? tag) =>
        surfaceMode is TimelineSurfaceMode.PianoRoll or TimelineSurfaceMode.Velocity
        && tag is string text
        && (string.Equals(text, "SubVoiceNotes", StringComparison.Ordinal)
            || string.Equals(text, "SubVoiceVelocity", StringComparison.Ordinal));

    internal static WorkspaceTimelineSelectionSource? GetTimelineSelectionSource(
        WorkspaceViewModel workspace,
        TimelineSurface surface,
        TimelineRenderItem? item = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(surface);
        switch (workspace)
        {
            case TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            } when surface.SurfaceMode == TimelineSurfaceMode.Arrangement
                && (item is null || item.Value.Kind == TimelineItemKind.Segment):
                return new(WorkspaceTimelineSelectionKind.ArrangementSegment);

            case TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timeline
                when string.Equals(
                    surface.Tag as string,
                    "ParameterLanes",
                    StringComparison.Ordinal):
                {
                    ParameterLaneOption? lane = timeline.GetActiveParameterLaneOption();
                    if (lane?.LaneId is MidoraId laneId
                        && (item is null
                            || item.Value.Kind == TimelineItemKind.LogicalParameterPoint))
                    {
                        return new(
                            WorkspaceTimelineSelectionKind.LogicalParameterPoint,
                            segmentId,
                            laneId,
                            PointMinimum: timeline.ActiveEditingValueMinimum,
                            PointMaximum: timeline.ActiveEditingValueMaximum);
                    }
                    if (lane?.DirectMidiTarget is DirectMidiEventLaneTarget target
                        && (item is null
                            || item.Value.Kind == TimelineItemKind.DirectMidiEvent))
                    {
                        return new(
                            WorkspaceTimelineSelectionKind.DirectMidiEventPoint,
                            segmentId,
                            DirectMidiEventKind: target.Kind,
                            DirectMidiData1: target.Data1,
                            PointMinimum: timeline.ActiveEditingValueMinimum,
                            PointMaximum: timeline.ActiveEditingValueMaximum);
                    }
                    return null;
                }

            case TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } timeline
                when surface.SurfaceMode is TimelineSurfaceMode.PianoRoll
                    or TimelineSurfaceMode.Velocity:
                if (timeline.TabIconKind == WorkspaceTabIconKind.PureMidiTrack
                    && (item is null
                        || item.Value.Kind is TimelineItemKind.DirectMidiNote
                            or TimelineItemKind.Velocity))
                {
                    return new(
                        WorkspaceTimelineSelectionKind.DirectMidiNote,
                        segmentId);
                }
                if (timeline.TabIconKind == WorkspaceTabIconKind.LogicalTrack
                    && (item is null
                        || item.Value.Kind is TimelineItemKind.LogicalNote
                            or TimelineItemKind.Velocity))
                {
                    return new(
                        WorkspaceTimelineSelectionKind.LogicalNote,
                        segmentId);
                }
                return null;

            case InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } instrument
                when IsSubVoiceNoteOperationSurface(surface.SurfaceMode, surface.Tag)
                    && (item is null
                        || item.Value.Kind is TimelineItemKind.TemplateNote
                            or TimelineItemKind.Velocity):
                return new(
                    WorkspaceTimelineSelectionKind.TemplateNote,
                    instrumentId,
                    subVoiceId);

            case InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                ActiveSubVoiceId: MidoraId subVoiceId
            } instrument
                when string.Equals(
                    surface.Tag as string,
                    "SubVoiceEvents",
                    StringComparison.Ordinal)
                    && instrument.GetRenderLane(instrument.ActiveRenderLaneIndex)?.Target
                        is MidiValueTarget target
                    && (item is null
                        || item.Value.Kind is TimelineItemKind.LogicalParameterPoint
                            or TimelineItemKind.TemplateEvent):
                return new(
                    WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
                    instrumentId,
                    subVoiceId,
                    target,
                    PointMinimum: instrument.ActiveValueAxisMinimum,
                    PointMaximum: instrument.ActiveValueAxisMaximum);

            default:
                return null;
        }
    }

    private static bool IsTimelineSelectionSourceCompatible(
        WorkspaceViewModel workspace,
        TimelineSurface surface,
        WorkspaceTimelineSelectionSource source) =>
        GetTimelineSelectionSource(workspace, surface) == source;

    private static bool IsTimelineSelectionQuantizeScopeCompatible(
        WorkspaceViewModel workspace,
        TimelineSurface surface,
        WorkspaceTimelineSelectionSource source) =>
        GetTimelineSelectionSource(workspace, surface)?.QuantizeScope
            == source.QuantizeScope;

    private bool CanOpenTimelineProperties(TimelineSurface surface)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return false;
        if (workspace is TimelineWorkspaceViewModel { IsConductor: true })
            return workspace.Selection.Ids.Count == 1;
        return workspace.Selection.Ids.Count != 0
            && workspace.Selection.HomogeneousTimelineSource is WorkspaceTimelineSelectionSource source
            && IsTimelineSelectionSourceCompatible(workspace, surface, source);
    }

    private async void OnHumanizeSelectionClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }

        TimelineSurface? sourceSurface = _lastTimelineCommandSurface;
        HumanizeSelectionDialog dialog = new() { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Options is not TimelineHumanizeOptions options)
            return;

        ITimelineSelectionResultEditCommand command = context.Kind switch
        {
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.HumanizeLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                options),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.HumanizeDirectMidiNotes(
                context.OwnerId!.Value,
                context.Ids,
                options),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.HumanizeTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                options),
            _ => throw new InvalidOperationException(
                "Humanize is unavailable for the current selection type.")
        };
        await ExecuteSelectionOperationAsync(context,
            "Humanize Notes",
            command,
            workspace,
            sourceSurface);
    }

    private async void OnSplitNotesClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }

        TimelineSurface? sourceSurface = _lastTimelineCommandSurface;
        SplitNotesDialog dialog = new() { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Options is not NoteSplitOptions options)
            return;

        try
        {
            ITimelineSelectionResultEditCommand command = context.Kind switch
            {
                TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.SplitLogicalNotes(
                    context.OwnerId!.Value,
                    context.Ids,
                    options),
                TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.SplitDirectMidiNotes(
                    context.OwnerId!.Value,
                    context.Ids,
                    options),
                TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.SplitTemplateNotes(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    options),
                _ => throw new InvalidOperationException(
                    "Split is unavailable for the current selection type.")
            };
            await ExecuteSelectionOperationAsync(context,
                "Split Notes",
                command,
                workspace,
                sourceSurface);
        }
        finally
        {
            options.ExpressionProgram?.Dispose();
        }
    }

    private async void OnJoinNotesClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineSelectionOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }

        TimelineSurface? sourceSurface = _lastTimelineCommandSurface;
        JoinNotesDialog dialog = new() { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Options is not NoteJoinOptions options)
            return;

        ITimelineSelectionResultEditCommand command = context.Kind switch
        {
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.JoinLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                options),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.JoinDirectMidiNotes(
                context.OwnerId!.Value,
                context.Ids,
                options),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.JoinTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                options),
            _ => throw new InvalidOperationException(
                "Join is unavailable for the current selection type.")
        };
        await ExecuteSelectionOperationAsync(context,
            "Join Notes",
            command,
            workspace,
            sourceSurface);
    }

    private async void OnQuantizeSelectionClick(object sender, RoutedEventArgs e)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_timelineQuantizeOperationContext is not { Ids.Count: > 0 } context
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace)
        {
            return;
        }

        bool noteSelection = context.Kind is TimelineSelectionObjectKind.LogicalNotes
            or TimelineSelectionObjectKind.DirectMidiNotes
            or TimelineSelectionObjectKind.TemplateNotes;
        bool eventSelection = context.Kind is TimelineSelectionObjectKind.LogicalParameterPoints
            or TimelineSelectionObjectKind.DirectMidiEventPoints
            or TimelineSelectionObjectKind.SubVoiceEventPoints;
        if (!noteSelection && !eventSelection) return;

        TimelineSurface? sourceSurface = _lastTimelineCommandSurface;
        QuantizeSelectionDialog dialog = new(
            noteSelection,
            GetEditorSettingsForSurface(sourceSurface).OperationSubdivision)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.Grid is not TimelineQuantizeGrid grid)
            return;

        ITimelineSelectionResultEditCommand command = context.Kind switch
        {
            TimelineSelectionObjectKind.LogicalNotes => ProjectDomainEditCommands.QuantizeLogicalNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.NoteOptions!),
            TimelineSelectionObjectKind.DirectMidiNotes => ProjectDomainEditCommands.QuantizeDirectMidiNotes(
                context.OwnerId!.Value,
                context.Ids,
                dialog.NoteOptions!),
            TimelineSelectionObjectKind.TemplateNotes => ProjectDomainEditCommands.QuantizeTemplateNotes(
                context.OwnerId!.Value,
                context.SecondaryId!.Value,
                context.Ids,
                dialog.NoteOptions!),
            TimelineSelectionObjectKind.LogicalParameterPoints =>
                ProjectDomainEditCommands.QuantizeLogicalParameterPoints(
                    context.OwnerId!.Value,
                    context.Ids,
                    grid),
            TimelineSelectionObjectKind.DirectMidiEventPoints =>
                ProjectDomainEditCommands.QuantizeDirectMidiEvents(
                    context.OwnerId!.Value,
                    context.Ids,
                    grid),
            TimelineSelectionObjectKind.SubVoiceEventPoints =>
                ProjectDomainEditCommands.QuantizeTemplateEvents(
                    context.OwnerId!.Value,
                    context.SecondaryId!.Value,
                    context.Ids,
                    grid),
            _ => throw new InvalidOperationException(
                "Quantize is unavailable for the current selection type.")
        };
        await ExecuteSelectionOperationAsync(context,
            noteSelection ? "Quantize Notes" : "Quantize Events",
            command,
            workspace,
            sourceSurface);
    }

    private Task<bool> ExecuteStagedTimelineSelectionOperationAsync(
        string title,
        ITimelineSelectionResultEditCommand command,
        WorkspaceViewModel workspace,
        TimelineSurface? sourceSurface)
        => ExecuteStagedProjectOperationAsync(title, command, workspace, sourceSurface);

    private Task<bool> ExecuteSelectionOperationAsync(TimelineSelectionOperationContext context,
        string title, IProjectEditCommand command, WorkspaceViewModel workspace,
        TimelineSurface? sourceSurface = null)
    {
        if (context.FrozenSelectionRevision is long revision && workspace.Selection.Revision != revision)
        {
            (command as IDisposable)?.Dispose();
            return Task.FromResult(false);
        }
        if (context.RetainedIds is { Count: > 0 } && TryGetTimelineObjectOwner(workspace, out var owner))
            command = ProjectDomainEditCommands.WithTimelineObjectSelection(command, owner, context.Ids);
        return ExecuteStagedProjectOperationAsync(title, command, workspace, sourceSurface,
            retainedSelectionIds: context.RetainedIds);
    }

    private async Task<bool> ExecuteStagedProjectOperationAsync(
        string title,
        IProjectEditCommand command,
        WorkspaceViewModel workspace,
        TimelineSurface? sourceSurface = null,
        Action? afterPublication = null,
        CompressedMidoraIdSet? retainedSelectionIds = null)
    {
        using IDisposable? commandLifetime = command as IDisposable;
        bool completed = await RunOperationAsync(
            title,
            async cancellationToken =>
            {
                string? detail = null;
                using DispatcherCoalescingProgress<TimelineEditPreparationProgress> progress = new(
                    Dispatcher,
                    TimeSpan.FromMilliseconds(100),
                    value =>
                    {
                        detail = value.Detail ?? detail;
                        _session.ActiveForegroundTask?.Report(
                            FormatTimelineEditPreparationProgress(value with { Detail = detail }),
                            value.IsIndeterminate ? null : value.OverallFraction);
                    });
                using StagedProjectEdit staged = await Task.Run(
                    () => _session.PrepareProjectEdit(
                        command,
                        workspace,
                        cancellationToken,
                        progress, retainedSelectionIds: retainedSelectionIds),
                    cancellationToken);
                progress.Flush();
                if (_session.ActiveForegroundTask is { } activeTask)
                    activeTask.SealCancellationBeforePublication();
                else
                    cancellationToken.ThrowIfCancellationRequested();
                _session.ActiveForegroundTask?.Report("Publishing prepared edit", 1.00);
                _session.ExecutePreparedPreservingWorkspaceSelection(
                    staged,
                    workspace,
                    command as ITimelineSelectionResultEditCommand);
                _preparedSelectionWorkspace = staged.PreparedSelection is null ? null : workspace;
                _preparedSelectionRevision = _session.Document?.PublicationRevision ?? -1;
                afterPublication?.Invoke();
            },
            canCancel: true);

        if (completed)
            _session.RefreshWorkspaceSelection(workspace);
        RestoreModalCommandFocus(workspace, sourceSurface);
        return completed;
    }

    private Task<bool> ExecuteWorkspaceEditAsync(
        IProjectEditCommand command,
        Action? afterPublication = null)
    {
        WorkspaceViewModel workspace = _session.ActiveWorkspace
            ?? throw new InvalidOperationException("A Project edit requires an active Workspace.");
        return ExecuteStagedProjectOperationAsync(
            command.Name,
            command,
            workspace,
            _lastTimelineCommandSurface,
            afterPublication);
    }

    internal static string FormatTimelineEditPreparationProgress(
        TimelineEditPreparationProgress value)
    {
        string phase = value.Phase switch
        {
            TimelineEditPreparationPhase.ResolvingSelection => "Resolving selection",
            TimelineEditPreparationPhase.PreparingIndex => "Preparing selection index",
            TimelineEditPreparationPhase.ReadingSelection => "Reading selection",
            TimelineEditPreparationPhase.Sorting => "Sorting records",
            TimelineEditPreparationPhase.WritingStorage => "Writing temporary storage",
            TimelineEditPreparationPhase.Planning => "Planning edit",
            TimelineEditPreparationPhase.ResolvingCollisions => "Resolving collisions",
            TimelineEditPreparationPhase.BuildingResult => "Building result",
            TimelineEditPreparationPhase.Ready => "Prepared",
            _ => "Preparing edit"
        };
        string text = value.Total > 0
            ? $"{phase} ({value.Completed:N0}/{value.Total:N0})"
            : value.Completed > 0 ? $"{phase} ({value.Completed:N0} processed)" : phase;
        return string.IsNullOrWhiteSpace(value.Detail) ? text : $"{text} · {value.Detail}";
    }

    private static long GetTimelineSelectionSpan(
        MidoraProject project,
        TimelineSelectionOperationContext context,
        CancellationToken token)
    {
        IReadOnlyCollection<MidoraId> ids = context.Ids;
        long directoryTotal = context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => project.Tracks.Sum(static track => (long)track.Segments.Count),
            TimelineSelectionObjectKind.MidiSegments => project.PureMidiTracks.Sum(static track => (long)track.Segments.Count),
            TimelineSelectionObjectKind.MixedSegments => project.Tracks.Sum(static track => (long)track.Segments.Count)
                + project.PureMidiTracks.Sum(static track => (long)track.Segments.Count),
            _ => 0
        };
        long directoryVisited = 0;
        return context.Kind switch
        {
            TimelineSelectionObjectKind.Segments => RangeSpan(project.Tracks
                .SelectMany(static track => track.Segments)
                .Where(segment => IsSelected(segment.Id))
                .Select(static segment => (
                    Start: segment.ProjectStartTick,
                    End: segment.ProjectRange.EndTick))),
            TimelineSelectionObjectKind.MidiSegments => RangeSpan(project.PureMidiTracks
                .SelectMany(static track => track.Segments)
                .Where(segment => IsSelected(segment.Id))
                .Select(static segment => (
                    Start: segment.ProjectStartTick,
                    End: segment.ProjectRange.EndTick))),
            TimelineSelectionObjectKind.MixedSegments => RangeSpan(project.Tracks
                .SelectMany(static track => track.Segments)
                .Where(segment => IsSelected(segment.Id))
                .Select(static segment => (
                    Start: segment.ProjectStartTick,
                    End: segment.ProjectRange.EndTick))
                .Concat(project.PureMidiTracks
                    .SelectMany(static track => track.Segments)
                    .Where(segment => IsSelected(segment.Id))
                    .Select(static segment => (
                        Start: segment.ProjectStartTick,
                        End: segment.ProjectRange.EndTick)))),
            TimelineSelectionObjectKind.LogicalNotes => RangeSpan(
                ReadTimelineSelection(project, TimelineWorkspaceViewModel.FindSegment(project, context.OwnerId)!.Value.Segment.Notes
                    .CreateQuerySnapshot(), ids, token)
                    .Select(static note => (
                        Start: note.StartTick,
                        End: checked(note.StartTick + note.LengthTicks)))),
            TimelineSelectionObjectKind.DirectMidiNotes => RangeSpan(
                ReadTimelineSelection(project, TimelineWorkspaceViewModel.FindMidiSegment(project, context.OwnerId)!.Value.Segment.Notes
                    .CreateObjectSource(), ids, token)
                    .Select(static value => (
                        Start: value.StartTick,
                        End: checked(value.StartTick + value.LengthTicks)))),
            TimelineSelectionObjectKind.TemplateNotes => RangeSpan(ReadTimelineSelection(project, project.EventInstruments
                .Single(value => value.Id == context.OwnerId)
                .SubVoices.Single(value => value.Id == context.SecondaryId)
                .Events.CreateQuerySnapshot(), ids, token)
                .Where(static value => value.Kind == TemplateEventKind.Note)
                .Select(static value => (
                    Start: value.Tick,
                    End: checked(value.Tick + value.LengthTicks)))),
            TimelineSelectionObjectKind.LogicalParameterPoints => PointSpan(
                ReadTimelineSelection(project, TimelineWorkspaceViewModel.FindSegment(project, context.OwnerId)!.Value.Segment
                    .ParameterLanes.Single(value => value.Id == context.SecondaryId)
                    .Points.CreateQuerySnapshot(), ids, token)
                    .Select(static value => value.Tick)),
            TimelineSelectionObjectKind.DirectMidiEventPoints => PointSpan(
                ReadTimelineSelection(project, TimelineWorkspaceViewModel.FindMidiSegment(project, context.OwnerId)!.Value.Segment
                    .ChannelEvents.CreateObjectSource(), ids, token)
                    .Select(static value => value.Tick)),
            TimelineSelectionObjectKind.SubVoiceEventPoints => PointSpan(ReadTimelineSelection(project, project.EventInstruments
                .Single(value => value.Id == context.OwnerId)
                .SubVoices.Single(value => value.Id == context.SecondaryId)
                .Events.CreateQuerySnapshot(), ids, token)
                .Select(static value => value.Tick)),
            _ => throw new ArgumentOutOfRangeException()
        };

        bool IsSelected(MidoraId id)
        {
            token.ThrowIfCancellationRequested();
            if ((++directoryVisited & 255) == 0 || directoryVisited == directoryTotal)
                SelectionReadProgress.Value?.Report(new(TimelineEditPreparationPhase.ReadingSelection, directoryVisited, directoryTotal));
            return ids is IReadOnlySet<MidoraId> set ? set.Contains(id) : ids.Contains(id);
        }

        long RangeSpan(IEnumerable<(long Start, long End)> source)
        {
            bool any = false;
            long minimum = long.MaxValue;
            long maximum = long.MinValue;
            foreach ((long start, long end) in source)
            {
                token.ThrowIfCancellationRequested();
                any = true;
                minimum = Math.Min(minimum, start);
                maximum = Math.Max(maximum, end);
            }
            return any ? checked(maximum - minimum) : 0;
        }

        long PointSpan(IEnumerable<long> source)
            => MeasurePointSelectionSpan(source, token);
    }

    private static TimelineSurface? GetTimelineContextSurface(object sender) =>
        sender is MenuItem menuItem
        && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu contextMenu
            ? contextMenu.PlacementTarget as TimelineSurface
            : null;

    private void OnSetTimeRangeFromObjectsClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace
            || !workspace.SetTimeRangeFromObjectSelection())
        {
            ShowUnavailable("Set Time Range", "Select at least one timeline object first.");
        }
    }

    private void OnSelectObjectsInTimeRangeClick(object sender, RoutedEventArgs e)
    {
        TimelineSurface? surface = GetTimelineContextSurface(sender);
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel workspace
            || surface?.Snapshot is not TimelineRenderSnapshot snapshot
            || !workspace.SetObjectSelectionFromTimeRange(
                snapshot,
                GetTimelineSelectionSource(workspace, surface)))
        {
            ShowUnavailable("Select Objects in Time Range", "Create a non-empty Time Range that intersects timeline objects first.");
            return;
        }
        _session.RefreshWorkspaceSelection(workspace);
    }

    private void OnClearTimeRangeClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel workspace)
        {
            workspace.ClearTimeRange();
        }
    }

    private async void OnTimelineBackgroundInvoked(object? sender, TimelinePointEventArgs e)
    {
        try { await HandleTimelineBackgroundInvokedAsync(sender, e); }
        catch (OverflowException)
        {
            ShowError("Timeline edit", "The requested Tick or duration exceeds the supported 64-bit range. No edit was applied.");
        }
    }

    private async Task HandleTimelineBackgroundInvokedAsync(object? sender, TimelinePointEventArgs e)
    {
        if (_session.ActiveWorkspace is WorkspaceViewModel activeWorkspace)
        {
            if (activeWorkspace is TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Arrangement
                } arrangement
                && e.IsEmptyBackground)
            {
                ClearArrangementTrackSelection(arrangement);
            }
            else
            {
                activeWorkspace.ActiveLane = e.Lane;
            }
            if (sender is TimelineSurface { ToolMode: TimelineToolMode.Select }
                && !e.IsDoubleClick
                && e.Modifiers == ModifierKeys.None
                && activeWorkspace.Selection.Ids.Count != 0)
            {
                activeWorkspace.Selection.Clear();
                _session.RefreshWorkspaceSelection(activeWorkspace);
            }
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline)
        {
            bool parameterSurface = sender is FrameworkElement { Tag: "ParameterLanes" };
            TimelineEditorSettings activeSettings = parameterSurface
                ? timeline.LaneEditorSettings
                : timeline.EditorSettings;
            timeline.EditCursorTick = activeSettings.SnapAbsolute(e.Tick);
            if (!e.IsDoubleClick || _session.Project is null) return;
            if (timeline.IsConductor)
            {
                try
                {
                    long tick = activeSettings.SnapAbsolute(e.Tick);
                    IProjectEditCommand? create = e.Lane switch
                    {
                        0 => ProjectDomainEditCommands.CreateTempo(tick,
                            (decimal)timeline.TempoAxisMinimum + (decimal)e.NormalizedValue *
                            ((decimal)timeline.TempoAxisMaximum - (decimal)timeline.TempoAxisMinimum)),
                        1 => ProjectDomainEditCommands.CreateTimeSignature(tick, 4, 4),
                        2 => ProjectDomainEditCommands.CreateKeySignature(tick, 0, isMinor: false),
                        3 => ProjectDomainEditCommands.CreateProjectMarker(tick, string.Empty),
                        4 => ProjectDomainEditCommands.CreateProjectEndMarker(tick),
                        _ => null
                    };
                    if (create is not null)
                        await ExecuteStagedProjectOperationAsync("Create Conductor event", create, timeline, sender as TimelineSurface);
                }
                catch (Exception ex) { ShowError("Create Conductor event", ex.Message); }
                return;
            }
            RunSynchronous("Create timeline object", () =>
            {
                long snapped = activeSettings.SnapAbsolute(e.Tick);
                switch (timeline.Mode)
                {
                    case TimelineWorkspaceMode.Arrangement:
                        if (timeline.GetArrangementLane(e.Lane) is not
                            { CanContainSegments: true, ObjectId: MidoraId trackId } arrangementLane) return;
                        long requestedLength = timeline.EditorSettings.DefaultLengthTicks;
                        long nextStart;
                        bool occupied;
                        if (arrangementLane.Kind == ArrangementLaneKind.LogicalTrack)
                        {
                            LogicalTrack track = _session.Project.Tracks.Single(value => value.Id == trackId);
                            nextStart = track.Segments.Where(item => item.ProjectStartTick > snapped)
                                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
                            occupied = track.Segments.Any(item => snapped >= item.ProjectStartTick && snapped < item.ProjectRange.EndTick);
                        }
                        else
                        {
                            PureMidiTrack track = _session.Project.PureMidiTracks.Single(value => value.Id == trackId);
                            nextStart = track.Segments.Where(item => item.ProjectStartTick > snapped)
                                .Select(item => item.ProjectStartTick).DefaultIfEmpty(long.MaxValue).Min();
                            occupied = track.Segments.Any(item => snapped >= item.ProjectStartTick && snapped < item.ProjectRange.EndTick);
                        }
                        if (occupied) return;
                        long available = nextStart == long.MaxValue ? requestedLength : checked(nextStart - snapped);
                        if (available <= 0) return;
                        IProjectEditCommand createSegment = arrangementLane.Kind == ArrangementLaneKind.LogicalTrack
                            ? ProjectDomainEditCommands.CreateSegment(trackId, snapped, Math.Min(requestedLength, available))
                            : ProjectDomainEditCommands.CreateMidiSegment(trackId, snapped, Math.Min(requestedLength, available));
                        ExecuteAndSelectCreated(createSegment, timeline);
                        break;
                    case TimelineWorkspaceMode.Segment:
                        if (timeline.ObjectId is not MidoraId segmentId) return;
                        if (parameterSurface)
                        {
                            (LogicalTrack Track, Segment Segment)? location = TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
                            if (location is null)
                            {
                                if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is not null
                                    && timeline.GetActiveParameterLaneOption()?.DirectMidiTarget is DirectMidiEventLaneTarget directTarget)
                                {
                                    (int data1, int data2) = DenormalizeDirectMidiEventValue(directTarget, e.NormalizedValue);
                                    ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateDirectMidiChannelEvent(
                                        segmentId,
                                        snapped,
                                        directTarget.Kind,
                                        data1,
                                        data2), timeline);
                                }
                                return;
                            }
                            if (timeline.GetActiveParameterLaneOption()?.LaneId is not MidoraId laneId) return;
                            LogicalParameterLane lane = location.Value.Segment.ParameterLanes.Single(item => item.Id == laneId);
                            EventInstrument instrument = _session.Project.FindEventInstrumentDefinition(
                                location.Value.Track)
                                ?? throw new InvalidOperationException(
                                    "The Logical Track is not bound to an Event Instrument Usage.");
                            LogicalParameterDefinition definition = instrument.LogicalParameters.Single(
                                item => item.Id == lane.ParameterId);
                            ExecuteAndSelectCreated(ProjectDomainEditCommands.CreateLogicalParameterPoint(
                                segmentId,
                                lane.Id,
                                snapped,
                                TimelineWorkspaceViewModel.DenormalizeParameterValue(definition, e.NormalizedValue),
                                CurveInterpolation.Step), timeline);
                            break;
                        }
                        IProjectEditCommand createNote = TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is not null
                            ? ProjectDomainEditCommands.CreateDirectMidiNote(
                                segmentId,
                                snapped,
                                timeline.EditorSettings.DefaultLengthTicks,
                                Math.Clamp(127 - e.Lane, 0, 127),
                                timeline.EditorSettings.DefaultVelocity)
                            : ProjectDomainEditCommands.CreateLogicalNote(
                                segmentId,
                                snapped,
                                timeline.EditorSettings.DefaultLengthTicks,
                                Math.Clamp(127 - e.Lane, 0, 127),
                                timeline.EditorSettings.DefaultVelocity);
                        ExecuteAndSelectCreated(createNote, timeline);
                        break;
                }
            });
            return;
        }

        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel instrument)
        {
            bool eventSurface = sender is FrameworkElement { Tag: "SubVoiceEvents" };
            TimelineEditorSettings activeSettings = eventSurface
                ? instrument.EventLaneEditorSettings
                : instrument.EditorSettings;
            instrument.EditCursorTick = activeSettings.SnapAbsolute(e.Tick);
            if (!e.IsDoubleClick
                || _session.Project is null
                || instrument.ObjectId is not MidoraId instrumentId)
            {
                return;
            }
            EventInstrument source = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
            if (sender is FrameworkElement { Tag: "SubVoiceNotes" }
                && instrument.ActiveSubVoiceId is MidoraId noteSubVoiceId)
            {
                long noteTick = instrument.EditorSettings.SnapAbsolute(e.Tick);
                RunSynchronous("Create Template Note", () => ExecuteAndSelectCreated(
                    ProjectDomainEditCommands.CreateTemplateNote(
                        instrumentId,
                        noteSubVoiceId,
                        noteTick,
                        instrument.EditorSettings.DefaultLengthTicks,
                        Math.Clamp(127 - e.Lane, 0, 127),
                        instrument.EditorSettings.DefaultVelocity),
                    instrument));
                return;
            }
            InstrumentRenderLane? lane = instrument.GetRenderLane(instrument.ActiveRenderLaneIndex);
            if (lane is null) return;
            long tick = instrument.EventLaneEditorSettings.SnapAbsolute(e.Tick);
            if (lane.Target is MidiValueTarget target)
            {
                int value = checked((int)InstrumentWorkspaceViewModel.DenormalizeMidiValue(
                    target,
                    e.NormalizedValue));
                RunSynchronous($"Create {TemplateEventMidiTargets.Format(target)}", () => ExecuteAndSelectCreated(
                    CreateTemplateEventCommand(instrumentId, lane.SubVoiceId, target, tick, value), instrument));
            }
        }
    }

    private async void OnTimelineItemEditCompleted(object? sender, TimelineItemEditEventArgs e)
    {
        if (_session.Project is null || _session.ActiveWorkspace is null) return;
        if (sender is TimelineSurface commandSurface) _lastTimelineCommandSurface = commandSurface;
        try
        {
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline)
            {
                TimelineEditorSettings activeSettings = e.Item.Kind is TimelineItemKind.LogicalParameterPoint
                    or TimelineItemKind.DirectMidiEvent
                    or TimelineItemKind.OpaqueMidiEvent
                    ? timeline.LaneEditorSettings
                    : timeline.EditorSettings;
                long snappedDelta = activeSettings.SnapDelta(
                    e.TickDelta,
                    TimelineTickMath.Clamp((Int128)e.Item.StartTick + e.TickDelta));
                long unclampedSnappedDelta = snappedDelta;
                long snappedTarget = Math.Max(0, checked(e.Item.StartTick + snappedDelta));
                long nonnegativeSnappedDelta = checked(snappedTarget - e.Item.StartTick);
                IReadOnlyCollection<MidoraId> selected = timeline.Selection.Ids.Count == 0
                    ? [e.Item.Id]
                    : timeline.Selection.SharedIds;
                switch (timeline.Mode)
                {
                    case TimelineWorkspaceMode.Arrangement:
                        await EditArrangementItem(
                            timeline,
                            e,
                            selected,
                            snappedTarget,
                            e.EditKind == TimelineItemEditKind.ResizeStart
                                ? unclampedSnappedDelta
                                : nonnegativeSnappedDelta);
                        break;
                    case TimelineWorkspaceMode.Segment when timeline.ObjectId is MidoraId segmentId:
                        if (e.Item.Kind == TimelineItemKind.LogicalParameterPoint)
                        {
                            await EditLogicalParameterPoint(segmentId, e, snappedTarget, selected);
                        }
                        else if (e.Item.Kind == TimelineItemKind.DirectMidiEvent)
                        {
                            await EditDirectMidiEventPoint(segmentId, e, snappedTarget, selected);
                        }
                        else if (e.Item.Kind == TimelineItemKind.OpaqueMidiEvent)
                        {
                            await EditOpaqueMidiEventPoint(segmentId, e, snappedTarget, selected);
                        }
                        else if (e.Item.Kind == TimelineItemKind.DirectMidiNote)
                        {
                            await EditDirectMidiNotes(
                                segmentId,
                                e,
                                selected,
                                e.EditKind == TimelineItemEditKind.ResizeStart
                                    ? unclampedSnappedDelta
                                    : nonnegativeSnappedDelta);
                        }
                        else
                        {
                            await EditLogicalNotes(
                                segmentId,
                                e,
                                selected,
                                e.EditKind == TimelineItemEditKind.ResizeStart
                                    ? unclampedSnappedDelta
                                    : nonnegativeSnappedDelta);
                        }
                        break;
                    case TimelineWorkspaceMode.Conductor:
                        await ExecuteStagedProjectOperationAsync("Move Conductor events",
                            ProjectDomainEditCommands.MoveConductorEvents(selected, nonnegativeSnappedDelta,
                                sender is TimelineSurface { Tag: "ConductorTempo" }
                                    ? (decimal)(e.ValueDelta * (timeline.TempoAxisMaximum - timeline.TempoAxisMinimum)) : 0m,
                                duplicate: e.CopyRequested), timeline, sender as TimelineSurface);
                        break;
                }
                return;
            }
            if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel instrument)
            {
                await EditTemplateEvent(instrument, e);
            }
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"Edit timeline object: {exception.Message}", isError: true);
        }
    }

    private sealed class DeferredTimelineEditValues<T>(int count, Func<IEnumerable<T>> values)
        : IReadOnlyCollection<T>
    {
        public int Count => count;
        public IEnumerator<T> GetEnumerator() => values().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private async void OnTimelineEventPointEditCompleted(object? sender, TimelineEventPointEditEventArgs e)
    {
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId directSegmentId
            } directTimeline
            && directTimeline.GetActiveParameterLaneOption()?.DirectMidiTarget is DirectMidiEventLaneTarget directTarget
            && _session.Project is MidoraProject directProject
            && TimelineWorkspaceViewModel.FindMidiSegment(directProject, directSegmentId) is not null
            && e.Points.Count != 0)
        {
            var directEdits = new DeferredTimelineEditValues<DirectMidiEventPointEdit>(e.Points.Count,
                () => e.Points.Select(value =>
                {
                    (int data1, int data2) = DenormalizeDirectMidiEventValue(directTarget, value.Value);
                    return new DirectMidiEventPointEdit(value.Key, data1, data2);
                }));
            await ExecuteStagedProjectOperationAsync("Draw Direct MIDI Event points",
                ProjectDomainEditCommands.UpsertDirectMidiEventPoints(
                    directSegmentId,
                    directTarget.Kind,
                    directTarget.Data1,
                    directEdits), directTimeline, sender as TimelineSurface);
            return;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline
            && timeline.Mode == TimelineWorkspaceMode.Segment
            && timeline.ObjectId is MidoraId segmentId
            && timeline.GetActiveParameterLaneOption() is ParameterLaneOption option
            && option.LaneId is MidoraId laneId
            && _session.Project is MidoraProject project
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is var location
            && location is not null
            && project.ResolveEventInstrumentDefinitionId(location.Value.Track) is MidoraId eventInstrumentId
            && project.EventInstruments.FirstOrDefault(value => value.Id == eventInstrumentId)
                is EventInstrument eventInstrument
            && eventInstrument.LogicalParameters.FirstOrDefault(value => value.Id == option.ParameterId)
                is LogicalParameterDefinition definition
            && e.Points.Count != 0)
        {
            var pointEdits = new DeferredTimelineEditValues<LogicalParameterPointEdit>(e.Points.Count,
                () => e.Points.Select(value => new LogicalParameterPointEdit(
                    value.Key,
                    TimelineWorkspaceViewModel.DenormalizeParameterValue(definition, value.Value))));
            await ExecuteStagedProjectOperationAsync("Draw Logical Parameter points",
                ProjectDomainEditCommands.UpsertLogicalParameterPoints(
                    segmentId,
                    laneId,
                    pointEdits), timeline, sender as TimelineSurface);
            return;
        }

        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || workspace.ActiveSubVoiceId is not MidoraId voiceId
            || workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target is not MidiValueTarget target
            || e.Points.Count == 0)
        {
            return;
        }
        var edits = new DeferredTimelineEditValues<TemplateEventPointEdit>(e.Points.Count,
            () => e.Points.Select(value => new TemplateEventPointEdit(
                value.Key,
                checked((int)InstrumentWorkspaceViewModel.DenormalizeMidiValue(target, value.Value)))));
        await ExecuteStagedProjectOperationAsync("Draw Event points",
            ProjectDomainEditCommands.UpsertTemplateEventPoints(
                instrumentId,
                voiceId,
                target,
                edits), workspace, sender as TimelineSurface);
    }

    private async void OnSubVoiceEventPointTraceCompleted(object? sender, TimelineEventPointTraceEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId || workspace.ActiveSubVoiceId is not MidoraId voiceId
            || workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target is not MidiValueTarget target
            || e.Trace.Count == 0) return;
        await ExecuteStagedProjectOperationAsync("Draw Event points",
            ProjectDomainEditCommands.DrawTemplateEventPoints(instrumentId, voiceId, target,
                token => e.Sample(token).Select(point => new TemplateEventPointEdit(point.Tick,
                    checked((int)InstrumentWorkspaceViewModel.DenormalizeMidiValue(target, point.NormalizedValue))))),
            workspace, sender as TimelineSurface);
    }

    private async Task EditArrangementItem(
        TimelineWorkspaceViewModel workspace,
        TimelineItemEditEventArgs edit,
        IReadOnlyCollection<MidoraId> selected,
        long snappedTarget,
        long snappedDelta)
    {
        int targetLaneIndex = Math.Clamp(
            checked(edit.Item.Lane + edit.LaneDelta), 0,
            Math.Max(0, workspace.Snapshot!.ArrangementLanes.Count - 1));
        ArrangementLaneDescriptor? targetLane = workspace.GetArrangementLane(targetLaneIndex);
        if (edit.EditKind == TimelineItemEditKind.Move
            && RequiresArrangementSegmentConversion(workspace.Snapshot!, selected, edit))
        {
            if (targetLane is not { CanContainSegments: true, ObjectId: MidoraId targetTrackId }) return;
            long minimumStart = edit.Item.StartTick;
            foreach (MidoraId id in selected)
                if (workspace.Snapshot!.TryGetItem(id, out TimelineRenderItem item))
                    minimumStart = Math.Min(minimumStart, item.StartTick);
            long primaryTick = checked(edit.Item.StartTick + Math.Max(snappedDelta, -minimumStart));
            await TransferArrangementSegmentsAsync(workspace, selected, edit.Item.Id,
                targetTrackId, primaryTick, edit.CopyRequested);
            return;
        }
        long endDelta = workspace.EditorSettings.SnapDelta(
            edit.TickDelta, TimelineTickMath.Clamp((Int128)edit.Item.EndTick + edit.TickDelta));
        long firstNewStableId = _session.Project!.NextStableId;
        var command = new ArrangementGestureEditCommand(selected, edit.Item.Id, edit.Item.StartTick,
            edit.EditKind, edit.CopyRequested, snappedDelta, endDelta,
            workspace.EditorSettings.EffectiveOperationStepTicks, targetLane);
        if (!await ExecuteWorkspaceEditAsync(command)) return;
        if (command.PreparedCopy)
            SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
    }

    private static IEnumerable<T> ReadTimelineSelection<T>(MidoraProject project, ITimelineObjectSource<T> source,
        IReadOnlyCollection<MidoraId> ids, CancellationToken token = default) where T : unmanaged =>
        ProjectTimelineReadPreparation.ReadSelectedValues(project, source, ids, token,
            SelectionReadProgress.Value, preserveFormalOrder: false);

    private static int CountMatchingTimelineIds<T>(MidoraProject project, ITimelineObjectSource<T> source,
        IReadOnlyCollection<MidoraId> ids, CancellationToken token)
    {
        int count = 0;
        foreach (T _ in ReadMetricValues(project, source, ids, token))
        {
            token.ThrowIfCancellationRequested();
            count++;
        }
        return count;
    }

    private async Task<(bool Completed, T Value)> ReadSelectionInputAsync<T>(Func<MidoraProject, CancellationToken, T> read)
    {
        ProjectDocumentSession document = _session.Document!;
        long revision = document.PublicationRevision;
        WorkspaceViewModel workspace = _session.ActiveWorkspace!;
        T value = default!;
        bool completed = await RunOperationAsync("Read Selection", async token =>
        {
            using DispatcherCoalescingProgress<TimelineEditPreparationProgress> progress = new(
                Dispatcher, TimeSpan.FromMilliseconds(100),
                update => _session.ActiveForegroundTask?.Report(FormatTimelineEditPreparationProgress(update),
                    update.IsIndeterminate ? null : update.OverallFraction));
            value = await Task.Run(() => WithSelectionReadProgress(progress, () => read(document.Project, token)), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session.Document, document) || document.PublicationRevision != revision)
                throw new InvalidOperationException("The Project changed while reading the selection.");
            progress.Report(new(TimelineEditPreparationPhase.Ready, 1, 1));
            progress.Flush();
        }, canCancel: true);
        if (!completed) RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);
        return (completed, value);
    }

    internal readonly record struct SelectionInputMetrics(CompressedMidoraIdSet Ids,
        long MinimumTick, long MaximumTick, int MinimumPitch, int MaximumPitch,
        double MinimumValue, double MaximumValue, long MaximumLength);

    internal static SelectionInputMetrics ReadSelectionInputMetrics<T>(MidoraProject sourceProject,
        ITimelineObjectSource<T> source,
        IReadOnlyCollection<MidoraId> ids,
        Func<T, (long Tick, long End, int Pitch, double Value)> project,
        CancellationToken token, Func<T, bool>? include = null,
        long maximumBuilderWorkingBytes = PagedEditResourceBudget.DefaultMaximumWorkingBytes)
    {
        long minTick = long.MaxValue, maxTick = long.MinValue, maxLength = 0;
        int minPitch = int.MaxValue, maxPitch = int.MinValue;
        double minValue = double.PositiveInfinity, maxValue = double.NegativeInfinity;
        int matchingCount = 0;
        long inputPages = 0, lastPage = -1;
        foreach (MidoraId id in ids)
        {
            token.ThrowIfCancellationRequested();
            // CompressedMidoraIdSet's internal storage contract groups sorted
            // IDs into 4096-ID pages (PageShift = 12). Count actual pages, not
            // objects, so a dense million-ID selection is not falsely rejected.
            long page = (id.Value - 1) >> 12;
            if (ids is not CompressedMidoraIdSet || page != lastPage) inputPages++;
            lastPage = page;
        }
        foreach (T value in ReadMetricValues(sourceProject, source, ids, token))
        {
            token.ThrowIfCancellationRequested();
            if (include is not null && !include(value)) continue;
            matchingCount++;
            var scalar = project(value);
            minTick = Math.Min(minTick, scalar.Tick); maxTick = Math.Max(maxTick, scalar.End);
            maxLength = Math.Max(maxLength, checked(scalar.End - scalar.Tick));
            minPitch = Math.Min(minPitch, scalar.Pitch); maxPitch = Math.Max(maxPitch, scalar.Pitch);
            minValue = Math.Min(minValue, scalar.Value); maxValue = Math.Max(maxValue, scalar.Value);
        }
        token.ThrowIfCancellationRequested();
        CompressedMidoraIdSet result;
        if (matchingCount == 0) result = CompressedMidoraIdSet.Empty;
        else if (matchingCount == ids.Count && ids is CompressedMidoraIdSet original) result = original;
        else
        {
            EnsureSelectionInputBuilderBudget(inputPages, maximumBuilderWorkingBytes);
            result = CompressedMidoraIdSet.Create(Read(), token);
        }
        return new(result, minTick, maxTick, minPitch, maxPitch, minValue, maxValue, maxLength);
        IEnumerable<MidoraId> Read()
        {
            foreach (T value in ReadMetricValues(sourceProject, source, ids, token))
            {
                token.ThrowIfCancellationRequested();
                if (include is not null && !include(value)) continue;
                yield return MetricValueId(value);
            }
        }
    }

    private static void EnsureSelectionInputBuilderBudget(long inputPageCount, long maximumWorkingBytes)
    {
        // Covers bitmap pages, dictionary resize overlap, sorted entries, and
        // final sparse/page containers alive together; not an exact CLR size.
        const long bytesPerPage = 1536;
        long maximumPages = (maximumWorkingBytes - 4096) / bytesPerPage;
        if (maximumWorkingBytes < 4096 || inputPageCount < 0 || inputPageCount > maximumPages)
            throw new InvalidOperationException("The selected-object filter exceeds its temporary working-memory budget. Select fewer objects.");
    }

    private async Task EditLogicalNotes(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        IReadOnlyCollection<MidoraId> selected,
        long snappedDelta)
    {
        Segment segment = TimelineWorkspaceViewModel.FindSegment(_session.Project!, segmentId)?.Segment
            ?? throw new InvalidOperationException("The Segment no longer exists.");
        TimelineWorkspaceViewModel workspace =
            (TimelineWorkspaceViewModel)_session.ActiveWorkspace!;
        IReadOnlyCollection<MidoraId> noteIds = selected;
        long minimumStart;
        if (workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.LogicalNote,
                out TimelineSelectionMetrics metrics)
            && metrics.Count == selected.Count)
        {
            minimumStart = metrics.MinimumStartTick;
        }
        else
        {
            var source = segment.Notes.CreateQuerySnapshot();
            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, selected,
                static item => (item.StartTick, checked(item.StartTick + item.LengthTicks), item.Note, (double)item.Velocity), token));
            if (!read.Completed || read.Value.Ids.Count == 0) return;
            noteIds = read.Value.Ids;
            minimumStart = read.Value.MinimumTick;
        }
        switch (edit.EditKind)
        {
            case TimelineItemEditKind.Move:
                long tickDelta = Math.Max(snappedDelta, -minimumStart);
                int pitchDelta = -edit.LaneDelta;
                if (edit.CopyRequested)
                {
                    long firstNewStableId = _session.Project!.NextStableId;
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateLogicalNotes(
                        segmentId,
                        noteIds,
                        segmentId,
                        checked(minimumStart + tickDelta),
                        pitchDelta))) return;
                    SelectCreatedWorkspaceObjects(
                        (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                        firstNewStableId,
                        replaceSelectionWhenNoObjectSurvives: true,
                        timelineSource: new(
                            WorkspaceTimelineSelectionKind.LogicalNote,
                            segmentId));
                }
                else
                {
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.MoveLogicalNotes(
                        segmentId,
                        noteIds,
                        tickDelta,
                        pitchDelta))) return;
                }
                break;
            case TimelineItemEditKind.ResizeStart:
                long startDelta = Math.Max(
                    snappedDelta,
                    -minimumStart);
                if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segmentId,
                    noteIds,
                    startDelta,
                    endDelta: 0,
                    minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks))) return;
                break;
            case TimelineItemEditKind.ResizeEnd:
                long endDelta = ((TimelineWorkspaceViewModel)_session.ActiveWorkspace!).EditorSettings.SnapDelta(
                    edit.TickDelta,
                    TimelineTickMath.Clamp((Int128)edit.Item.EndTick + edit.TickDelta));
                if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustLogicalNoteEdges(
                    segmentId,
                    noteIds,
                    startDelta: 0,
                    endDelta,
                    minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks))) return;
                break;
        }
    }

    private async Task EditDirectMidiNotes(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        IReadOnlyCollection<MidoraId> selected,
        long snappedDelta)
    {
        MidiSegment segment = TimelineWorkspaceViewModel.FindMidiSegment(_session.Project!, segmentId)?.Segment
            ?? throw new InvalidOperationException("The MIDI Segment no longer exists.");
        TimelineWorkspaceViewModel workspace =
            (TimelineWorkspaceViewModel)_session.ActiveWorkspace!;
        IReadOnlyCollection<MidoraId> noteIds = selected;
        long minimumStart;
        if (workspace.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.DirectMidiNote,
                out TimelineSelectionMetrics metrics)
            && metrics.Count == selected.Count)
        {
            minimumStart = metrics.MinimumStartTick;
        }
        else
        {
            var source = segment.Notes.CreateObjectSource();
            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, selected,
                static item => (item.StartTick, checked(item.StartTick + item.LengthTicks), item.Key, (double)item.NoteOnVelocity), token));
            if (!read.Completed || read.Value.Ids.Count == 0) return;
            noteIds = read.Value.Ids;
            minimumStart = read.Value.MinimumTick;
        }
        switch (edit.EditKind)
        {
            case TimelineItemEditKind.Move:
                long tickDelta = Math.Max(snappedDelta, -minimumStart);
                int keyDelta = -edit.LaneDelta;
                if (edit.CopyRequested)
                {
                    long firstNewStableId = _session.Project!.NextStableId;
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                        segmentId,
                        noteIds,
                        tickDelta,
                        keyDelta))) return;
                    SelectCreatedWorkspaceObjects(
                        (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                        firstNewStableId,
                        replaceSelectionWhenNoObjectSurvives: true,
                        timelineSource: new(
                            WorkspaceTimelineSelectionKind.DirectMidiNote,
                            segmentId));
                }
                else
                {
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.MoveDirectMidiNotes(
                        segmentId,
                        noteIds,
                        tickDelta,
                        keyDelta))) return;
                }
                break;
            case TimelineItemEditKind.ResizeStart:
                if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                    segmentId,
                    noteIds,
                    Math.Max(snappedDelta, -minimumStart),
                    0,
                    workspace.EditorSettings.EffectiveOperationStepTicks))) return;
                break;
            case TimelineItemEditKind.ResizeEnd:
                long endDelta = workspace.EditorSettings.SnapDelta(
                    edit.TickDelta,
                    TimelineTickMath.Clamp((Int128)edit.Item.EndTick + edit.TickDelta));
                if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustDirectMidiNoteEdges(
                    segmentId,
                    noteIds,
                    0,
                    endDelta,
                    workspace.EditorSettings.EffectiveOperationStepTicks))) return;
                break;
        }
    }

    private async Task EditDirectMidiEventPoint(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        long snappedTarget,
        IReadOnlyCollection<MidoraId> selectedIds)
    {
        if (_session.Project is not MidoraProject project
            || TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { } location
            || !location.Segment.ChannelEvents.TryGetById(
                edit.Item.Id,
                out DirectMidiChannelEvent? point)
            || point is null)
        {
            return;
        }
        var eventSource = location.Segment.ChannelEvents.CreateObjectSource();
        var frozenIds = CompressedMidoraIdSet.Create(selectedIds).Add(point.Id);
        DirectMidiEventLaneTarget pointTarget = TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(point);
        var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, eventSource, frozenIds,
            static value => (value.Tick, value.Tick, 0, (double)(value.Kind switch
            {
                DirectMidiChannelEventKind.PitchBend => (value.Data2 << 7) | value.Data1,
                DirectMidiChannelEventKind.ProgramChange or DirectMidiChannelEventKind.ChannelPressure => value.Data1,
                _ => value.Data2
            })), token,
            value => TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(value) == pointTarget));
        if (!read.Completed || read.Value.Ids.Count == 0) return;
        var selectedEventIds = read.Value.Ids;
        long minimumTick = read.Value.MinimumTick;
        long tickDelta = edit.EditKind == TimelineItemEditKind.Move
            ? Math.Max(checked(snappedTarget - point.Tick), -minimumTick)
            : 0;
        int valueDelta = checked((int)Math.Round(
            edit.ValueDelta * (point.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127),
            MidpointRounding.AwayFromZero));
        valueDelta = Math.Clamp(valueDelta, checked(-(int)read.Value.MinimumValue),
            checked((point.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127) - (int)read.Value.MaximumValue));
        long firstNewStableId = project.NextStableId;
        if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustDirectMidiEventPointValues(
            segmentId,
            selectedEventIds,
            tickDelta,
            valueDelta,
            edit.CopyRequested))) return;
        if (edit.CopyRequested)
        {
            DirectMidiEventLaneTarget target =
                TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(point);
            SelectCreatedWorkspaceObjects(
                (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                firstNewStableId,
                timelineSource: new(
                    WorkspaceTimelineSelectionKind.DirectMidiEventPoint,
                    segmentId,
                    DirectMidiEventKind: target.Kind,
                    DirectMidiData1: target.Data1,
                    PointMinimum: MidiEditingValueDomain.Offset(target.Kind, target.Data1),
                    PointMaximum: (target.Kind == DirectMidiChannelEventKind.PitchBend ? 16383 : 127)
                        + MidiEditingValueDomain.Offset(target.Kind, target.Data1)));
        }
    }

    private static (int Data1, int Data2) DenormalizeDirectMidiEventValue(
        DirectMidiEventLaneTarget target,
        double normalized)
    {
        normalized = Math.Clamp(normalized, 0, 1);
        if (target.Kind == DirectMidiChannelEventKind.PitchBend)
        {
            int value = checked((int)Math.Round(normalized * 16383, MidpointRounding.AwayFromZero));
            return (value & 0x7f, (value >> 7) & 0x7f);
        }
        int scalar = checked((int)Math.Round(normalized * 127, MidpointRounding.AwayFromZero));
        return target.Kind switch
        {
            DirectMidiChannelEventKind.ControlChange
                or DirectMidiChannelEventKind.PolyphonicKeyPressure => (target.Data1, scalar),
            DirectMidiChannelEventKind.ProgramChange
                or DirectMidiChannelEventKind.ChannelPressure => (scalar, 0),
            _ => (target.Data1, scalar)
        };
    }

    private async Task EditOpaqueMidiEventPoint(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        long snappedTarget,
        IReadOnlyCollection<MidoraId> selectedIds)
    {
        if (_session.Project is not MidoraProject project
            || TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not { } location
            || !location.Segment.OpaqueEvents.TryGetById(
                edit.Item.Id,
                out OpaqueMidiEvent? point)
            || point is null)
        {
            return;
        }
        IReadOnlyCollection<MidoraId> selectedEventIds;
        long minimumTick;
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline
            && timeline.SelectionSnapshot.TryGetMetrics(
                TimelineItemKind.OpaqueMidiEvent,
                out TimelineSelectionMetrics metrics)
            && metrics.Count == selectedIds.Count)
        {
            selectedEventIds = selectedIds;
            minimumTick = metrics.MinimumStartTick;
        }
        else
        {
            var source = location.Segment.OpaqueEvents.CreateObjectSource();
            var frozenIds = CompressedMidoraIdSet.Create(selectedIds).Add(point.Id);
            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, frozenIds,
                static value => (value.Tick, value.Tick, 0, 0d), token));
            if (!read.Completed || read.Value.Ids.Count == 0) return;
            selectedEventIds = read.Value.Ids;
            minimumTick = read.Value.MinimumTick;
        }
        long tickDelta = Math.Max(
            checked(snappedTarget - point.Tick),
            -minimumTick);
        long firstNewStableId = project.NextStableId;
        if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustOpaqueMidiEvents(
            segmentId,
            selectedEventIds,
            tickDelta,
            edit.CopyRequested))) return;
        if (edit.CopyRequested)
        {
            SelectCreatedWorkspaceObjects(
                (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                firstNewStableId);
        }
    }

    private TimelineEditorSettings GetActiveEditorSettings() => _session.ActiveWorkspace switch
    {
        TimelineWorkspaceViewModel timeline => timeline.EditorSettings,
        InstrumentWorkspaceViewModel instrument => instrument.EditorSettings,
        _ => _session.ArrangementEditorSettings
    };

    private TimelineEditorSettings GetFocusedEditorSettings()
    {
        TimelineSurface? focusedSurface = Keyboard.FocusedElement as TimelineSurface;
        return GetEditorSettingsForSurface(focusedSurface);
    }

    private TimelineEditorSettings GetEditorSettingsForSurface(TimelineSurface? surface) =>
        GetWorkspaceEditorSettingsForSurface(_session.ActiveWorkspace, surface) ?? _session.ArrangementEditorSettings;

    internal static TimelineEditorSettings? GetWorkspaceEditorSettingsForSurface(
        WorkspaceViewModel? workspace, TimelineSurface? surface)
    {
        bool eventLaneFocused = surface is { SurfaceMode: TimelineSurfaceMode.EventLanes };
        return workspace switch
        {
            // Tempo uses an EventLanes renderer but shares the Conductor toolbar's
            // main settings. Only Segment parameter lanes have independent Snap.
            TimelineWorkspaceViewModel timeline when eventLaneFocused && !timeline.IsConductor => timeline.LaneEditorSettings,
            TimelineWorkspaceViewModel timeline => timeline.EditorSettings,
            InstrumentWorkspaceViewModel instrument when eventLaneFocused => instrument.EventLaneEditorSettings,
            InstrumentWorkspaceViewModel instrument => instrument.EditorSettings,
            _ => null
        };
    }

    private async Task EditLogicalParameterPoint(
        MidoraId segmentId,
        TimelineItemEditEventArgs edit,
        long snappedTarget,
        IReadOnlyCollection<MidoraId> selectedIds)
    {
        if (_session.Project is null) return;
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
        if (location is null) return;
        LogicalParameterLane? lane = null;
        CurvePoint? point = null;
        foreach (LogicalParameterLane candidate in location.Value.Segment.ParameterLanes)
        {
            if (!candidate.Points.TryGetById(edit.Item.Id, out CurvePoint? resolved)
                || resolved is null)
            {
                continue;
            }
            lane = candidate;
            point = resolved;
            break;
        }
        if (lane is null || point is null) return;
        EventInstrument instrument = _session.Project.FindEventInstrumentDefinition(location.Value.Track)
            ?? throw new InvalidOperationException(
                "The Logical Track is not bound to an Event Instrument Usage.");
        LogicalParameterDefinition definition = instrument.LogicalParameters.Single(item => item.Id == lane.ParameterId);
        double normalized = TimelineWorkspaceViewModel.NormalizeParameterValue(definition, point.Value);
        double value = TimelineWorkspaceViewModel.DenormalizeParameterValue(
            definition,
            Math.Clamp(normalized + edit.ValueDelta, 0, 1));
        var pointSource = lane.Points.CreateQuerySnapshot();
        var frozenIds = CompressedMidoraIdSet.Create(selectedIds).Add(point.Id);
        var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, pointSource, frozenIds,
            static value => (value.Tick, value.Tick, 0, value.Value), token));
        if (!read.Completed || read.Value.Ids.Count == 0) return;
        var selected = read.Value.Ids;
        double requestedValueDelta = value - point.Value;
        double selectedMinimumValue = read.Value.MinimumValue;
        double selectedMaximumValue = read.Value.MaximumValue;
        double minimumValueDelta = definition.Minimum - selectedMinimumValue;
        double maximumValueDelta = definition.Maximum - selectedMaximumValue;
        double valueDelta = Math.Clamp(requestedValueDelta, minimumValueDelta, maximumValueDelta);
        long tickDelta = edit.EditKind == TimelineItemEditKind.Move
            ? Math.Max(
                checked(snappedTarget - point.Tick),
                -read.Value.MinimumTick)
            : 0;
        if (edit.CopyRequested)
        {
            long firstNewStableId = _session.Project.NextStableId;
            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateLogicalParameterPoints(
                segmentId,
                lane.Id,
                selected,
                tickDelta,
                valueDelta))) return;
            SelectCreatedWorkspaceObjects(
                (TimelineWorkspaceViewModel)_session.ActiveWorkspace!,
                firstNewStableId,
                timelineSource: new(
                    WorkspaceTimelineSelectionKind.LogicalParameterPoint,
                    segmentId,
                    lane.Id,
                    PointMinimum: definition.Minimum,
                    PointMaximum: definition.Maximum));
        }
        else
        {
            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustLogicalParameterPoints(
                segmentId,
                lane.Id,
                selected,
                tickDelta,
                valueDelta))) return;
        }
    }

    private void OnAddParameterLaneClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment,
                ObjectId: MidoraId segmentId
            } workspace
            || _session.Project is null) return;
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project, segmentId);
        if (location is null)
        {
            if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project, segmentId) is null)
            {
                return;
            }
            DirectMidiLaneTargetDialog midiDialog = new()
            {
                Owner = this
            };
            if (ShowModalDialog(midiDialog) == true
                && midiDialog.Result is DirectMidiEventLaneTarget target)
            {
                workspace.AddDirectMidiLaneTarget(target);
                _session.RefreshWorkspace(workspace);
                workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions.ToList()
                    .FindIndex(value => value.DirectMidiTarget == target);
                _session.RefreshWorkspace(workspace);
                ShowActiveEventLaneTab(workspace);
                RestoreModalCommandFocus(workspace, FindWorkspaceElement<TimelineSurface>("ParameterLanes"));
            }
            return;
        }
        if (_session.Project.ResolveEventInstrumentDefinitionId(location.Value.Track)
            is not MidoraId instrumentId)
        {
            ShowUnavailable("Add Logical Parameter Lane", "Bind this Logical Track to an Event Instrument first.");
            return;
        }
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        HashSet<MidoraId> existing = location.Value.Segment.ParameterLanes.Select(item => item.ParameterId).ToHashSet();
        SelectionDialogItem[] options = instrument.LogicalParameters
            .Where(item => !existing.Contains(item.Id))
            .Select(item => new SelectionDialogItem(item.Id, item.Name, $"{item.Type} · {item.Minimum}–{item.Maximum}"))
            .ToArray();
        if (options.Length == 0)
        {
            ShowUnavailable("Add Logical Parameter Lane", instrument.LogicalParameters.Count == 0
                ? "The bound Event Instrument has no Logical Parameters."
                : "This Segment already has a Lane for every Logical Parameter.");
            return;
        }
        SelectionDialog dialog = new("Add Logical Parameter Lane", "Select a Logical Parameter from the bound Event Instrument.", options) { Owner = this };
        if (ShowModalDialog(dialog) == true && dialog.SelectedValue is MidoraId parameterId)
        {
            if (RunSynchronous("Create Logical Parameter Lane", () => ExecuteAndSelectCreated(
                ProjectDomainEditCommands.CreateLogicalParameterLane(segmentId, parameterId), workspace)))
            {
                workspace.ActiveParameterLaneIndex = workspace.ParameterLaneOptions.ToList().FindIndex(value => value.ParameterId == parameterId);
                workspace.PreferCurrentParameterLaneOnNextRebuild();
                _session.RefreshWorkspace(workspace);
                ShowActiveEventLaneTab(workspace);
                RestoreModalCommandFocus(workspace, FindWorkspaceElement<TimelineSurface>("ParameterLanes"));
            }
        }
    }

    private async Task EditTemplateEvent(
        InstrumentWorkspaceViewModel workspace,
        TimelineItemEditEventArgs edit)
    {
        if (workspace.ObjectId is not MidoraId instrumentId) return;
        EventInstrument instrument = _session.Project!.EventInstruments.Single(item => item.Id == instrumentId);
        SubVoice? voice = null;
        TemplateEvent? template = null;
        foreach (SubVoice candidate in instrument.SubVoices)
        {
            if (!candidate.Events.TryGetById(edit.Item.Id, out TemplateEvent? resolved)
                || resolved is null)
            {
                continue;
            }
            voice = candidate;
            template = resolved;
            break;
        }
        if (voice is null || template is null) return;
        TimelineEditorSettings activeSettings = template.Kind == TemplateEventKind.Note
            ? workspace.EditorSettings
            : workspace.EventLaneEditorSettings;
        long snappedDelta = activeSettings.SnapDelta(
            edit.TickDelta,
            TimelineTickMath.Clamp((Int128)(edit.EditKind == TimelineItemEditKind.ResizeEnd
                ? checked(template.Tick + Math.Max(1, template.LengthTicks))
                : template.Tick) + edit.TickDelta));
        if (template.Kind == TemplateEventKind.Note)
        {
            IReadOnlyCollection<MidoraId> selectedIds = workspace.Selection.SharedIds;
            TimelineSelectionMetrics noteMetrics;
            if (!workspace.SelectionSnapshot.TryGetMetrics(TimelineItemKind.TemplateNote, out noteMetrics)
                || noteMetrics.Count != selectedIds.Count)
            {
                var source = voice.Events.CreateQuerySnapshot();
                var frozenIds = workspace.Selection.SharedIds.Add(template.Id);
                var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, frozenIds,
                    static item => (item.Tick, checked(item.Tick + item.LengthTicks), item.Number, item.Value / 127d),
                    token, static item => item.Kind == TemplateEventKind.Note));
                if (!read.Completed || read.Value.Ids.Count == 0) return;
                selectedIds = read.Value.Ids;
                noteMetrics = new(selectedIds.Count, read.Value.MinimumTick, read.Value.MaximumTick,
                    127 - read.Value.MaximumPitch, 127 - read.Value.MinimumPitch,
                    read.Value.MinimumValue, read.Value.MaximumValue, default);
            }
            switch (edit.EditKind)
            {
                case TimelineItemEditKind.Move:
                    long tickDelta = Math.Max(snappedDelta, -noteMetrics.MinimumStartTick);
                    int pitchDelta = -edit.LaneDelta;
                    if (edit.CopyRequested)
                    {
                        long firstNewStableId = _session.Project.NextStableId;
                        if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateTemplateNotes(
                            instrumentId,
                            voice.Id,
                            selectedIds,
                            checked(noteMetrics.MinimumStartTick + tickDelta),
                            pitchDelta))) return;
                        SelectCreatedWorkspaceObjects(
                            workspace,
                            firstNewStableId,
                            replaceSelectionWhenNoObjectSurvives: true,
                            timelineSource: new(
                                WorkspaceTimelineSelectionKind.TemplateNote,
                                instrumentId,
                                voice.Id));
                    }
                    else
                    {
                        if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.MoveTemplateNotes(
                            instrumentId,
                            voice.Id,
                            selectedIds,
                            tickDelta,
                            pitchDelta))) return;
                    }
                    return;
                case TimelineItemEditKind.ResizeStart:
                    long startDelta = Math.Max(
                        snappedDelta,
                        -noteMetrics.MinimumStartTick);
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                        instrumentId,
                        voice.Id,
                        selectedIds,
                        startDelta,
                        endDelta: 0,
                        minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks))) return;
                    return;
                case TimelineItemEditKind.ResizeEnd:
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustTemplateNoteEdges(
                        instrumentId,
                        voice.Id,
                        selectedIds,
                        startDelta: 0,
                        endDelta: snappedDelta,
                        minimumLengthTicks: workspace.EditorSettings.EffectiveOperationStepTicks))) return;
                    return;
            }
        }
        MidiValueTarget? activeTarget = workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target;
        if (activeTarget is MidiValueTarget target
            && TemplateEventMidiTargets.Enumerate(template).Contains(target))
        {
            var eventSource = voice.Events.CreateQuerySnapshot();
            var frozenIds = workspace.Selection.SharedIds.Add(template.Id);
            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, eventSource, frozenIds,
                item => (item.Tick, item.Tick, 0, (double)TemplateEventMidiTargets.GetValue(item, target)), token,
                item => item.Kind != TemplateEventKind.Note && TemplateEventMidiTargets.Enumerate(item).Contains(target)));
            if (!read.Completed || read.Value.Ids.Count == 0) return;
            var eventIds = read.Value.Ids;
            long requestedTickDelta = workspace.EventLaneEditorSettings.SnapDelta(
                edit.TickDelta,
                TimelineTickMath.Clamp((Int128)template.Tick + edit.TickDelta));
            long tickDelta = Math.Max(
                requestedTickDelta,
                -read.Value.MinimumTick);
            (double minimum, double maximum) = InstrumentWorkspaceViewModel.MidiValueRange(target);
            int requestedValueDelta = checked((int)Math.Round(
                edit.ValueDelta * (maximum - minimum),
                MidpointRounding.AwayFromZero));
            int minimumValueDelta = checked((int)Math.Ceiling(
                minimum - read.Value.MinimumValue));
            int maximumValueDelta = checked((int)Math.Floor(
                maximum - read.Value.MaximumValue));
            int valueDelta = Math.Clamp(
                requestedValueDelta,
                minimumValueDelta,
                maximumValueDelta);
            long firstNewStableId = _session.Project.NextStableId;
            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                instrumentId,
                voice.Id,
                eventIds,
                target,
                tickDelta,
                valueDelta,
                edit.CopyRequested))) return;
            if (edit.CopyRequested)
            {
                SelectCreatedWorkspaceObjects(
                    workspace,
                    firstNewStableId,
                    timelineSource: new(
                        WorkspaceTimelineSelectionKind.SubVoiceEventPoint,
                        instrumentId,
                        voice.Id,
                        target,
                        PointMinimum: minimum + MidiEditingValueDomain.Offset(target),
                        PointMaximum: maximum + MidiEditingValueDomain.Offset(target)));
            }
            return;
        }
        long tick = Math.Max(0, checked(template.Tick + snappedDelta));
        long length = template.LengthTicks;
        IProjectEditCommand command = template.Kind switch
        {
            TemplateEventKind.Note => ProjectDomainEditCommands.UpdateTemplateNote(
                instrumentId, voice.Id, template.Id, tick, length,
                template.Number, template.Value, template.FollowPitchDelta),
            TemplateEventKind.ControlChange => ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrumentId, voice.Id, template.Id, tick, template.Number, template.Value),
            TemplateEventKind.Bank => ProjectDomainEditCommands.UpdateTemplateBank(
                instrumentId, voice.Id, template.Id, tick,
                template.HasBankMsb ? template.Value : null,
                template.HasBankLsb ? template.SecondaryValue : null),
            TemplateEventKind.Program => ProjectDomainEditCommands.UpdateTemplateProgram(
                instrumentId, voice.Id, template.Id, tick, template.Value),
            TemplateEventKind.PitchBend => ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrumentId, voice.Id, template.Id, tick, template.Value),
            TemplateEventKind.RegisteredParameter => ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrumentId, voice.Id, template.Id, tick, template.Number, template.Value),
            TemplateEventKind.NonRegisteredParameter => ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrumentId, voice.Id, template.Id, tick, template.Number, template.Value),
            TemplateEventKind.PitchBendRange => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrumentId, voice.Id, template.Id, tick, template.Value, template.SecondaryValue),
            _ => throw new InvalidOperationException("Unsupported Template Event kind.")
        };
        if (!await ExecuteWorkspaceEditAsync(command)) return;
    }

    private async Task EditValueCurvePoint(InstrumentWorkspaceViewModel workspace, TimelineItemEditEventArgs edit)
    {
        if (workspace.ObjectId is not MidoraId instrumentId || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        foreach (SubVoice voice in instrument.SubVoices)
        {
            ValueCurve? curve = null;
            CurvePoint? point = null;
            foreach (ValueCurve candidate in voice.Curves)
            {
                if (!candidate.Points.TryGetById(edit.Item.Id, out CurvePoint? resolved)
                    || resolved is null)
                {
                    continue;
                }
                curve = candidate;
                point = resolved;
                break;
            }
            if (curve is null || point is null) continue;
            (double minimum, double maximum) = InstrumentWorkspaceViewModel.MidiValueRange(curve.Target);
            var pointSource = curve.Points.CreateQuerySnapshot();
            var frozenIds = workspace.Selection.SharedIds.Add(point.Id);
            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, pointSource, frozenIds,
                static value => (value.Tick, value.Tick, 0, value.Value), token));
            if (!read.Completed || read.Value.Ids.Count == 0) return;
            var selected = read.Value.Ids;
            double requestedValue = Math.Round(
                Math.Clamp(point.Value + edit.ValueDelta * (maximum - minimum), minimum, maximum),
                MidpointRounding.AwayFromZero);
            double requestedValueDelta = requestedValue - point.Value;
            double minimumValueDelta = minimum
                - read.Value.MinimumValue;
            double maximumValueDelta = maximum
                - read.Value.MaximumValue;
            long requestedTickDelta = workspace.EditorSettings.SnapDelta(
                edit.TickDelta,
                TimelineTickMath.Clamp((Int128)edit.Item.StartTick + edit.TickDelta));
            long tickDelta = Math.Max(
                requestedTickDelta,
                -read.Value.MinimumTick);
            await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustValueCurvePoints(
                instrumentId,
                voice.Id,
                curve.Id,
                selected,
                tickDelta,
                Math.Clamp(requestedValueDelta, minimumValueDelta, maximumValueDelta)));
            return;
        }
    }

    private void OnAddTemplateEventClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        MidoraId? voiceId = workspace.ActiveSubVoiceId;
        if (voiceId is null)
        {
            SelectionDialog voiceDialog = new(
                "Add Event",
                "Select the target SubVoice.",
                instrument.SubVoices.Select((voice, index) => new SelectionDialogItem(
                    voice.Id,
                    string.IsNullOrWhiteSpace(voice.Name) ? $"SubVoice {index + 1}" : voice.Name)))
            { Owner = this };
            if (ShowModalDialog(voiceDialog) != true || voiceDialog.SelectedValue is not MidoraId selectedVoiceId) return;
            voiceId = selectedVoiceId;
        }
        MidiTargetDialog targetDialog = new("Add Event") { Owner = this };
        if (ShowModalDialog(targetDialog) != true || targetDialog.Result is not MidiValueTarget target) return;
        SubVoice targetVoice = instrument.SubVoices.Single(item => item.Id == voiceId.Value);
        TemplateEventMappingTarget mappingTarget = TemplateEventMidiTargets.ToMappingTarget(target);
        if (targetVoice.EventMappings.Any(item => item.Target == mappingTarget))
        {
            ShowUnavailable(
                "Add Event",
                $"The {TemplateEventMidiTargets.Format(target)} event lane already exists in this SubVoice.");
            return;
        }
        if (!RunSynchronous($"Create {TemplateEventMidiTargets.Format(target)} lane", () =>
            _session.Execute(ProjectDomainEditCommands.CreateSubVoiceEventLane(
                instrumentId,
                voiceId.Value,
                target)))) return;
        if (!_session.ActivateCreatedSubVoiceEventLane(workspace, voiceId.Value, target)) return;
        ShowActiveEventLaneTab(workspace);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsActive || !ReferenceEquals(_session.ActiveWorkspace, workspace)
                || workspace.ActiveSubVoiceId != voiceId.Value
                || workspace.GetRenderLane(workspace.ActiveRenderLaneIndex)?.Target != target) return;
            if (FindWorkspaceElement<TimelineSurface>("SubVoiceEvents") is { IsVisible: true, IsEnabled: true } surface
                && ReferenceEquals(surface.DataContext, workspace))
                surface.Focus();
        }));
    }

    private void OnDeleteSubVoiceEventLaneClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || workspace.GetRenderLane(workspace.ActiveRenderLaneIndex) is not InstrumentRenderLane
            {
                Target: MidiValueTarget target,
                EventMappingTarget: not null
            } lane
            || _session.Project?.EventInstruments
                .SingleOrDefault(value => value.Id == instrumentId)?
                .SubVoices.SingleOrDefault(value => value.Id == lane.SubVoiceId) is not SubVoice voice)
        {
            return;
        }

        int pointCount = voice.Events.CreateQuerySnapshot().EnumerateAll().Count(value =>
            TemplateEventMidiTargets.Enumerate(value).Contains(target));
        string label = TemplateEventMidiTargets.Format(target);
        if (MessageDialog.Show(
                this,
                pointCount == 0
                    ? $"Delete the '{label}' event lane?"
                    : $"Delete the '{label}' event lane and its {pointCount} event point(s)?",
                "Delete SubVoice Event Lane",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        StartWorkspaceEdit(ProjectDomainEditCommands.DeleteSubVoiceEventLane(
                instrumentId,
                lane.SubVoiceId,
                target,
                nonEmptyDeletionConfirmed: pointCount != 0),
            () => workspace.Selection.Clear());
    }

    private void OnAddParameterMappingClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace
            || workspace.ObjectId is not MidoraId instrumentId
            || _session.Project is null) return;
        EventInstrument instrument = _session.Project.EventInstruments.Single(item => item.Id == instrumentId);
        if (instrument.LogicalParameters.Count == 0 || instrument.SubVoices.Count == 0)
        {
            ShowUnavailable(
                "Add Logical Parameter Mapping",
                "Create at least one Logical Parameter and one SubVoice first.");
            return;
        }
        ParameterMappingPropertiesDialog dialog = new(instrument) { Owner = this };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous("Create Logical Parameter Mapping", () => ExecuteAndSelectCreated(
            ProjectDomainEditCommands.CreateLogicalParameterMapping(
                instrumentId,
                dialog.ParameterId,
                dialog.SubVoiceId,
                dialog.Target,
                dialog.Rounding,
                dialog.Overflow),
            workspace));
    }

    private void OnEditParameterMappingClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId,
                Selection.Primary: MidoraId mappingId
            }
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterMapping? mapping = instrument.ParameterMappings
            .FirstOrDefault(item => item.Id == mappingId);
        if (mapping is null) return;

        ParameterMappingPropertiesDialog dialog = new(instrument, mapping) { Owner = this };
        if (ShowModalDialog(dialog) != true) return;
        RunSynchronous(
            "Update Logical Parameter Mapping",
            () => _session.Execute(
                new SequentialProjectEditCommand(
                    "Update Logical Parameter Mapping",
                    [
                        _ => ProjectDomainEditCommands.UpdateLogicalParameterMappingRoute(
                            instrumentId,
                            mapping.Id,
                            dialog.ParameterId,
                            dialog.SubVoiceId,
                            dialog.Target),
                        _ => ProjectDomainEditCommands.UpdateLogicalParameterMappingTargetSettings(
                            instrumentId,
                            mapping.Id,
                            dialog.Rounding,
                            dialog.Overflow)
                    ])));
    }

    private void OnMoveParameterMappingClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                Tag: string directionText,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    Selection.Primary: MidoraId mappingId
                }
            }
            || !int.TryParse(directionText, out int direction)
            || _session.Project is not MidoraProject project)
        {
            return;
        }
        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
        LogicalParameterMapping? mapping = instrument.ParameterMappings
            .FirstOrDefault(item => item.Id == mappingId);
        if (mapping is null) return;
        int current = instrument.ParameterMappings.IndexOf(mapping);
        int target = Math.Clamp(current + direction, 0, instrument.ParameterMappings.Count - 1);
        RunSynchronous(
            "Reorder Logical Parameter Mapping",
            () => _session.Execute(
                ProjectDomainEditCommands.ReorderLogicalParameterMapping(
                    instrumentId,
                    mapping.Id,
                    target)));
    }

    private void OnAddMappingStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } workspace
            }
            || workspace.MappingChains.Count == 0)
        {
            ShowUnavailable("Add Mapping Step", "Create a Logical Parameter Mapping or Template Event first.");
            return;
        }
        if (_session.Project is not MidoraProject project)
        {
            return;
        }
        EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
        MidoraId? preferredChainId = workspace.Selection.Primary is MidoraId selectedId
            ? workspace.MappingChains.FirstOrDefault(chain => chain.Id == selectedId)?.Id
                ?? workspace.MappingSteps.FirstOrDefault(step => step.Id == selectedId)?.ChainId
            : null;
        MidoraId chainId = preferredChainId ?? workspace.MappingChains[0].Id;
        ObjectPropertiesViewModel properties =
            ObjectPropertiesProjection.CreateMappingStepCreationProperties(
                instrument,
                workspace,
                chainId);
        ObjectPropertiesDialog dialog = new(
            properties,
            values =>
            {
                ExecuteAndSelectCreated(
                    ObjectPropertiesProjection.CreateMappingStepCreationCommand(
                        instrument,
                        values),
                    workspace);
                return true;
            })
        { Owner = this };
        _ = ShowModalDialog(dialog);
    }

    private void OnMoveMappingStepClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                Tag: string directionText,
                DataContext: InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId,
                    SelectedMappingStepId: MidoraId stepId
                } workspace
            }
            || !int.TryParse(directionText, out int direction)
            || workspace.MappingSteps.FirstOrDefault(item => item.Id == stepId) is not MappingStepListItem item)
        {
            return;
        }
        MappingChainListItem chain = workspace.MappingChains.Single(value => value.Id == item.ChainId);
        int target = Math.Clamp(item.Index + direction, 0, Math.Max(0, chain.StepCount - 1));
        RunSynchronous("Reorder Mapping Step", () => _session.Execute(
            ProjectDomainEditCommands.ReorderMappingStep(
                instrumentId,
                item.ChainId,
                item.Id,
                target)));
    }

    private void OnAddEnvelopeClick(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel
            {
                ObjectId: MidoraId instrumentId
            } workspace
            || _session.Project?.EventInstruments.FirstOrDefault(
                value => value.Id == instrumentId) is not EventInstrument instrument) return;
        string name = UniqueName(
            "Envelope Preset",
            instrument.Envelopes.Select(value => value.Name ?? string.Empty));
        ObjectPropertiesViewModel properties =
            ObjectPropertiesProjection.CreateEnvelopeCreationProperties(name);
        ObjectPropertiesDialog dialog = new(
            properties,
            values =>
            {
                ExecuteAndSelectCreated(
                    ObjectPropertiesProjection.CreateEnvelopeCreationCommand(
                        instrumentId,
                        values),
                    workspace);
                return true;
            })
        { Owner = this };
        _ = ShowModalDialog(dialog);
    }

    private static IProjectEditCommand CreateTemplateEventCommand(
        MidoraId instrumentId,
        MidoraId voiceId,
        TemplateEventKind kind,
        long tick,
        int rootNote) => kind switch
        {
            TemplateEventKind.Note => ProjectDomainEditCommands.CreateTemplateNote(
                instrumentId, voiceId, tick, 48, rootNote, 100),
            TemplateEventKind.ControlChange => ProjectDomainEditCommands.CreateTemplateControlChange(
                instrumentId, voiceId, tick, 1, 0),
            TemplateEventKind.Bank => ProjectDomainEditCommands.CreateTemplateBank(
                instrumentId, voiceId, tick, 0, 0),
            TemplateEventKind.Program => ProjectDomainEditCommands.CreateTemplateProgram(
                instrumentId, voiceId, tick, 0),
            TemplateEventKind.PitchBend => ProjectDomainEditCommands.CreateTemplatePitchBend(
                instrumentId, voiceId, tick, 0),
            TemplateEventKind.RegisteredParameter => ProjectDomainEditCommands.CreateTemplateRegisteredParameter(
                instrumentId, voiceId, tick, 0, 0),
            TemplateEventKind.NonRegisteredParameter => ProjectDomainEditCommands.CreateTemplateNonRegisteredParameter(
                instrumentId, voiceId, tick, 0, 0),
            TemplateEventKind.PitchBendRange => ProjectDomainEditCommands.CreateTemplatePitchBendRange(
                instrumentId, voiceId, tick, 2, 0),
            _ => throw new InvalidOperationException("Unsupported Template Event kind.")
        };

    private static IProjectEditCommand CreateTemplateEventCommand(
        MidoraId instrumentId,
        MidoraId voiceId,
        MidiValueTarget target,
        long tick,
        int value) => target.Kind switch
        {
            MidiValueKind.ControlChange => ProjectDomainEditCommands.CreateTemplateControlChange(
                instrumentId, voiceId, tick, target.Number, Math.Clamp(value, 0, 127)),
            MidiValueKind.BankMsb => ProjectDomainEditCommands.CreateTemplateBank(
                instrumentId, voiceId, tick, Math.Clamp(value, 0, 127), null),
            MidiValueKind.BankLsb => ProjectDomainEditCommands.CreateTemplateBank(
                instrumentId, voiceId, tick, null, Math.Clamp(value, 0, 127)),
            MidiValueKind.Program => ProjectDomainEditCommands.CreateTemplateProgram(
                instrumentId, voiceId, tick, Math.Clamp(value, 0, 127)),
            MidiValueKind.PitchBend => ProjectDomainEditCommands.CreateTemplatePitchBend(
                instrumentId, voiceId, tick, Math.Clamp(value, -8192, 8191)),
            MidiValueKind.RegisteredParameter => ProjectDomainEditCommands.CreateTemplateRegisteredParameter(
                instrumentId, voiceId, tick, target.Number, Math.Clamp(value, 0, 16_383)),
            MidiValueKind.NonRegisteredParameter => ProjectDomainEditCommands.CreateTemplateNonRegisteredParameter(
                instrumentId, voiceId, tick, target.Number, Math.Clamp(value, 0, 16_383)),
            MidiValueKind.PitchBendRangeSemitones => ProjectDomainEditCommands.CreateTemplatePitchBendRange(
                instrumentId, voiceId, tick, Math.Clamp(value, 0, 127), 0),
            MidiValueKind.PitchBendRangeCents => ProjectDomainEditCommands.CreateTemplatePitchBendRange(
                instrumentId, voiceId, tick, 2, Math.Clamp(value, 0, 99)),
            _ => throw new InvalidOperationException("Unsupported MIDI event target.")
        };

    private static IProjectEditCommand UpdateTemplateEventTargetCommand(
        MidoraId instrumentId,
        MidoraId voiceId,
        TemplateEvent template,
        MidiValueTarget target,
        long tick,
        int value) => target.Kind switch
        {
            MidiValueKind.ControlChange => ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrumentId, voiceId, template.Id, tick, target.Number, value),
            MidiValueKind.BankMsb => ProjectDomainEditCommands.UpdateTemplateBank(
                instrumentId, voiceId, template.Id, tick, value,
                template.HasBankLsb ? template.SecondaryValue : null),
            MidiValueKind.BankLsb => ProjectDomainEditCommands.UpdateTemplateBank(
                instrumentId, voiceId, template.Id, tick,
                template.HasBankMsb ? template.Value : null, value),
            MidiValueKind.Program => ProjectDomainEditCommands.UpdateTemplateProgram(
                instrumentId, voiceId, template.Id, tick, value),
            MidiValueKind.PitchBend => ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrumentId, voiceId, template.Id, tick, value),
            MidiValueKind.RegisteredParameter => ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrumentId, voiceId, template.Id, tick, target.Number, value),
            MidiValueKind.NonRegisteredParameter => ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrumentId, voiceId, template.Id, tick, target.Number, value),
            MidiValueKind.PitchBendRangeSemitones => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrumentId, voiceId, template.Id, tick, value, template.SecondaryValue),
            MidiValueKind.PitchBendRangeCents => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrumentId, voiceId, template.Id, tick, template.Value, value),
            _ => throw new InvalidOperationException("Unsupported MIDI event target.")
        };

    private async void OnCompileClick(object sender, RoutedEventArgs e)
    {
        if (!_session.HasProject) return;
        bool completed = await RunOperationAsync(
            "Compile Project",
            async () => _ = await _session.CompileProjectAsync(),
            DesktopTaskLockLevel.ProjectEdit);
        if (completed)
        {
            bool failed = _session.ErrorCount != 0;
            bool hasIssues = failed || _session.WarningCount != 0;
            if (hasIssues) OpenTreeWorkspace(ProjectTreeNodeKind.Diagnostics);
            _session.SetStatusMessage(
                failed
                    ? $"Compile completed with {_session.ErrorCount} error(s). Open Diagnostics for details."
                    : hasIssues
                        ? $"Compile succeeded with {_session.WarningCount} warning(s). Open Diagnostics for details."
                        : "Compile succeeded. The current canonical result is consumable.",
                isError: failed);
        }
    }

    private void OnDismissNoticeClick(object sender, RoutedEventArgs e) => _session.Notice = null;

    private void OnDismissStatusMessageClick(object sender, RoutedEventArgs e) =>
        _session.SetStatusMessage(null);

    private void OnStatusMessageDetailsClick(object sender, RoutedEventArgs e)
    {
        if (_session.StatusMessage is not string message) return;
        TextDetailsDialog dialog = new(
            _session.StatusMessageDetailsTitle ?? "Status Message",
            _session.StatusMessageDetails ?? message)
        {
            Owner = this
        };
        _ = ShowModalDialog(dialog);
    }

    private void OnDiagnosticListPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox listBox || e.Handled) return;
        // The shared ScrollViewer router uses pixel-sized offsets; this virtual
        // list uses item offsets, so handle the wheel before it reaches that router.
        ListBoxWheelScroll.ScrollOneItemPerNotch(listBox, e);
        e.Handled = true;
    }

    private void OnPreviousDiagnosticPageClick(object sender, RoutedEventArgs e) =>
        ChangeDiagnosticPage(sender, false);

    private void OnNextDiagnosticPageClick(object sender, RoutedEventArgs e) =>
        ChangeDiagnosticPage(sender, true);

    private void ChangeDiagnosticPage(object sender, bool next)
    {
        if (sender is not FrameworkElement { DataContext: DiagnosticsWorkspaceViewModel workspace }) return;
        _session.SelectedDiagnostic = null;
        workspace.MoveDiagnosticPage(next);
        RestoreDiagnosticsPageFocus();
    }

    private void OnGoToDiagnosticPageClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DiagnosticsWorkspaceViewModel workspace }
            || !workspace.GoToDiagnosticPage()) return;
        _session.SelectedDiagnostic = null;
        RestoreDiagnosticsPageFocus();
    }

    private void OnDiagnosticPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnGoToDiagnosticPageClick(sender, e);
    }

    private void RestoreDiagnosticsPageFocus()
    {
        if (_session.ActiveWorkspace is DiagnosticsWorkspaceViewModel)
            FindWorkspaceElement<FrameworkElement>("DiagnosticsWorkspaceFocusTarget")?.Focus();
    }

    private void OnDiagnosticDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DiagnosticRow diagnostic })
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(
                () => NavigateToDiagnostic(diagnostic),
                DispatcherPriority.Normal);
        }
    }

    private void OnNavigateDiagnosticClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            NavigateToDiagnostic(diagnostic);
        }
    }

    private void NavigateToDiagnostic(DiagnosticRow diagnostic)
    {
        if (!RunSynchronous("Navigate to Diagnostic", () => _session.NavigateToDiagnostic(diagnostic))) return;
        SourceReference source = diagnostic.SourceReference;
        if (source.EventInstrumentId != default && source.MappingFunctionId != default)
        {
            ShowMappingFunctionDialog(source.EventInstrumentId, source.MappingFunctionId);
        }
    }

    private void OnCopyDiagnosticMessageClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            Clipboard.SetText(diagnostic.Message);
        }
    }

    private void OnCopyDiagnosticCodeClick(object sender, RoutedEventArgs e)
    {
        if (GetDiagnosticCommandTarget(sender) is DiagnosticRow diagnostic)
        {
            Clipboard.SetText(diagnostic.Code);
        }
    }

    private DiagnosticRow? GetDiagnosticCommandTarget(object sender)
    {
        if (sender is MenuItem menuItem
            && ItemsControl.ItemsControlFromItemContainer(menuItem) is ContextMenu contextMenu
            && contextMenu.PlacementTarget is ListBox listBox
            && listBox.SelectedItem is DiagnosticRow selected)
        {
            return selected;
        }
        return _session.SelectedDiagnostic;
    }

    private void OnCancelTaskClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DesktopTaskViewModel task }
            || !task.CanRequestCancel)
        {
            return;
        }
        if (task.LockLevel == DesktopTaskLockLevel.FullApplication
            && MessageDialog.Show(
                this,
                "Cancel audio rendering? Midora will stop at a safe boundary, finalize cleanup, and will not publish incomplete output files.",
                "Cancel Audio Rendering",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        task.RequestCancel();
    }

    private async void OnMidiExportClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not MidoraProject project) return;
        if (!StopPlaybackForProjectCommand("MIDI Export")) return;
        string? initialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.MidiExport)
            ?? (_session.Persistence?.CurrentProjectPath is string currentPath
            ? Path.GetDirectoryName(currentPath)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        MidiExportDialog dialog = new(
            project,
            initialDirectory)
        { Owner = this };
        if (ShowModalDialog(dialog) != true || dialog.Options is null) return;
        RecordRecentDirectory(
            RecentDirectoryPurpose.MidiExport,
            dialog.Options.OutputDirectory);

        PreparedDesktopMidiExport? prepared = null;
        if (!await RunOperationAsync(
                "Prepare MIDI Export",
                () => Task.Run(() => prepared = _session.PrepareMidiExport(dialog.Options))))
        {
            return;
        }
        if (prepared is null) return;
        if (!prepared.Succeeded)
        {
            string compile = string.Join("\n", prepared.Compilation.Diagnostics.Take(12)
                .Select(item => $"{item.Severity} {item.Code}: {item.Message}"));
            string planning = string.Join("\n", prepared.OutputPlan.Diagnostics.Take(12)
                .Select(item => $"{item.Code}: {item.Message}"));
            ShowError("MIDI Export Plan Failed", string.Join("\n", new[] { compile, planning }.Where(value => value.Length != 0)));
            return;
        }

        string preview = string.Join("\n", prepared.OutputPlan.Targets.Take(16).Select(target =>
            $"• {target.FullPath}{(target.ExistedAtFreeze ? "  [EXISTS]" : string.Empty)}"));
        if (prepared.OutputPlan.Targets.Count > 16)
        {
            preview += $"\n… and {prepared.OutputPlan.Targets.Count - 16} more target(s)";
        }
        bool overwrite = prepared.OutputPlan.RequiresOverwriteAuthorization;
        MessageBoxResult confirmation = MessageDialog.Show(
            this,
            $"Frozen MIDI export paths:\n\n{preview}\n\n" +
            (overwrite
                ? "One or more targets already exist. Choose Yes to authorize overwriting exactly these frozen paths."
                : "Choose OK to start the export."),
            "Confirm MIDI Export",
            overwrite ? MessageBoxButton.YesNo : MessageBoxButton.OKCancel,
            overwrite ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (overwrite ? confirmation != MessageBoxResult.Yes : confirmation != MessageBoxResult.OK) return;

        MidiExportTaskResult? result = null;
        using DispatcherCoalescingProgress<MidiExportProgress> progress = new(
            Dispatcher, TimeSpan.FromMilliseconds(100), value =>
            {
                if (_session.ActiveForegroundTask is not DesktopTaskViewModel active) return;
                active.SetCancellationAvailable(value.Phase != "Finalizing");
                string detail = $"{value.Phase}: {value.FileName}";
                if (value.Encoding is { } encoded)
                {
                    detail += $" · MTrk {encoded.TrackIndex + 1}/{encoded.TrackCount} · " +
                        $"{encoded.EventCount:N0} events · {encoded.DataByteCount:N0} bytes";
                    if (encoded.PaddingEventsRequired > 0)
                        detail += $" · Timing padding {encoded.PaddingEventsWritten:N0}/{encoded.PaddingEventsRequired:N0}";
                }
                // No invented event total or extra scan just to obtain a percentage.
                _session.ReportTask(active, detail, null);
            });
        bool completed = await RunOperationAsync(
            "MIDI Export",
            async cancellationToken => result = await _session.ExecuteMidiExportAsync(prepared, overwrite, cancellationToken, progress),
            canCancel: true);
        if (!completed || result is null) return;
        string resultMessage = result.Status switch
        {
            MidiExportTaskStatus.Succeeded =>
                $"MIDI export completed.\n\n{result.Output?.Items.Count ?? 0} artifact(s) were published." +
                (result.PaddingSummary.HasPadding ? $"\n\nInfo: {result.PaddingSummary.Message}" : ""),
            MidiExportTaskStatus.Cancelled => "MIDI export was cancelled. No uncommitted target was published.",
            _ => result.OutputFailure?.Message
                ?? string.Join("\n", result.ArtifactDiagnostics.Select(item =>
                    $"{item.Diagnostic.Code}: {item.Diagnostic.Message}"))
                ?? "MIDI export failed."
        };
        MessageDialog.Show(
            this,
            resultMessage,
            "MIDI Export",
            MessageBoxButton.OK,
            result.Status == MidiExportTaskStatus.Succeeded ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private async void OnAudioRenderClick(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null) return;
        if (!StopPlaybackForProjectCommand("Render Audio")) return;
        if (_preferences.GetEnabledSoundFontPaths().Length == 0)
        {
            ShowUnavailable(
                "Render Audio",
                "Audio rendering requires at least one enabled application SoundFont in Preferences.");
            return;
        }
        string initialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.AudioRender)
            ?? (_session.Persistence?.CurrentProjectPath is string currentPath
            ? Path.GetDirectoryName(currentPath)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        string currentStem = _session.Persistence?.CurrentProjectPath is string projectPath
            ? Path.GetFileNameWithoutExtension(projectPath)
            : string.Empty;
        string suggested = AudioRenderOutputPlanner.SuggestWholeMixFileName(
            _session.Project.Metadata.ProjectName,
            currentStem);
        AudioRenderDialog dialog = new(
            _session.Project,
            initialDirectory,
            suggested)
        {
            Owner = this
        };
        if (ShowModalDialog(dialog) != true || dialog.Options is null) return;
        RecordRecentDirectory(
            RecentDirectoryPurpose.AudioRender,
            Directory.Exists(dialog.Options.OutputPath)
                ? dialog.Options.OutputPath
                : Path.GetDirectoryName(dialog.Options.OutputPath));

        PreparedDesktopAudioRender? prepared = null;
        DesktopAudioRenderOptions options = dialog.Options;
        while (true)
        {
            if (_operationInProgress)
            {
                _session.SetStatusMessage(
                    "Another foreground task is already running. Midora does not queue foreground tasks.",
                    isError: true);
                return;
            }
            _operationInProgress = true;
            DesktopTaskViewModel task = _session.BeginTask(
                "Prepare Audio Render",
                canCancel: true,
                DesktopTaskLockLevel.FullApplication);
            try
            {
                prepared = await _session.PrepareAudioRenderAsync(options, task.CancellationToken);
                _session.CompleteTask(task, "Succeeded");
                break;
            }
            catch (OperationCanceledException) when (task.CancellationToken.IsCancellationRequested)
            {
                _session.CompleteTask(task, "Cancelled", "Cancelled by user.");
                return;
            }
            catch (Exception exception)
            {
                _session.CompleteTask(task, "Failed", exception.Message);
                ShowError("Prepare Audio Render", exception.Message);
                return;
            }
            finally
            {
                _operationInProgress = false;
            }
        }
        if (prepared is null) return;
        await using (prepared)
        {
            if (!prepared.Succeeded)
            {
                string compilation = string.Join("\n", prepared.Compilation.Diagnostics.Take(12)
                    .Select(item => $"{item.Severity} {item.Code}: {item.Message}"));
                string planning = string.Join("\n", prepared.OutputPlan.Diagnostics.Take(12)
                    .Select(item => $"{item.Severity} {item.Code}: {item.Message}"));
                ShowError("Audio Render Plan Failed", string.Join("\n", new[] { compilation, planning }.Where(value => value.Length != 0)));
                return;
            }

            string preview = string.Join("\n", prepared.OutputPlan.Targets.Take(16).Select(target =>
                $"• {target.FullPath}{(target.ExistedAtFreeze ? "  [EXISTS]" : string.Empty)}"));
            if (prepared.OutputPlan.Targets.Count > 16)
            {
                preview += $"\n… and {prepared.OutputPlan.Targets.Count - 16} more target(s)";
            }
            bool overwrite = prepared.OutputPlan.RequiresOverwriteAuthorization;
            MessageBoxResult confirmation = MessageDialog.Show(
                this,
                $"Frozen audio render paths:\n\n{preview}\n\n" +
                (overwrite
                    ? "One or more targets already exist. Choose Yes to authorize overwriting exactly these frozen paths."
                    : "Choose OK to start rendering."),
                "Confirm Audio Render",
                overwrite ? MessageBoxButton.YesNo : MessageBoxButton.OKCancel,
                overwrite ? MessageBoxImage.Warning : MessageBoxImage.Information);
            if (overwrite ? confirmation != MessageBoxResult.Yes : confirmation != MessageBoxResult.OK) return;

            AudioRenderTaskResult? result = null;
            using DispatcherCoalescingProgress<AudioRenderTaskProgress> progress = new(
                Dispatcher,
                TimeSpan.FromMilliseconds(100),
                value =>
                {
                    string detail = $"{value.Status}: output {Math.Max(0, value.CurrentOutputIndex + 1)}/{value.OutputCount}, {value.ProcessedFrameCount:N0}/{value.TotalFrameCount:N0} frames";
                    DesktopTaskViewModel? active = _session.ActiveForegroundTask;
                    if (active is not null)
                    {
                        active.SetCancellationAvailable(value.Status is not AudioRenderTaskStatus.Finalizing
                            and not AudioRenderTaskStatus.Completed
                            and not AudioRenderTaskStatus.CompletedWithErrors
                            and not AudioRenderTaskStatus.Failed
                            and not AudioRenderTaskStatus.Cancelled);
                        double? fraction = value.TotalFrameCount > 0
                            ? value.ProcessedFrameCount / (double)value.TotalFrameCount
                            : null;
                        _session.ReportTask(active, detail, fraction);
                    }
                });
            bool completed = await RunOperationAsync(
                "Render Audio",
                async cancellationToken => result = await _session.ExecuteAudioRenderAsync(
                    prepared,
                    overwrite,
                    progress,
                    cancellationToken),
                canCancel: true,
                lockLevel: DesktopTaskLockLevel.FullApplication);
            _session.SetStatusMessage(null);
            if (!completed || result is null) return;
            string message = AudioRenderResultFormatter.Format(result);
            MessageDialog.Show(
                this,
                message,
                "Audio Render",
                MessageBoxButton.OK,
                result.Status == AudioRenderTaskStatus.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) => OpenAboutDialog();

    private void OpenAboutDialog()
    {
        AboutDialog dialog = new();
        _ = ShowModalDialog(dialog);
    }

    private void ShowUnavailable(string title, string message) =>
        _session.SetStatusMessage($"{title}: {message}", isError: true);

    private async Task<Exception?> InitializeAudioWorkerForActiveProjectAsync(
        CancellationToken cancellationToken)
    {
        if (!_session.HasEnabledSoundFonts)
        {
            return null;
        }
        _session.ActiveForegroundTask?.Report("Preparing audio Worker");
        return await _session.TryInitializeAudioWorkerAsync(cancellationToken);
    }

    private void OnSegmentLowerEditorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        OnInstrumentLaneTabSelected(sender, e);
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not TabControl { SelectedItem: TabItem { Header: "Parameter Lane" } }
            || _session.ActiveWorkspace is not TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Segment
            } workspace)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!ReferenceEquals(_session.ActiveWorkspace, workspace)) return;
                TimelineSurface? timeline = FindWorkspaceElement<TimelineSurface>("ParameterLanes");
                if (timeline is { IsVisible: true, IsEnabled: true, Focusable: true })
                {
                    timeline.Focus();
                }
            }));
    }

    private void OnSubVoiceLowerEditorSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        OnInstrumentLaneTabSelected(sender, e);
        if (!ReferenceEquals(e.OriginalSource, sender)
            || sender is not TabControl { SelectedItem: TabItem { Header: "Event Lane" } }
            || _session.ActiveWorkspace is not InstrumentWorkspaceViewModel workspace)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (!ReferenceEquals(_session.ActiveWorkspace, workspace)) return;
                TimelineSurface? timeline = FindWorkspaceElement<TimelineSurface>("SubVoiceEvents");
                if (timeline is { IsVisible: true, IsEnabled: true, Focusable: true })
                {
                    timeline.Focus();
                }
            }));
    }

    private void ReportAudioWorkerInitializationFailure(Exception? failure)
    {
        if (failure is null)
        {
            return;
        }
        const string summary =
            "The Project is available, but the audio Worker could not be initialized. "
            + "Playback will retry initialization when started.";
        _session.SetStatusMessage(
            summary,
            isError: true,
            details: failure.ToString(),
            detailsTitle: "Audio Worker Initialization");
        ShowError(
            "Audio Worker Initialization",
            $"{summary}\n\n{failure.Message}");
    }

    private Task<bool> RunOperationAsync(
        string title,
        Func<Task> operation,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.MainWindow) =>
        RunOperationAsync(title, _ => operation(), canCancel: false, lockLevel: lockLevel);

    private async Task<bool> RunOperationAsync(
        string title,
        Func<CancellationToken, Task> operation,
        bool canCancel,
        DesktopTaskLockLevel lockLevel = DesktopTaskLockLevel.MainWindow,
        Func<Exception, string?>? handledException = null)
    {
        if (_operationInProgress)
        {
            _session.SetStatusMessage("Another foreground task is already running. Midora does not queue foreground tasks.", isError: true);
            return false;
        }
        _operationInProgress = true;
        DesktopTaskViewModel task = _session.BeginTask(title, canCancel, lockLevel);
        if (lockLevel >= DesktopTaskLockLevel.MainWindow)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (task.CanRequestCancel) TaskLockCancelButton.Focus();
                else TaskLockOverlay.Focus();
            }, DispatcherPriority.Input);
        }
        try
        {
            await operation(task.CancellationToken);
            _session.CompleteTask(task, "Succeeded");
            return true;
        }
        catch (OperationCanceledException) when (task.CancellationToken.IsCancellationRequested)
        {
            _session.CompleteTask(task, "Cancelled", "Cancelled by user.");
            return false;
        }
        catch (Exception exception)
        {
            string? handledDetail = handledException?.Invoke(exception);
            if (handledDetail is not null)
            {
                _session.CompleteTask(task, "Needs input", handledDetail);
                return false;
            }
            _session.CompleteTask(task, "Failed", exception.Message);
            ShowError(title, exception.Message);
            return false;
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private bool RunSynchronous(string title, Action operation)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"{title}: {exception.Message}", isError: true);
            return false;
        }
    }

    private static string? TrySubmitDialogEdit(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    internal bool PrepareForModalSurface()
    {
        if (_session.ActiveWorkspace is not InstrumentWorkspaceViewModel) return true;
        return _session.StopEventInstrumentKeyboardPreviewForEditing();
    }

    private bool? ShowModalDialog(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        WorkspaceViewModel? sourceWorkspace = _session.ActiveWorkspace;
        TimelineSurface? sourceSurface = GetFocusedTimelineSurface()
            ?? _lastTimelineCommandSurface;
        if (!PrepareForModalSurface()) return false;
        if (dialog.Owner is null && IsVisible) dialog.Owner = this;
        try
        {
            return dialog.ShowDialog();
        }
        finally
        {
            RestoreModalCommandFocus(sourceWorkspace, sourceSurface);
        }
    }

    private void RestoreModalCommandFocus(
        WorkspaceViewModel? sourceWorkspace,
        TimelineSurface? sourceSurface) =>
        QueueTimelineCommandFocus(Dispatcher, _session, sourceWorkspace, sourceSurface);

    internal static void QueueTimelineCommandFocus(
        Dispatcher dispatcher,
        DesktopSessionController session,
        WorkspaceViewModel? sourceWorkspace,
        TimelineSurface? sourceSurface)
    {
        if (sourceWorkspace is null || sourceSurface is null
            || !ReferenceEquals(sourceSurface.DataContext, sourceWorkspace)) return;
        TimelineCommandTarget target = new();
        target.Set(sourceSurface);
        // Success navigation is queued after the dialog's original-source
        // restore, at the same priority, so that the destination wins after
        // the higher-priority model refresh and bindings have completed.
        _ = dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (session.ActiveWorkspace is { } active && session.Workspaces.Contains(active))
                    target.RestoreFocus(active);
            }));
    }

    private void OnEditingSurfaceContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel)
        {
            _ = PrepareForModalSurface();
        }
    }

    private void OnMainMenuSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel)
        {
            _ = PrepareForModalSurface();
        }
    }

    private void OnAnyTabSelectionChangedForPreviewPriority(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is TabControl
            && _session.ActiveWorkspace is InstrumentWorkspaceViewModel)
        {
            _ = PrepareForModalSurface();
        }
    }

    private void ShowError(string title, string message) => MessageDialog.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private SaveFileDialog CreateProjectSaveDialog(string title) => new()
    {
        Title = title,
        Filter = "Midora Project (*.midora)|*.midora",
        AddExtension = true,
        DefaultExt = ".midora",
        InitialDirectory = ExistingRecentDirectory(RecentDirectoryPurpose.SaveAndSaveCopy),
        FileName = $"{_session.ProjectDisplayName}.midora"
    };

    private string? ExistingRecentDirectory(RecentDirectoryPurpose purpose)
    {
        string? directory = _preferences.RecentDirectories.Get(purpose);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    private void RecordRecentDirectory(RecentDirectoryPurpose purpose, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        string normalized;
        try
        {
            normalized = Path.GetFullPath(directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _session.SetStatusMessage($"The recent directory was not saved: {exception.Message}", isError: true);
            return;
        }
        ApplicationRecentDirectories recent = _preferences.RecentDirectories;
        ApplicationRecentDirectories updated = purpose switch
        {
            RecentDirectoryPurpose.OpenProject => recent with { OpenProject = normalized },
            RecentDirectoryPurpose.SaveAndSaveCopy => recent with { SaveAndSaveCopy = normalized },
            RecentDirectoryPurpose.SoundFont => recent with { SoundFont = normalized },
            RecentDirectoryPurpose.MidiExport => recent with { MidiExport = normalized },
            RecentDirectoryPurpose.AudioRender => recent with { AudioRender = normalized },
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };
        ApplicationPreferences candidate = _preferences with { RecentDirectories = updated };
        ApplicationPreferencesSaveResult saved = _preferenceStore.Save(candidate);
        if (saved.Succeeded)
        {
            _preferences = candidate;
        }
        else
        {
            _session.SetStatusMessage(
                saved.Notice?.Message ?? "The recent directory could not be saved.",
                isError: true);
        }
    }

    private void RecordRecentProject(string path)
    {
        RecentProjectsUpdateResult result = _recentProjects.RecordSuccessfulProjectActivation(path);
        if (!result.Succeeded)
        {
            _session.SetStatusMessage(
                result.Notice?.Message ?? "The Recent Projects list could not be saved.",
                isError: true);
        }
    }

    private void LoadDesktopPreferences()
    {
        ApplicationPreferencesLoadResult loaded = _preferenceStore.Load();
        _preferences = loaded.Preferences;
        DesktopUiPreferences ui = _preferences.DesktopUi;
        Width = ui.MainWindowWidth;
        Height = ui.MainWindowHeight;
        ProjectPanelColumn.Width = new GridLength(0);
        ApplyPanelVisibility();
        if (ui.MainWindowLeft is double left && ui.MainWindowTop is double top
            && left + Width >= SystemParameters.VirtualScreenLeft
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top + Height >= SystemParameters.VirtualScreenTop
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        if (ui.MainWindowMaximized)
        {
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
        if (loaded.Notice is not null)
        {
            _session.SetStatusMessage(loaded.Notice.Message, isError: true);
        }
    }

    private void ReloadInstrumentCatalogSnapshot(bool reportNoticeInStatus)
    {
        InstrumentCatalogLoadResult loaded = _instrumentCatalogStore.Load();
        _instrumentCatalog = loaded.Catalog;
        _instrumentCatalogCanPublish = loaded.CanPublish;
        _instrumentCatalogNotice = loaded.Notice;
        RefreshInstrumentCatalogResolver();
        if (reportNoticeInStatus && loaded.Notice is not null)
        {
            _session.SetStatusMessage(loaded.Notice.Message, isError: true);
        }
    }

    private void RefreshInstrumentCatalogResolver()
    {
        _instrumentCatalogResolver = new(_instrumentCatalog, _preferences.SoundFonts);
        _session.SetInstrumentCatalogResolver(_instrumentCatalogResolver);
    }

    private void SaveDesktopPreferences()
    {
        Rect bounds = RestoreBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new(Left, Top, ActualWidth, ActualHeight);
        }
        DesktopUiPreferences currentUi = _preferences.DesktopUi;
        DesktopUiPreferences ui = currentUi with
        {
            MainWindowWidth = Math.Clamp(bounds.Width, 1100, 32768),
            MainWindowHeight = Math.Clamp(bounds.Height, 680, 32768),
            MainWindowLeft = double.IsFinite(bounds.Left) ? bounds.Left : null,
            MainWindowTop = double.IsFinite(bounds.Top) ? bounds.Top : null,
            MainWindowMaximized = WindowState == WindowState.Maximized,
            ProjectPanelWidth = currentUi.ProjectPanelVisible
                ? Math.Clamp(ProjectPanelColumn.ActualWidth, 170, 360)
                : currentUi.ProjectPanelWidth
        };
        _preferences = _preferences with { DesktopUi = ui };
        ApplicationPreferencesSaveResult saved = _preferenceStore.Save(_preferences);
        if (!saved.Succeeded && saved.Notice is not null)
        {
            _session.SetStatusMessage(saved.Notice.Message, isError: true);
        }
    }

    private void ApplyPanelVisibility()
    {
        DesktopUiPreferences ui = _preferences.DesktopUi;
        ProjectPanelMenuItem.IsChecked = false;
        SnapMenuItem.IsChecked = GetActiveEditorSettings().SnapEnabled;
        FollowPlaybackMenuItem.IsChecked = ui.FollowPlayback;
        FollowPlaybackToggleButton.IsChecked = ui.FollowPlayback;

        ProjectPanelGrid.Visibility = Visibility.Collapsed;
        ProjectPanelSplitter.Visibility = Visibility.Collapsed;
        ProjectPanelColumn.MinWidth = 0;
        ProjectPanelColumn.Width = new(0);
        ProjectSplitterColumn.Width = new(0);

    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closeRequestInProgress || _operationInProgress) return;

        _closeRequestInProgress = true;
        try
        {
            if (!StopPlaybackForProjectCommand("Exit Midora")) return;
            if (!await ConfirmCloseCurrentProjectAsync()) return;

            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                new Action(Close));
        }
        catch (Exception exception)
        {
            ShowError(
                "Exit Midora",
                $"Midora could not complete the exit request: {exception.Message}");
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnTimelineAltGestureConsumed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TimelineSurface surface)
        {
            _pendingTimelineAltReleaseFocus = surface;
        }
        e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!IsAltKey(e) || _pendingTimelineAltReleaseFocus is not TimelineSurface surface)
        {
            return;
        }

        _pendingTimelineAltReleaseFocus = null;
        e.Handled = true;
        TimelineCommandTarget target = new();
        target.Set(surface);
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (IsActive) target.RestoreFocus(_session.ActiveWorkspace);
            }));
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _pendingTimelineAltReleaseFocus = null;
    }

    private static bool IsAltKey(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key is Key.LeftAlt or Key.RightAlt;
    }

    private static bool IsAltF4(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_session.IsMainWindowTaskLocked && FindVisualAncestor<InstrumentChangeLane>(Keyboard.FocusedElement as DependencyObject) is { } instrumentLane
            && instrumentLane.HandleShortcut(e)) return;
        if (IsAltF4(e))
        {
            _pendingTimelineAltReleaseFocus = null;
        }
        if (_session.IsMainWindowTaskLocked)
        {
            DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
            if (focused is null || !TaskLockOverlay.IsAncestorOf(focused))
            {
                e.Handled = true;
            }
            return;
        }
        if (_spaceStartedPlayback && e.Key == Key.Tab)
        {
            _spaceStartedPlayback = false;
        }
        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (!e.IsRepeat && !IsTransientInputSurfaceOpen())
            {
                OpenAboutDialog();
            }
            return;
        }
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int count = _session.Workspaces.Count;
            if (count != 0)
            {
                int current = _session.ActiveWorkspace is null
                    ? -1
                    : _session.Workspaces.IndexOf(_session.ActiveWorkspace);
                int direction = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
                int target = (current + direction + count) % count;
                _session.ActiveWorkspace = _session.Workspaces[target];
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F6)
        {
            WorkspaceTabs.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.P
            && Keyboard.Modifiers == ModifierKeys.Control
            && !IsTransientInputSurfaceOpen())
        {
            if (!PrepareForModalSurface())
            {
                e.Handled = true;
                return;
            }
            e.Handled = TryOpenActiveProperties();
            if (e.Handled) return;
        }
        if (Keyboard.Modifiers == ModifierKeys.None
            && !IsTextEditingFocus()
            && !IsTransientInputSurfaceOpen()
            && TryActivateTimelineTool(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space
            && Keyboard.Modifiers == ModifierKeys.None
            && (!IsPlaybackShortcutInputFocus() || (_session.IsPlaybackActive && _spaceStartedPlayback))
            && !IsTransientInputSurfaceOpen())
        {
            e.Handled = true;
            if (!e.IsRepeat)
            {
                if (_session.IsPlaybackActive)
                {
                    OnStopClick(this, new RoutedEventArgs());
                }
                else
                {
                    OnPlayClick(this, new RoutedEventArgs());
                }
            }
            return;
        }
        if (e.Key == Key.Delete && !IsTextEditingFocus())
        {
            if (!_session.CanEditProject) return;
            if (IsArrangementHeaderShortcutContext())
                OnArrangementHeaderDeleteClick(this, new RoutedEventArgs());
            else if (ProjectTree.IsKeyboardFocusWithin) DeleteSelectedTreeNode();
            else DeleteWorkspaceSelection();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (IsTextEditingFocus())
        {
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control
            && !IsTransientInputSurfaceOpen()
            && TryInvokeTimelineSelectionOperationShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.N: OnNewProjectClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O: OnOpenProjectClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S when shift: OnSaveCopyClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.S:
                OnSaveProjectClick(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.Z: OnUndoClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.Y: OnRedoClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.X: OnCutClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.C: OnCopyClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.V: OnPasteClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.A: OnSelectAllClick(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.D: OnDuplicateClick(this, new RoutedEventArgs()); e.Handled = true; break;
        }
    }

    private bool TryOpenActiveProperties()
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return false;
        if (FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject) is not null)
        {
            InvokeObjectListShortcut(Key.P, workspace);
            return true;
        }
        if (GetCachedObjectListSelection(workspace) is { IsMixed: true }) return true;
        if (workspace is TimelineWorkspaceViewModel
            {
                Mode: TimelineWorkspaceMode.Arrangement
            }
            && _arrangementHeaderShortcut is { } trackShortcut
            && ArrangementShortcutTargetExists(trackShortcut))
        {
            _ = OpenArrangementTrackProperties(trackShortcut.Kind, trackShortcut.Id);
            return true;
        }
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && _session.Project?.EventInstruments.FirstOrDefault(
                value => value.Id == instrumentWorkspace.ObjectId) is EventInstrument instrument
            && instrumentWorkspace.Selection.Primary is MidoraId selectedId)
        {
            if (instrument.LogicalParameters.Any(value => value.Id == selectedId))
            {
                OnEditLogicalParameterDefinitionClick(this, new RoutedEventArgs());
                return true;
            }
            if (instrument.ParameterMappings.Any(value => value.Id == selectedId))
            {
                OnEditParameterMappingClick(this, new RoutedEventArgs());
                return true;
            }
            if (instrument.MappingFunctions.Any(value => value.Id == selectedId))
            {
                ShowMappingFunctionDialog(instrument.Id, selectedId);
                return true;
            }
        }
        if (workspace is InstrumentWorkspaceViewModel)
        {
            return OpenInstrumentProperties();
        }
        OnEditTimelinePropertiesClick(this, new RoutedEventArgs());
        return true;
    }

    private bool TryInvokeTimelineSelectionOperationShortcut(Key key)
    {
        if (key is Key.Q or Key.T or Key.E
            && FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject) is not null
            && _session.ActiveWorkspace is { } listWorkspace)
        {
            InvokeObjectListShortcut(key, listWorkspace);
            return true;
        }
        if (key is not (Key.Q or Key.T or Key.E)
            || (GetFocusedTimelineSurface()
                ?? (FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject) is not null
                    ? _lastTimelineCommandSurface : null)) is not TimelineSurface surface)
        {
            return false;
        }

        _timelineSelectionOperationContext = ResolveTimelineSelectionOperationContext(surface);
        if (!_session.CanEditProject
            || _timelineSelectionOperationContext is not { Ids.Count: > 0 } context)
        {
            return true;
        }

        if (!PrepareForModalSurface()) return true;

        switch (key)
        {
            case Key.Q:
                OnScaleSelectionClick(this, new RoutedEventArgs());
                break;
            case Key.T when context.Kind is TimelineSelectionObjectKind.Segments
                or TimelineSelectionObjectKind.MidiSegments
                or TimelineSelectionObjectKind.MixedSegments
                or TimelineSelectionObjectKind.LogicalNotes
                or TimelineSelectionObjectKind.DirectMidiNotes
                or TimelineSelectionObjectKind.TemplateNotes:
                OnTransposeSelectionClick(this, new RoutedEventArgs());
                break;
            case Key.E:
                OnBatchEditSelectionClick(this, new RoutedEventArgs());
                break;
        }
        return true;
    }

    private bool TryActivateTimelineTool(Key key)
    {
        if (key == Key.A)
        {
            if (_session.ActiveWorkspace is not (TimelineWorkspaceViewModel
                or InstrumentWorkspaceViewModel))
            {
                return false;
            }
            TimelineEditorSettings settings = GetFocusedEditorSettings();
            settings.SnapEnabled = !settings.SnapEnabled;
            SnapMenuItem.IsChecked = settings.SnapEnabled;
            return true;
        }
        TimelineToolMode? mode = key switch
        {
            Key.D => TimelineToolMode.Draw,
            Key.S => TimelineToolMode.Select,
            Key.E => TimelineToolMode.Erase,
            _ => null
        };
        if (mode is not TimelineToolMode resolved) return false;
        switch (_session.ActiveWorkspace)
        {
            case TimelineWorkspaceViewModel timeline:
                timeline.ToolMode = resolved;
                return true;
            case InstrumentWorkspaceViewModel instrument:
                instrument.ToolMode = resolved;
                return true;
            default:
                return false;
        }
    }

    private void StartDetachedWorkspaceEdit(IProjectEditCommand command, Action? afterPublication = null) =>
        StartWorkspaceEdit(new SequentialProjectEditCommand(command.Name, [_ => command]), afterPublication);

    private async void StartWorkspaceEdit(IProjectEditCommand command, Action? afterPublication = null)
    {
        try { await ExecuteWorkspaceEditAsync(command, afterPublication); }
        catch (Exception exception) { _session.SetStatusMessage($"{command.Name}: {exception.Message}", isError: true); }
    }

    private void OnClipboardSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopSessionController.ActiveWorkspace)) ClearTimelineCommandTargets();
        if (_clipboardDocument is null || ReferenceEquals(_clipboardDocument, _session.Document)) return;
        _projectClipboard?.Dispose();
        _projectClipboard = null;
        _clipboardDocument = null;
    }

    private void ClearTimelineCommandTargets()
    {
        _lastTimelineCommandTarget.Clear();
        _pendingTimelineAltTarget.Clear();
        _timelineSelectionOperationContext = null;
        _timelineQuantizeOperationContext = null;
    }

    private async void StartClipboardTransfer(ProjectDocumentSession document,
        Func<ProjectObjectClipboardPayload> copy, IProjectEditCommand? delete = null)
    {
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace) return;
        try { await RunClipboardTransferAsync(document, workspace, copy, delete); }
        catch (Exception exception) { _session.SetStatusMessage($"Clipboard: {exception.Message}", isError: true); }
    }

    private async void CutOrCopyProjectSelection(bool cut)
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (IsArrangementHeaderShortcutContext()) { CopyArrangementHeader(cut); return; }
        if (CutOrCopySelectedLogicalTrack(cut)) return;
        if (!cut && TryGetSelectedEventInstrumentId(out _)) { _ = CopySelectedEventInstrument(); return; }
        if (_session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0 || cut && !_session.CanEditProject) return;
        try
        {
            if (IsObjectListSelectionCommandContext(workspace)
                && await ReadObjectListSelectionAsync(workspace) is { } listSelection)
            {
                if (!listSelection.CanCopyOrCut) return;
                if (listSelection.InstrumentMembers.Count != 0)
                { RunInstrumentAction(workspace, cut ? "Cut" : "Copy"); return; }
            }
            ClipboardTransferRequest request = ResolveWorkspaceClipboardRequest(document, project, workspace);
            await RunClipboardTransferAsync(document, workspace, request.Copy, cut ? request.Delete : null);
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"{(cut ? "Cut" : "Copy")} Project Objects: {exception.Message}", isError: true);
        }
    }

    private sealed record ClipboardTransferRequest(Func<ProjectObjectClipboardPayload> Copy, IProjectEditCommand Delete);

    private static ClipboardTransferRequest ResolveWorkspaceClipboardRequest(
        ProjectDocumentSession document, MidoraProject project, WorkspaceViewModel workspace,
        CompressedMidoraIdSet? subset = null, MidoraId? subsetPrimary = null)
    {
        // Freeze the immutable ID root. All expensive copying belongs to the background
        // preparation; the Dispatcher only resolves the primary object's owner.
        IReadOnlyCollection<MidoraId> ids = subset ?? workspace.Selection.SharedIds;
        MidoraId primary = subsetPrimary ?? workspace.Selection.Primary
            ?? throw new InvalidOperationException("The selection has no primary object.");
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            if (timeline.Mode == TimelineWorkspaceMode.Arrangement)
            {
                return new(() => ProjectObjectClipboard.CopyArrangementSegments(document, ids, primary),
                    ProjectDomainEditCommands.DeleteArrangementSegments(ids));
            }
            if (timeline.Mode == TimelineWorkspaceMode.Conductor)
                return new(() => ProjectObjectClipboard.CopyConductorEvents(document, ids),
                    ProjectDomainEditCommands.DeleteConductorEvents(ids));
            if (timeline.ObjectId is not MidoraId segmentId)
                throw new InvalidOperationException("The Segment no longer exists.");
            if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } midi)
            {
                if (midi.Segment.Notes.TryGetById(primary, out _))
                    return new(() => ProjectObjectClipboard.CopyDirectMidiNotes(document, segmentId, ids),
                        ProjectDomainEditCommands.DeleteDirectMidiNotes(segmentId, ids));
                if (midi.Segment.ChannelEvents.TryGetById(primary, out _))
                    return new(() => ProjectObjectClipboard.CopyDirectMidiEvents(document, segmentId, ids),
                        ProjectDomainEditCommands.DeleteDirectMidiEvents(segmentId, ids));
                return new(() => ProjectObjectClipboard.CopyOpaqueMidiEvents(document, segmentId, ids),
                    ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segmentId, ids));
            }
            Segment segment = TimelineWorkspaceViewModel.FindSegment(project, segmentId)?.Segment
                ?? throw new InvalidOperationException("The Segment no longer exists.");
            if (segment.Notes.TryGetById(primary, out _))
                return new(() => ProjectObjectClipboard.CopyLogicalNotes(document, segmentId, ids),
                    ProjectDomainEditCommands.DeleteLogicalNotes(segmentId, ids));
            foreach (LogicalParameterLane lane in segment.ParameterLanes)
            {
                MidoraId laneId = lane.Id;
                if (ids.Count == 1 && lane.Id == primary)
                    return new(() => ProjectObjectClipboard.CopyLogicalParameterLane(document, segmentId, laneId),
                        ProjectDomainEditCommands.DeleteLogicalParameterLane(segmentId, laneId, deletionConfirmed: true));
                if (lane.Points.TryGetById(primary, out _))
                    return new(() => ProjectObjectClipboard.CopyLogicalParameterLaneContent(document, segmentId, laneId, ids),
                        ProjectDomainEditCommands.DeleteLogicalParameterPoints(segmentId, laneId, ids));
            }
        }
        if (workspace is InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId })
        {
            EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
            if (ids.Count == 1)
            {
                if (instrument.SubVoices.Any(item => item.Id == primary))
                    return new(() => ProjectObjectClipboard.CopySubVoice(document, instrumentId, primary),
                        ProjectDomainEditCommands.DeleteSubVoice(instrumentId, primary, nonEmptyDeletionConfirmed: true));
                if (instrument.LogicalParameters.Any(item => item.Id == primary))
                    return new(() => ProjectObjectClipboard.CopyLogicalParameterDefinition(document, instrumentId, primary),
                        ProjectDomainEditCommands.DeleteLogicalParameter(instrumentId, primary, referencedDeletionConfirmed: true));
                if (instrument.ParameterMappings.Any(item => item.Id == primary))
                    return new(() => ProjectObjectClipboard.CopyLogicalParameterMapping(document, instrumentId, primary),
                        ProjectDomainEditCommands.DeleteLogicalParameterMapping(instrumentId, primary, deletionConfirmed: true));
                if (instrument.Envelopes.Any(item => item.Id == primary))
                    return new(() => ProjectObjectClipboard.CopyEnvelopePreset(document, instrumentId, primary),
                        ProjectDomainEditCommands.DeleteInstrumentEnvelope(instrumentId, primary, referencedDeletionConfirmed: true));
                if (instrument.MappingFunctions.Any(item => item.Id == primary))
                    return new(() => ProjectObjectClipboard.CopyMappingFunction(document, instrumentId, primary),
                        ProjectDomainEditCommands.DeleteMappingFunction(instrumentId, primary, referencedDeletionConfirmed: true));
                foreach (MappingChain chain in EnumerateInstrumentMappingChains(instrument))
                {
                    MidoraId chainId = chain.Id;
                    if (chain.Id == primary)
                        return new(() => ProjectObjectClipboard.CopyMappingChain(document, instrumentId, chainId),
                            ProjectDomainEditCommands.DeleteMappingChain(instrumentId, chainId, nonEmptyDeletionConfirmed: true));
                    if (chain.Any(item => item.Id == primary))
                        return new(() => ProjectObjectClipboard.CopyMappingStep(document, instrumentId, chainId, primary),
                            ProjectDomainEditCommands.DeleteMappingStep(instrumentId, chainId, primary));
                }
            }
            foreach (SubVoice voice in instrument.SubVoices)
            {
                MidoraId voiceId = voice.Id;
                if (voice.Events.TryGetById(primary, out _))
                    return new(() => ProjectObjectClipboard.CopySubVoiceTimelineEvents(document, instrumentId, voiceId, ids),
                        ProjectDomainEditCommands.DeleteTemplateEvents(instrumentId, voiceId, ids));
                foreach (ValueCurve curve in voice.Curves)
                {
                    MidoraId curveId = curve.Id;
                    if (curve.Points.TryGetById(primary, out _))
                        return new(() => ProjectObjectClipboard.CopyValueCurveContent(document, instrumentId, voiceId, curveId, ids),
                            ProjectDomainEditCommands.DeleteValueCurvePoints(instrumentId, voiceId, curveId, ids));
                }
            }
        }
        throw new InvalidOperationException("The selection cannot be copied in this Workspace.");
    }

    private async Task<bool> RunClipboardTransferAsync(
        ProjectDocumentSession document, WorkspaceViewModel workspace,
        Func<ProjectObjectClipboardPayload> copy, IProjectEditCommand? delete = null,
        CompressedMidoraIdSet? retainedSelectionIds = null)
    {
        bool cut = delete is not null;
        TimelineSurface? sourceSurface = _lastTimelineCommandSurface;
        bool completed = await RunOperationAsync(cut ? "Cut Project Objects" : "Copy Project Objects",
            async token =>
            {
                using DispatcherCoalescingProgress<TimelineEditPreparationProgress> progress = new(
                    Dispatcher, TimeSpan.FromMilliseconds(100),
                    value => _session.ActiveForegroundTask?.Report(
                        FormatTimelineEditPreparationProgress(value), value.IsIndeterminate ? null : value.OverallFraction));
                using PreparedProjectClipboardTransfer prepared = await Task.Run(
                    () =>
                    {
                        PreparedProjectClipboardTransfer transfer = ProjectObjectClipboard.PrepareTransfer(document, copy, delete, token, progress);
                        try
                        {
                            if (transfer.Deletion is not null && retainedSelectionIds is not null)
                                _session.PrepareWorkspaceSelectionForStagedEdit(transfer.Deletion, workspace, retainedSelectionIds, token);
                            return transfer;
                        }
                        catch { transfer.Dispose(); throw; }
                    }, token);
                progress.Flush();
                _session.ActiveForegroundTask!.SealCancellationBeforePublication();
                prepared.ValidatePublication(_session.Document
                    ?? throw new InvalidOperationException("The Project was closed while copying."));
                IDataObject? previousClipboard = Clipboard.GetDataObject();
                Clipboard.SetDataObject(prepared.Payload.PlainTextSummary, copy: true);
                try
                {
                    if (prepared.Deletion is not null)
                        _session.ExecutePreparedPreservingWorkspaceSelection(prepared.Deletion, workspace);
                }
                catch
                {
                    if (previousClipboard is null) Clipboard.Clear();
                    else Clipboard.SetDataObject(previousClipboard, copy: true);
                    throw;
                }
                ProjectObjectClipboardPayload? previous = _projectClipboard;
                _projectClipboard = prepared.TakePayload();
                previous?.Dispose();
                _clipboardDocument = document;
                if (cut)
                {
                    if (retainedSelectionIds is null) workspace.Selection.Clear();
                    if (workspace is InstrumentWorkspaceViewModel instrument)
                        ClearInstrumentStructureVisualSelection(instrument);
                    _session.RefreshWorkspaceSelection(workspace);
                }
                _session.SetStatusMessage($"{(cut ? "Cut" : "Copied")} {prepared.Payload.PlainTextSummary}.");
            }, canCancel: true);
        RestoreModalCommandFocus(workspace, sourceSurface);
        return completed;
    }

    private async void PasteProjectSelection()
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (IsArrangementHeaderShortcutContext()
            && TryGetArrangementHeaderContext(out ArrangementLaneDescriptor header)
            && CanPasteArrangementHeader(header))
        {
            OnArrangementHeaderPasteClick(this, new RoutedEventArgs());
            return;
        }
        if (_projectClipboard?.Kind == ProjectObjectClipboardKind.LogicalTrack
            && IsLogicalTrackShortcutContext()
            && PasteLogicalTrackClipboard(ResolveLogicalTrackPasteIndex()))
        {
            return;
        }
        if (_projectClipboard?.Kind == ProjectObjectClipboardKind.EventInstrument
            && PasteEventInstrumentClipboard())
        {
            return;
        }
        if (!_session.CanEditProject
            || _session.Document is not ProjectDocumentSession document
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || _projectClipboard is not ProjectObjectClipboardPayload payload
            || !ReferenceEquals(document, _clipboardDocument))
        {
            return;
        }

        try
        {
            if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangementPaste
                && payload.Kind is ProjectObjectClipboardKind.Segments or ProjectObjectClipboardKind.MidiSegments
                    or ProjectObjectClipboardKind.ArrangementSegments)
            {
                await PasteArrangementSegmentsAsync(arrangementPaste, document, project, payload);
                return;
            }
            if (payload.Kind == ProjectObjectClipboardKind.InstrumentChanges && InstrumentOwner(workspace) is not null)
            { RunInstrumentAction(workspace, "Paste"); return; }
            long cursor = workspace is TimelineWorkspaceViewModel timeline
                ? timeline.EditCursorTick ?? 0
                : 0;
            IProjectEditCommand command;
            MidoraId? retainedStructureSelection = null;
            if (workspace is InstrumentWorkspaceViewModel chainWorkspace
                && chainWorkspace.ObjectId is MidoraId chainInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.MappingChain)
            {
                MidoraId targetChainId = ResolveMappingChainTarget(chainWorkspace);
                MappingChainListItem target = chainWorkspace.MappingChains.Single(item => item.Id == targetChainId);
                if (target.StepCount != 0
                    && MessageDialog.Show(
                        this,
                        $"Replace all {target.StepCount} step(s) in '{target.Owner}' with the copied Mapping Chain?",
                        "Replace Mapping Chain",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                command = ProjectObjectClipboard.CreatePasteMappingChainCommand(
                    document,
                    payload,
                    chainInstrumentId,
                    targetChainId,
                    nonEmptyReplacementConfirmed: target.StepCount != 0);
            }
            else if (workspace is InstrumentWorkspaceViewModel mappingWorkspace
                && mappingWorkspace.ObjectId is MidoraId mappingInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.LogicalParameterMapping)
            {
                MidoraId targetMappingId = mappingWorkspace.Selection.Primary is MidoraId selected
                    && mappingWorkspace.ParameterMappings.Any(value => value.Id == selected)
                    ? selected
                    : throw new InvalidOperationException(
                        "Select a target Logical Parameter Mapping before pasting its configuration.");
                ParameterMappingListItem target = mappingWorkspace.ParameterMappings
                    .Single(value => value.Id == targetMappingId);
                if (target.StepCount != 0
                    && MessageDialog.Show(
                        this,
                        $"Replace all {target.StepCount} step(s) in the selected Logical Parameter Mapping?",
                        "Replace Logical Parameter Mapping",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }
                command = ProjectObjectClipboard.CreatePasteLogicalParameterMappingCommand(
                    document,
                    payload,
                    mappingInstrumentId,
                    targetMappingId,
                    nonEmptyReplacementConfirmed: target.StepCount != 0);
                retainedStructureSelection = targetMappingId;
            }
            else if (workspace is InstrumentWorkspaceViewModel stepWorkspace
                && stepWorkspace.ObjectId is MidoraId stepInstrumentId
                && payload.Kind == ProjectObjectClipboardKind.MappingStep)
            {
                (MidoraId ChainId, int InsertionIndex) target =
                    ResolveMappingStepPasteTarget(stepWorkspace);
                command = ProjectObjectClipboard.CreatePasteMappingStepCommand(
                    document,
                    payload,
                    stepInstrumentId,
                    target.ChainId,
                    target.InsertionIndex);
            }
            else command = workspace switch
            {
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                    when payload.Kind == ProjectObjectClipboardKind.Segments =>
                    ProjectObjectClipboard.CreatePasteSegmentsCommand(
                        document,
                        payload,
                        ResolveArrangementTargetTrack(project, arrangement),
                        cursor),
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement
                    when payload.Kind == ProjectObjectClipboardKind.MidiSegments =>
                    ProjectObjectClipboard.CreatePasteMidiSegmentsCommand(
                        document,
                        payload,
                        ResolveArrangementTargetMidiTrack(project, arrangement),
                        cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind is ProjectObjectClipboardKind.LogicalNotes
                    or ProjectObjectClipboardKind.DirectMidiNotes =>
                    ProjectObjectClipboard.CreatePasteNotesCommand(
                        document,
                        payload,
                        segmentId,
                        cursor,
                        TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not null),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind == ProjectObjectClipboardKind.DirectMidiEvents
                    && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not null =>
                    ProjectObjectClipboard.CreatePasteDirectMidiEventsCommand(
                        document, payload, segmentId, cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind == ProjectObjectClipboardKind.OpaqueMidiEvents
                    && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is not null =>
                    ProjectObjectClipboard.CreatePasteOpaqueMidiEventsCommand(
                        document, payload, segmentId, cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } segmentWorkspace when payload.Kind == ProjectObjectClipboardKind.LogicalParameterLaneContent =>
                    ProjectObjectClipboard.CreatePasteLogicalParameterLaneContentCommand(
                        document,
                        payload,
                        segmentId,
                        ResolveSegmentTargetLane(project, segmentWorkspace, segmentId),
                        cursor),
                TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                } when payload.Kind == ProjectObjectClipboardKind.LogicalParameterLane =>
                    ProjectObjectClipboard.CreatePasteLogicalParameterLaneCommand(document, payload, segmentId, cursor),
                TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor }
                    when payload.Kind == ProjectObjectClipboardKind.ConductorEvents =>
                    ProjectObjectClipboard.CreatePasteConductorEventsCommand(document, payload, cursor),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.SubVoice =>
                    ProjectObjectClipboard.CreatePasteSubVoiceCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveSubVoicePasteIndex(project, instrumentWorkspace, instrumentId)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.LogicalParameterDefinition =>
                    ProjectObjectClipboard.CreatePasteLogicalParameterDefinitionCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveStructurePasteIndex(
                            project.EventInstruments.Single(value => value.Id == instrumentId).LogicalParameters,
                            instrumentWorkspace.Selection.Primary,
                            value => value.Id)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.EnvelopePreset =>
                    ProjectObjectClipboard.CreatePasteEnvelopePresetCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveStructurePasteIndex(
                            project.EventInstruments.Single(value => value.Id == instrumentId).Envelopes,
                            instrumentWorkspace.Selection.Primary,
                            value => value.Id)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.MappingFunction =>
                    ProjectObjectClipboard.CreatePasteMappingFunctionCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveStructurePasteIndex(
                            project.EventInstruments.Single(value => value.Id == instrumentId).MappingFunctions,
                            instrumentWorkspace.Selection.Primary,
                            value => value.Id)),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.SubVoiceTimelineEvents =>
                    ProjectObjectClipboard.CreatePasteSubVoiceTimelineEventsCommand(
                        document,
                        payload,
                        instrumentId,
                        ResolveInstrumentTargetLane(instrumentWorkspace).SubVoiceId,
                        cursor,
                        ResolveInstrumentTargetLane(instrumentWorkspace).Target),
                InstrumentWorkspaceViewModel instrumentWorkspace
                    when instrumentWorkspace.ObjectId is MidoraId instrumentId
                    && payload.Kind == ProjectObjectClipboardKind.ValueCurveContent =>
                    CreatePasteValueCurveCommand(document, payload, instrumentWorkspace, instrumentId, cursor),
                _ => throw new InvalidOperationException(
                    $"{payload.Kind} cannot be pasted into the active Workspace selection scope.")
            };
            long firstNewStableId = project.NextStableId;
            if (!await ExecuteWorkspaceEditAsync(command)) return;
            if (workspace is InstrumentWorkspaceViewModel structureWorkspace
                && IsInstrumentStructureClipboardKind(payload.Kind))
            {
                SelectInstrumentStructurePasteResult(
                    structureWorkspace,
                    payload.Kind,
                    firstNewStableId,
                    retainedStructureSelection);
            }
            else
            {
                SelectCreatedWorkspaceObjects(workspace, firstNewStableId);
            }
            _session.SetStatusMessage($"Pasted {payload.PlainTextSummary}.");
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"Paste Project Objects: {exception.Message}", isError: true);
        }
    }

    private static MidoraId ResolveArrangementTargetTrack(
        MidoraProject project,
        TimelineWorkspaceViewModel workspace)
    {
        if (project.Tracks.Count == 0)
        {
            throw new InvalidOperationException("Create a Logical Track before pasting Segments.");
        }
        if (workspace.Selection.Primary is MidoraId segmentId
            && TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located)
        {
            return located.Track.Id;
        }
        if (workspace.GetArrangementLane(workspace.ActiveLane ?? -1) is
            { Kind: ArrangementLaneKind.LogicalTrack, ObjectId: MidoraId activeTrackId })
        {
            return activeTrackId;
        }
        return project.TracksInArrangementOrder()
            .FirstOrDefault(value => value.Kind == ArrangementTrackKind.LogicalTrack) is
        { TrackId: var first } && first != default
                    ? first
                    : project.Tracks[0].Id;
    }

    private static MidoraId ResolveArrangementTargetMidiTrack(
        MidoraProject project,
        TimelineWorkspaceViewModel workspace)
    {
        if (project.PureMidiTracks.Count == 0)
            throw new InvalidOperationException("Create a MIDI Track before pasting MIDI Segments.");
        if (workspace.Selection.Primary is MidoraId segmentId
            && TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } located)
        {
            return located.Track.Id;
        }
        if (workspace.GetArrangementLane(workspace.ActiveLane ?? -1) is
            { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId activeTrackId })
        {
            return activeTrackId;
        }
        return project.TracksInArrangementOrder()
            .FirstOrDefault(value => value.Kind == ArrangementTrackKind.PureMidiTrack) is
        { TrackId: var first } && first != default
                    ? first
                    : project.PureMidiTracks[0].Id;
    }

    private static int ResolveSubVoicePasteIndex(
        MidoraProject project,
        InstrumentWorkspaceViewModel workspace,
        MidoraId instrumentId)
    {
        EventInstrument instrument = project.EventInstruments.Single(value => value.Id == instrumentId);
        if (workspace.Selection.Primary is MidoraId selected)
        {
            int index = instrument.SubVoices.FindIndex(value => value.Id == selected);
            if (index >= 0) return index + 1;
        }
        return instrument.SubVoices.Count;
    }

    private static int ResolveStructurePasteIndex<T>(
        IReadOnlyList<T> values,
        MidoraId? selectedId,
        Func<T, MidoraId> getId)
    {
        if (selectedId is MidoraId selected)
        {
            for (int index = 0; index < values.Count; index++)
            {
                if (getId(values[index]) == selected) return index + 1;
            }
        }
        return values.Count;
    }

    private static (MidoraId ChainId, int InsertionIndex) ResolveMappingStepPasteTarget(
        InstrumentWorkspaceViewModel workspace)
    {
        MidoraId selected = workspace.Selection.Primary
            ?? throw new InvalidOperationException(
                "Select a target Mapping Chain or Mapping Step before pasting.");
        MappingStepListItem? step = workspace.MappingSteps
            .FirstOrDefault(value => value.Id == selected);
        if (step is not null) return (step.ChainId, step.Index + 1);
        MappingChainListItem? chain = workspace.MappingChains
            .FirstOrDefault(value => value.Id == selected);
        return chain is not null
            ? (chain.Id, chain.StepCount)
            : throw new InvalidOperationException(
                "The current selection is not a Mapping Chain target.");
    }

    private static bool IsInstrumentStructureClipboardKind(ProjectObjectClipboardKind kind) =>
        kind is ProjectObjectClipboardKind.SubVoice
            or ProjectObjectClipboardKind.LogicalParameterDefinition
            or ProjectObjectClipboardKind.LogicalParameterMapping
            or ProjectObjectClipboardKind.MappingChain
            or ProjectObjectClipboardKind.MappingStep
            or ProjectObjectClipboardKind.EnvelopePreset
            or ProjectObjectClipboardKind.MappingFunction;

    private void SelectInstrumentStructurePasteResult(
        InstrumentWorkspaceViewModel workspace,
        ProjectObjectClipboardKind kind,
        long firstNewStableId,
        MidoraId? retainedSelection)
    {
        MidoraId? selected = retainedSelection ?? kind switch
        {
            ProjectObjectClipboardKind.SubVoice => workspace.SubVoices
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.LogicalParameterDefinition => workspace.Parameters
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.MappingChain => workspace.MappingChains
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.MappingStep => workspace.MappingSteps
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.EnvelopePreset => workspace.Envelopes
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            ProjectObjectClipboardKind.MappingFunction => workspace.MappingFunctions
                .Select(value => value.Id).FirstOrDefault(value => value.Value >= firstNewStableId),
            _ => null
        };
        if (selected is not MidoraId id || id == default)
        {
            workspace.Selection.Clear();
        }
        else
        {
            workspace.Selection.Replace(id);
        }
        _session.RefreshWorkspaceSelection(workspace);
        if (selected is MidoraId selectedId && selectedId != default)
        {
            SetInstrumentStructureVisualSelection(workspace, selectedId);
        }
        else
        {
            ClearInstrumentStructureVisualSelection(workspace);
        }
    }

    private static MidoraId ResolveSegmentTargetLane(
        MidoraProject project,
        TimelineWorkspaceViewModel workspace,
        MidoraId segmentId)
    {
        (LogicalTrack Track, Segment Segment)? located = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
        if (located is null || located.Value.Segment.ParameterLanes.Count == 0)
        {
            throw new InvalidOperationException("Create a Logical Parameter Lane before pasting points.");
        }
        return workspace.GetActiveParameterLaneOption()?.LaneId
            ?? throw new InvalidOperationException("Select an existing Logical Parameter Lane before pasting points.");
    }

    private static InstrumentRenderLane ResolveInstrumentTargetLane(InstrumentWorkspaceViewModel workspace) =>
        workspace.GetRenderLane(workspace.ActiveRenderLaneIndex) is InstrumentRenderLane target
            ? target
            : throw new InvalidOperationException("Select the target SubVoice Event Lane before pasting.");

    private static MidoraId ResolveMappingChainTarget(InstrumentWorkspaceViewModel workspace)
    {
        MidoraId selected = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select a target Mapping Chain or Mapping Step before pasting.");
        if (workspace.MappingChains.Any(chain => chain.Id == selected)) return selected;
        return workspace.MappingSteps.FirstOrDefault(step => step.Id == selected)?.ChainId
            ?? throw new InvalidOperationException("The current selection is not a Mapping Chain target.");
    }

    private static IProjectEditCommand CreatePasteValueCurveCommand(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        InstrumentWorkspaceViewModel workspace,
        MidoraId instrumentId,
        long cursor)
    {
        InstrumentRenderLane lane = ResolveInstrumentTargetLane(workspace);
        MidoraId curveId = lane.ValueCurveId
            ?? throw new InvalidOperationException("Select a Value Curve lane before pasting curve points.");
        return ProjectObjectClipboard.CreatePasteValueCurveContentCommand(
            document, payload, instrumentId, lane.SubVoiceId, curveId, cursor);
    }

    private async void SelectAllInFocusedScope()
    {
        if (FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject)
            is { Source.Count: > 0 } objectList)
        {
            await SelectObjectListRangeAsync(objectList,
                new(0, objectList.Source.Count - 1, ModifierKeys.None, null, forceSingle: true));
            return;
        }
        if (_session.ActiveWorkspace is TimelineWorkspaceViewModel { IsConductor: true } conductor
            && FindWorkspaceElement<ConductorWorkspaceView>("ConductorWorkspace") is { } conductorView)
        {
            if (conductor.ConductorListSource is { Count: > 0 } list)
                OnConductorListSelectionRequested(conductorView.EventList,
                    new(0, list.Count - 1, ModifierKeys.None, null));
            return;
        }
        if (_session.ActiveWorkspace is not WorkspaceViewModel workspace
            || Keyboard.FocusedElement is not TimelineSurface surface
            || surface.Snapshot is null)
        {
            return;
        }
        int? lane = workspace.ActiveLane is int activeLane
            && (surface.Tag as string) == "ParameterLanes"
                ? activeLane
                : null;
        await MaterializeTimelineSelectionAsync(
            workspace,
            surface,
            surface.Snapshot,
            invert: false,
            lane);
    }

    private async Task MaterializeTimelineSelectionAsync(
        WorkspaceViewModel workspace,
        TimelineSurface surface,
        TimelineRenderSnapshot snapshot,
        bool invert,
        int? lane)
    {
        long selectionRevision = workspace.Selection.Revision;
        TimelineSelectionSnapshot current = workspace.SelectionSnapshot;
        MidoraId? currentAnchor = workspace.Selection.Anchor;
        if (current.Revision != selectionRevision)
        {
            // Selection presentation is frame-coalesced. Do not synchronously
            // resolve a potentially paged million-item selection merely to run
            // this command; allow the pending frame to publish it first.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
            current = workspace.SelectionSnapshot;
            selectionRevision = workspace.Selection.Revision;
            currentAnchor = workspace.Selection.Anchor;
            if (current.Revision != selectionRevision) return;
        }

        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _timelineSelectionMaterialization,
            cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            MaterializedTimelineSelection result = await Task.Run(
                () => BuildTimelineSelection(
                    snapshot,
                    current,
                    currentAnchor,
                    invert,
                    lane,
                    cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session.ActiveWorkspace, workspace)
                || !ReferenceEquals(surface.Snapshot, snapshot)
                || !_session.IsWorkspaceSelectionMaterializationCurrent(
                    workspace,
                    selectionRevision))
            {
                return;
            }
            if (result.IsUnchanged) return;
            if (GetTimelineSelectionSource(workspace, surface)
                is WorkspaceTimelineSelectionSource source)
            {
                workspace.Selection.RegisterTimelineMaterialization(
                    result.Ids,
                    source,
                    invert
                        ? WorkspaceSelectionRangeMode.Toggle
                        : WorkspaceSelectionRangeMode.Replace,
                    result.RangeCount,
                    result.BaseIntersectionCount);
            }
            workspace.Selection.AdoptMaterialized(
                result.Ids,
                result.Primary,
                result.Anchor);
            _session.RefreshWorkspaceSelection(workspace);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _session.SetStatusMessage(
                $"Selection operation failed: {ex.Message}",
                isError: true);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _timelineSelectionMaterialization,
                        null,
                        cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private static MaterializedTimelineSelection BuildTimelineSelection(
        TimelineRenderSnapshot snapshot,
        TimelineSelectionSnapshot current,
        MidoraId? currentAnchor,
        bool invert,
        int? lane,
        CancellationToken cancellationToken)
    {
        CompressedMidoraIdSet.Builder rangeBuilder =
            CompressedMidoraIdSet.CreateBuilder();
        MidoraId? first = null;
        int visited = 0;
        int baseIntersectionCount = 0;
        foreach (TimelineRenderItem item in snapshot.EnumerateAllItems())
        {
            if ((visited++ & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (item.State.HasFlag(TimelineItemState.HitTestDisabled)
                || lane is int requiredLane && item.Lane != requiredLane)
            {
                continue;
            }
            first ??= item.Id;
            if (rangeBuilder.Add(item.Id) && current.Contains(item.Id))
                baseIntersectionCount++;
        }
        cancellationToken.ThrowIfCancellationRequested();

        CompressedMidoraIdSet range = rangeBuilder.Build();
        CompressedMidoraIdSet currentIds = CompressedMidoraIdSet.Create(current.Ids);
        CompressedMidoraIdSet materialized = invert
            ? currentIds.SymmetricExcept(range)
            : range;

        MidoraId? primary;
        MidoraId? anchor;
        if (!invert)
        {
            primary = first;
            anchor = first;
        }
        else
        {
            primary = current.Primary is MidoraId currentPrimary
                && materialized.Contains(currentPrimary)
                    ? currentPrimary
                    : materialized.TryGetMinimum(out MidoraId minimum) ? minimum : null;
            anchor = primary;
        }
        bool unchanged = current.Count == materialized.Count
            && materialized.SetEquals(current.Ids)
            && primary == current.Primary
            && anchor == currentAnchor;
        return new(
            materialized,
            primary,
            anchor,
            unchanged,
            range.Count,
            baseIntersectionCount);
    }

    private sealed record MaterializedTimelineSelection(
        CompressedMidoraIdSet Ids,
        MidoraId? Primary,
        MidoraId? Anchor,
        bool IsUnchanged,
        int RangeCount,
        int BaseIntersectionCount);

    private async void DuplicateFocusedSelection()
    {
        if (_session.ActiveWorkspace is { } active && GetCachedObjectListSelection(active) is { IsMixed: true }) return;
        if (_session.CanEditProject && IsArrangementHeaderShortcutContext())
        {
            DuplicateArrangementHeader(shareInstrumentState: false);
            return;
        }
        if (_session.CanEditProject
            && TryGetSelectedLogicalTrack(out LogicalTrack logicalTrack, out _))
        {
            await ExecuteWorkspaceEditAsync(
                ProjectDomainEditCommands.DuplicateLogicalTrack(logicalTrack.Id));
            return;
        }
        if (_session.CanEditProject && TryGetSelectedEventInstrumentId(out MidoraId eventInstrumentId))
        {
            await ExecuteWorkspaceEditAsync(
                ProjectDomainEditCommands.DuplicateEventInstrument(eventInstrumentId));
            return;
        }
        if (!_session.CanEditProject
            || _session.Project is not MidoraProject project
            || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0)
        {
            return;
        }
        try
        {
            WorkspaceTimelineSelectionSource? timelineSource =
                workspace.Selection.HomogeneousTimelineSource;
            IReadOnlyCollection<MidoraId> ids = workspace.Selection.SharedIds;
            long cursor = (workspace as TimelineWorkspaceViewModel)?.EditCursorTick ?? 0;
            long firstNewStableId = project.NextStableId;
            switch (workspace)
            {
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement } arrangement:
                    {
                        MidoraId primary = workspace.Selection.Primary!.Value;
                        if (TimelineWorkspaceViewModel.FindSegment(project, primary) is { } located)
                        {
                            long target = cursor == 0
                                ? checked(located.Segment.ProjectStartTick + located.Segment.LengthTicks)
                                : cursor;
                            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateSegments(
                                ids, primary, ResolveArrangementTargetTrack(project, arrangement), target))) return;
                        }
                        else if (TimelineWorkspaceViewModel.FindMidiSegment(project, primary) is { } midi)
                        {
                            long target = cursor == 0
                                ? checked(midi.Segment.ProjectStartTick + midi.Segment.LengthTicks)
                                : cursor;
                            MidoraId targetTrackId = arrangement.GetArrangementLane(arrangement.ActiveLane ?? -1) is
                            { Kind: ArrangementLaneKind.PureMidiTrack, ObjectId: MidoraId activeTrackId }
                                    ? activeTrackId
                                    : midi.Track.Id;
                            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateMidiSegments(
                                ids, primary, targetTrackId, target))) return;
                        }
                        else throw new InvalidOperationException("The primary Segment no longer exists.");
                        break;
                    }
                case TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                }:
                    {
                        if (TimelineWorkspaceViewModel.FindSegment(project, segmentId) is { } located)
                        {
                            var source = located.Segment.Notes.CreateQuerySnapshot();
                            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, ids,
                                static item => (item.StartTick, checked(item.StartTick + item.LengthTicks), item.Note, (double)item.Velocity), token));
                            if (!read.Completed) return;
                            if (read.Value.Ids.Count != ids.Count)
                                throw new InvalidOperationException("Ctrl+D currently duplicates Logical Notes in the Segment note scope.");
                            long target = cursor == 0
                                ? checked(read.Value.MinimumTick + Math.Max(1, read.Value.MaximumLength))
                                : cursor;
                            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateLogicalNotes(segmentId, ids, segmentId, target))) return;
                        }
                        else if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } midi)
                        {
                            var source = midi.Segment.Notes.CreateObjectSource();
                            var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, ids,
                                static item => (item.StartTick, checked(item.StartTick + item.LengthTicks), item.Key, (double)item.NoteOnVelocity), token));
                            if (!read.Completed) return;
                            if (read.Value.Ids.Count != ids.Count)
                                throw new InvalidOperationException("Ctrl+D currently duplicates Direct MIDI Notes in the piano-roll scope.");
                            long target = cursor == 0
                                ? checked(read.Value.MinimumTick + Math.Max(1, read.Value.MaximumLength))
                                : cursor;
                            if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DuplicateDirectMidiNotes(
                                segmentId,
                                ids,
                                checked(target - read.Value.MinimumTick),
                                0))) return;
                        }
                        else throw new InvalidOperationException("The Segment no longer exists.");
                        break;
                    }
                case InstrumentWorkspaceViewModel
                {
                    ObjectId: MidoraId instrumentId
                } instrumentWorkspace:
                    {
                        InstrumentRenderLane lane = ResolveInstrumentTargetLane(instrumentWorkspace);
                        MidiValueTarget target = lane.Target
                            ?? throw new InvalidOperationException(
                                "Select a SubVoice MIDI Event lane before duplicating event points.");
                        EventInstrument instrument = project.EventInstruments.Single(value => value.Id == instrumentId);
                        SubVoice voice = instrument.SubVoices.Single(value => value.Id == lane.SubVoiceId);
                        var source = voice.Events.CreateQuerySnapshot();
                        var read = await ReadSelectionInputAsync((readProject, token) => ReadSelectionInputMetrics(readProject, source, ids,
                            static value => (value.Tick, value.Tick, 0, 0d), token,
                            value => value.Kind != TemplateEventKind.Note && TemplateEventMidiTargets.Enumerate(value).Contains(target)));
                        if (!read.Completed) return;
                        if (read.Value.Ids.Count != ids.Count)
                        {
                            throw new InvalidOperationException(
                                "Ctrl+D may duplicate event points from one SubVoice MIDI Event lane only.");
                        }
                        long earliest = read.Value.MinimumTick;
                        long destination = instrumentWorkspace.EditCursorTick is > 0
                            ? instrumentWorkspace.EditCursorTick.Value
                            : checked(read.Value.MaximumTick
                                + Math.Max(1, instrumentWorkspace.EditorSettings.EffectiveOperationStepTicks));
                        if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.AdjustSubVoiceEventPoints(
                            instrumentId,
                            voice.Id,
                            ids,
                            target,
                            checked(destination - earliest),
                            valueDelta: 0,
                            duplicate: true))) return;
                        break;
                    }
                default:
                    throw new InvalidOperationException("The active selection scope does not define Duplicate.");
            }
            SelectCreatedWorkspaceObjects(
                workspace,
                firstNewStableId,
                timelineSource: timelineSource);
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"Duplicate Selection: {exception.Message}", isError: true);
        }
    }

    private async void DeleteWorkspaceSelection()
    {
        using IDisposable? objectListFocus = PreserveObjectListCommandFocus();
        if (_session.Project is null || _session.ActiveWorkspace is not WorkspaceViewModel workspace
            || workspace.Selection.Ids.Count == 0)
        {
            return;
        }
        IReadOnlyCollection<MidoraId> ids = workspace.Selection.SharedIds;
        try
        {
            if (IsObjectListSelectionCommandContext(workspace)
                && TryGetTimelineObjectOwner(workspace, out var owner))
            {
                await ExecuteStagedProjectOperationAsync("Delete timeline objects",
                    ProjectDomainEditCommands.DeleteTimelineObjects(owner, ids), workspace, _lastTimelineCommandSurface);
                return;
            }
            switch (workspace)
            {
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement }:
                    if (!await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteArrangementSegments(ids))) return;
                    break;
                case TimelineWorkspaceViewModel
                {
                    Mode: TimelineWorkspaceMode.Segment,
                    ObjectId: MidoraId segmentId
                }:
                    if (!await DeleteSegmentSelection(segmentId, ids)) return;
                    break;
                case TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor }:
                    if (!await ExecuteStagedProjectOperationAsync("Delete Conductor events",
                        ProjectDomainEditCommands.DeleteConductorEvents(ids), workspace)) return;
                    break;
                case InstrumentWorkspaceViewModel instrumentWorkspace:
                    if (!await DeleteInstrumentSelection(instrumentWorkspace, ids)) return;
                    break;
            }
            workspace.Selection.Clear();
            if (workspace is InstrumentWorkspaceViewModel visualWorkspace)
            {
                ClearInstrumentStructureVisualSelection(visualWorkspace);
            }
        }
        catch (Exception exception)
        {
            _session.SetStatusMessage($"Delete Selection: {exception.Message}", isError: true);
        }
    }

    private async Task<bool> DeleteSegmentSelection(MidoraId segmentId, IReadOnlyCollection<MidoraId> ids)
    {
        (LogicalTrack Track, Segment Segment)? location =
            TimelineWorkspaceViewModel.FindSegment(_session.Project!, segmentId);
        if (location is null)
        {
            if (TimelineWorkspaceViewModel.FindMidiSegment(_session.Project!, segmentId) is not { } midi)
                return false;
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel timeline
                && timeline.SelectionSnapshot.TryGetMetrics(
                    TimelineItemKind.DirectMidiNote,
                    out TimelineSelectionMetrics noteMetrics)
                && noteMetrics.Count == ids.Count)
            {
                return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteDirectMidiNotes(segmentId, ids));
            }
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel eventTimeline
                && eventTimeline.SelectionSnapshot.TryGetMetrics(
                    TimelineItemKind.DirectMidiEvent,
                    out TimelineSelectionMetrics eventMetrics)
                && eventMetrics.Count == ids.Count)
            {
                return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteDirectMidiEvents(segmentId, ids));
            }
            if (_session.ActiveWorkspace is TimelineWorkspaceViewModel opaqueTimeline
                && opaqueTimeline.SelectionSnapshot.TryGetMetrics(
                    TimelineItemKind.OpaqueMidiEvent,
                    out TimelineSelectionMetrics opaqueMetrics)
                && opaqueMetrics.Count == ids.Count)
            {
                return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segmentId, ids));
            }
            var read = await ReadSelectionInputAsync<IProjectEditCommand>((readProject, token) =>
            {
                if (CountMatchingTimelineIds(readProject, midi.Segment.Notes.CreateObjectSource(), ids, token) == ids.Count)
                    return ProjectDomainEditCommands.DeleteDirectMidiNotes(segmentId, ids);
                if (CountMatchingTimelineIds(readProject, midi.Segment.ChannelEvents.CreateObjectSource(), ids, token) == ids.Count)
                    return ProjectDomainEditCommands.DeleteDirectMidiEvents(segmentId, ids);
                if (CountMatchingTimelineIds(readProject, midi.Segment.OpaqueEvents.CreateObjectSource(), ids, token) == ids.Count)
                    return ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segmentId, ids);
                throw new InvalidOperationException(
                    "A single delete gesture may target Direct MIDI Notes, Direct MIDI Events, or imported MIDI events, not a mixed selection.");
            });
            return read.Completed && await ExecuteWorkspaceEditAsync(read.Value);
        }
        if (ids.Count == 1
            && location.Value.Segment.ParameterLanes.Any(lane => lane.Id == ids.First()))
        {
            if (MessageDialog.Show(
                    this,
                    "Delete the selected Logical Parameter Lane and all of its points?",
                    "Delete Logical Parameter Lane",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteLogicalParameterLane(
                    segmentId,
                    ids.First(),
                    deletionConfirmed: true));
            }
            return false;
        }
        var logicalRead = await ReadSelectionInputAsync<IProjectEditCommand>((readProject, token) =>
        {
            if (CountMatchingTimelineIds(readProject, location.Value.Segment.Notes.CreateQuerySnapshot(), ids, token) == ids.Count)
                return ProjectDomainEditCommands.DeleteLogicalNotes(segmentId, ids);
            LogicalParameterLane? selectedLane = null;
            int selectedPointCount = 0;
            foreach (LogicalParameterLane lane in location.Value.Segment.ParameterLanes)
            {
                token.ThrowIfCancellationRequested();
                int matches = CountMatchingTimelineIds(readProject, lane.Points.CreateQuerySnapshot(), ids, token);
                if (matches == 0) continue;
                if (selectedLane is not null) { selectedLane = null; break; }
                selectedLane = lane;
                selectedPointCount = matches;
            }
            if (selectedLane is not null && selectedPointCount == ids.Count)
                return ProjectDomainEditCommands.DeleteLogicalParameterPoints(segmentId, selectedLane.Id, ids);
            throw new InvalidOperationException(
                "A single delete gesture may target Logical Notes or points from one Logical Parameter Lane, not a mixed selection.");
        });
        return logicalRead.Completed && await ExecuteWorkspaceEditAsync(logicalRead.Value);
    }

    private async Task<bool> DeleteInstrumentSelection(InstrumentWorkspaceViewModel workspace, IReadOnlyCollection<MidoraId> ids)
    {
        if (workspace.ObjectId is not MidoraId instrumentId) return false;
        EventInstrument instrument = _session.Project!.EventInstruments.Single(item => item.Id == instrumentId);
        if (ids.Count == 1)
        {
            MidoraId id = ids.First();
            if (instrument.SubVoices.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected SubVoice, all of its Template Events and Value Curves, and its Logical Parameter Mappings?",
                        "Delete SubVoice",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteSubVoice(instrumentId, id, nonEmptyDeletionConfirmed: true));
                }
                return false;
            }
            if (instrument.LogicalParameters.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Logical Parameter and all references to it?",
                        "Delete Logical Parameter",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteLogicalParameter(instrumentId, id, referencedDeletionConfirmed: true));
                }
                return false;
            }
            if (instrument.MappingFunctions.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Mapping Function? Existing Mapping Steps that reference it may also be affected.",
                        "Delete Mapping Function",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteMappingFunction(instrumentId, id, referencedDeletionConfirmed: true));
                }
                return false;
            }
            if (instrument.ParameterMappings.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Logical Parameter Mapping and its ordered Mapping Chain?",
                        "Delete Logical Parameter Mapping",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteLogicalParameterMapping(
                        instrumentId, id, deletionConfirmed: true));
                }
                return false;
            }
            if (instrument.Envelopes.Any(item => item.Id == id))
            {
                if (MessageDialog.Show(
                        this,
                        "Delete the selected Envelope Preset? Mapping Steps that reference it will retain an unavailable reference.",
                        "Delete Envelope Preset",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteInstrumentEnvelope(
                        instrumentId, id, referencedDeletionConfirmed: true));
                }
                return false;
            }
            MappingChainListItem? selectedChain = workspace.MappingChains
                .FirstOrDefault(item => item.Id == id);
            if (selectedChain is not null)
            {
                if (!selectedChain.CanDelete) return false;
                bool nonEmpty = selectedChain.StepCount != 0;
                if (nonEmpty
                    && MessageDialog.Show(
                        this,
                        $"Delete Mapping Chain '{selectedChain.Owner}' and its {selectedChain.StepCount} step(s)?",
                        "Delete Mapping Chain",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return false;
                }
                return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteMappingChain(
                    instrumentId,
                    id,
                    nonEmptyDeletionConfirmed: nonEmpty));
            }
            foreach (MappingChain chain in EnumerateInstrumentMappingChains(instrument))
            {
                if (chain.FirstOrDefault(item => item.Id == id) is not ValueMappingStep step) continue;
                return await ExecuteWorkspaceEditAsync(ProjectDomainEditCommands.DeleteMappingStep(
                    instrumentId,
                    chain.Id,
                    step.Id));
            }
        }
        var read = await ReadSelectionInputAsync<IProjectEditCommand>((readProject, token) =>
        {
            foreach (SubVoice candidateVoice in instrument.SubVoices)
            {
                token.ThrowIfCancellationRequested();
                ValueCurve? curve = null;
                foreach (ValueCurve candidate in candidateVoice.Curves)
                {
                    token.ThrowIfCancellationRequested();
                    int matches = CountMatchingTimelineIds(readProject, candidate.Points.CreateQuerySnapshot(), ids, token);
                    if (matches == 0) continue;
                    if (curve is not null || matches != ids.Count) { curve = null; break; }
                    curve = candidate;
                }
                if (curve is not null)
                    return ProjectDomainEditCommands.DeleteValueCurvePoints(instrumentId, candidateVoice.Id, curve.Id, ids);
            }
            SubVoice? selectedVoice = null;
            int selectedEventCount = 0;
            foreach (SubVoice voice in instrument.SubVoices)
            {
                token.ThrowIfCancellationRequested();
                int matches = CountMatchingTimelineIds(readProject, voice.Events.CreateQuerySnapshot(), ids, token);
                if (matches == 0) continue;
                if (selectedVoice is not null) { selectedVoice = null; break; }
                selectedVoice = voice;
                selectedEventCount = matches;
            }
            if (selectedVoice is null || selectedEventCount != ids.Count)
                throw new InvalidOperationException(
                    "A single delete gesture may target Template Events from one SubVoice only.");
            return ProjectDomainEditCommands.DeleteTemplateEvents(instrumentId, selectedVoice.Id, ids);
        });
        return read.Completed && await ExecuteWorkspaceEditAsync(read.Value);
    }

    private static IEnumerable<MappingChain> EnumerateInstrumentMappingChains(EventInstrument instrument) =>
        instrument.ParameterMappings.Select(item => item.Steps)
            .Concat(instrument.SubVoices
                .SelectMany(voice => voice.EventMappings)
                .Select(item => item.Steps));

    private static bool IsTextEditingFocus()
    {
        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        return focused is TextBoxBase or PasswordBox or ComboBox
               || FindVisualAncestor<MappingFunctionCodeEditor>(focused) is not null;
    }

    private void OnKeyboardFocusChangedForInputMethod(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        ApplyInputMethodPolicy(e.NewFocus as DependencyObject);

    internal static void ApplyInputMethodPolicy(DependencyObject? focused)
    {
        if (focused is null) return;
        InputMethod.SetIsInputMethodEnabled(focused, IsInputMethodTextTarget(focused));
    }

    internal static bool IsInputMethodTextTarget(DependencyObject? focused)
    {
        for (DependencyObject? current = focused; current is not null; current = GetUiParent(current))
        {
            if (current is TextBoxBase or PasswordBox)
            {
                return true;
            }
            if (current is MappingFunctionCodeEditor)
            {
                return true;
            }
            if (current is ComboBox { IsEditable: true })
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPlaybackShortcutInputFocus()
    {
        DependencyObject? focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is TextBoxBase or PasswordBox)
        {
            return true;
        }
        if (FindVisualAncestor<MappingFunctionCodeEditor>(focused) is not null)
        {
            return true;
        }

        if (focused is ComboBox { IsDropDownOpen: true })
        {
            return true;
        }

        ComboBoxItem? item = focused as ComboBoxItem;
        if (item is null && focused is Visual or Visual3D)
        {
            item = FindVisualAncestor<ComboBoxItem>(focused);
        }
        return item is not null
            && ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true };
    }

    private void OnPreviewMouseDownForPlaybackShortcut(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (_session.ActiveWorkspace is InstrumentWorkspaceViewModel
            && (e.ChangedButton == MouseButton.Right
                || e.ClickCount > 1
                || IsPreviewPriorityPointerTarget(source, PrimaryTransportButton)))
        {
            _ = PrepareForModalSurface();
        }
        TimelineSurface? surface = FindVisualAncestor<TimelineSurface>(source);
        if (surface is { SurfaceMode: TimelineSurfaceMode.Arrangement }
            && _session.ActiveWorkspace is TimelineWorkspaceViewModel
            { Mode: TimelineWorkspaceMode.Arrangement } arrangementWorkspace)
        {
            Point point = e.GetPosition(surface);
            if (surface.IsArrangementEmptyBackground(point))
            {
                ClearArrangementTrackSelection(arrangementWorkspace);
            }
        }
        if (_spaceStartedPlayback
            && (FindVisualAncestor<TextBoxBase>(source) is not null
                || FindVisualAncestor<PasswordBox>(source) is not null
                || FindVisualAncestor<ComboBox>(source) is not null
                || FindVisualAncestor<MappingFunctionCodeEditor>(source) is not null))
        {
            _spaceStartedPlayback = false;
        }
    }

    internal static bool IsPreviewPriorityPointerTarget(
        DependencyObject? source,
        ButtonBase? primaryTransportButton)
    {
        ButtonBase? button = FindVisualAncestor<ButtonBase>(source);
        return (button is not null && !ReferenceEquals(button, primaryTransportButton))
            || FindVisualAncestor<TabItem>(source) is not null
            || FindVisualAncestor<ComboBox>(source) is not null
            || FindVisualAncestor<MenuItem>(source) is not null;
    }

    private bool IsLogicalTrackShortcutContext() =>
        ProjectTree.IsKeyboardFocusWithin
        || _logicalTrackShortcutTrackId is MidoraId trackId
        && _session.Project?.Tracks.Any(value => value.Id == trackId) == true
        && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
        && _session.ActiveWorkspace is TimelineWorkspaceViewModel
        { Mode: TimelineWorkspaceMode.Arrangement };

    private bool IsArrangementHeaderShortcutContext() =>
        _arrangementHeaderShortcut is { } shortcut
        && ArrangementShortcutTargetExists(shortcut)
        && GetFocusedTimelineSurface() is { SurfaceMode: TimelineSurfaceMode.Arrangement }
        && _session.ActiveWorkspace is TimelineWorkspaceViewModel
        { Mode: TimelineWorkspaceMode.Arrangement };

    private bool ArrangementShortcutTargetExists((ArrangementLaneKind Kind, MidoraId Id) shortcut) =>
        _session.Project is MidoraProject project
        && (shortcut.Kind switch
        {
            ArrangementLaneKind.LogicalTrack => project.Tracks.Any(value => value.Id == shortcut.Id),
            ArrangementLaneKind.PureMidiTrack => project.PureMidiTracks.Any(value => value.Id == shortcut.Id),
            _ => false
        });

    private void SelectArrangementTrack(
        TimelineWorkspaceViewModel workspace,
        ArrangementLaneDescriptor descriptor)
    {
        bool conductor = descriptor.Kind == ArrangementLaneKind.Conductor;
        bool selectableObject = descriptor.Kind is ArrangementLaneKind.LogicalTrack
            or ArrangementLaneKind.PureMidiTrack
            or ArrangementLaneKind.DamagedLogicalTrack
            or ArrangementLaneKind.DamagedPureMidiTrack;
        if (!conductor && (!selectableObject || descriptor.ObjectId is not MidoraId)) return;

        workspace.ActiveLane = descriptor.Lane;
        workspace.IsConductorTrackSelected = conductor;
        workspace.SelectedArrangementTrackId = conductor ? null : descriptor.ObjectId;
        if (descriptor.ObjectId is MidoraId objectId
            && descriptor.Kind is ArrangementLaneKind.LogicalTrack or ArrangementLaneKind.PureMidiTrack)
        {
            _arrangementHeaderShortcut = (descriptor.Kind, objectId);
            _logicalTrackShortcutTrackId = descriptor.Kind == ArrangementLaneKind.LogicalTrack
                ? objectId
                : null;
        }
        else
        {
            _arrangementHeaderShortcut = null;
            _logicalTrackShortcutTrackId = null;
        }
    }

    private void ClearArrangementTrackSelection(TimelineWorkspaceViewModel workspace)
    {
        workspace.SelectedArrangementTrackId = null;
        workspace.IsConductorTrackSelected = false;
        workspace.ActiveLane = null;
        _trackHeaderContextLane = null;
        _arrangementSharedGroupContextId = null;
        _logicalTrackShortcutTrackId = null;
        _arrangementHeaderShortcut = null;
    }

    private static TimelineSurface? GetFocusedTimelineSurface() =>
        FindVisualAncestor<TimelineSurface>(Keyboard.FocusedElement as DependencyObject);

    private void RestorePlaybackShortcutFocus()
    {
        TimelineSurface? timeline = FindDescendant<TimelineSurface>(
            WorkspaceTabs,
            candidate => candidate.IsVisible && candidate.IsEnabled);
        if (timeline?.Focus() != true)
        {
            WorkspaceTabs.Focus();
        }
    }

    private bool IsTransientInputSurfaceOpen() =>
        NewProjectItemPopup.IsOpen
        || MainMenu.Items.OfType<MenuItem>().Any(IsOpenMenuBranch);

    private static bool IsOpenMenuBranch(MenuItem item) =>
        item.IsSubmenuOpen
        || item.Items.OfType<MenuItem>().Any(IsOpenMenuBranch);

    private static bool IsTimelineInteractionFocus()
    {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is TimelineSurface)
            {
                return true;
            }
            current = GetUiParent(current);
        }
        return false;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (IsInteractiveTitleBarSource(source))
        {
            return;
        }
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnTitleBarMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveTitleBarSource(e.OriginalSource as DependencyObject)) return;
        SystemCommands.ShowSystemMenu(this, PointToScreen(e.GetPosition(this)));
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject? source) =>
        FindVisualAncestor<Menu>(source) is not null
        || FindVisualAncestor<ButtonBase>(source) is not null;

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        WindowChrome? chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.ResizeBorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(6);
        MaximizeGlyph.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "WindowControl.Restore" : "WindowControl.Maximize");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo && ApplyMonitorWorkArea(hwnd, lParam, MinWidth, MinHeight)) handled = true;
        return IntPtr.Zero;
    }

    private static bool ApplyMonitorWorkArea(IntPtr hwnd, IntPtr pointer, double minWidth, double minHeight)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        MinMaxInfo limits = Marshal.PtrToStructure<MinMaxInfo>(pointer);
        limits.MaxPosition.X = info.WorkArea.Left - info.MonitorBounds.Left;
        limits.MaxPosition.Y = info.WorkArea.Top - info.MonitorBounds.Top;
        limits.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        limits.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        limits.MaxTrackSize = limits.MaxSize;
        double scale = Math.Max(1, GetDpiForWindow(hwnd)) / 96d;
        limits.MinTrackSize.X = Math.Max(limits.MinTrackSize.X, (int)Math.Ceiling(minWidth * scale));
        limits.MinTrackSize.Y = Math.Max(limits.MinTrackSize.Y, (int)Math.Ceiling(minHeight * scale));
        Marshal.StructureToPtr(limits, pointer, false);
        return true;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved; public NativePoint MaxSize; public NativePoint MaxPosition; public NativePoint MinTrackSize; public NativePoint MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)] private struct MonitorInfo { public int Size; public NativeRectangle MonitorBounds; public NativeRectangle WorkArea; public uint Flags; }
}
