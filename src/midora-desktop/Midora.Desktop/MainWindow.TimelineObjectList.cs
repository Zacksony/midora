using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<TimelineObjectListPane, ObjectListInteraction> _objectListInteractions = new();
    private readonly ConditionalWeakTable<WorkspaceViewModel, ObjectListSelectionCache> _objectListSelections = new();
    private TimelineObjectListPane? _pendingObjectListCommandFocus;

    private sealed class ObjectListFocusRestoration(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    private IDisposable? PreserveObjectListCommandFocus()
    {
        TimelineObjectListPane? pane = _pendingObjectListCommandFocus
            ?? FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject);
        if (pane?.DataContext is not WorkspaceViewModel workspace || !pane.IsVisible) return null;
        return new ObjectListFocusRestoration(() => Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (ReferenceEquals(_session.ActiveWorkspace, workspace)
                && ReferenceEquals(pane.DataContext, workspace) && pane.IsVisible && pane.IsEnabled)
                pane.Focus();
        })));
    }

    private sealed class ObjectListInteraction
    {
        public bool Attached;
        public CancellationTokenSource Cancellation = new();
        public void Cancel() { Cancellation.Cancel(); Cancellation.Dispose(); Cancellation = new(); }
    }

    private sealed class ObjectListSelectionCache
    {
        public long DocumentRevision = -1;
        public long SelectionRevision = -1;
        public TimelineObjectSelection? Value;
    }

    private void OnTimelineObjectListLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TimelineObjectListPane pane) return;
        ObjectListInteraction state = _objectListInteractions.GetOrCreateValue(pane);
        if (!state.Attached)
        {
            state.Attached = true;
            pane.SelectionRequested += OnTimelineObjectListSelectionRequested;
            pane.PropertiesRequested += async (_, row) =>
            {
                try
                {
                    if (pane.DataContext is not WorkspaceViewModel workspace) return;
                    await SelectObjectListRowAsync(pane, row);
                    if (row.IsInstrumentChange && row.IsSelected(workspace.Selection.IdSet))
                    { RunInstrumentAction(workspace, "Properties", () => pane.Focus()); return; }
                    if (ReferenceEquals(_session.ActiveWorkspace, workspace) && ReferenceEquals(pane.DataContext, workspace)
                        && workspace.Selection.Primary == row.Id && workspace.Selection.Ids.Count == 1)
                        await OpenWorkspacePropertiesCoreAsync(workspace);
                }
                catch (Exception ex) { ShowError("Properties", ex.Message); }
            };
            pane.LocateRequested += (_, row) =>
            {
                try { LocateObjectListRow(pane, row); }
                catch (Exception ex) { ShowError("Locate", ex.Message); }
            };
            pane.ContextRequested += OnTimelineObjectListContextRequested;
            pane.ReadFailed += (_, error) => _session.SetStatusMessage($"Read object list: {error.Message}", isError: true);
            pane.PreviewMouseDown += (_, _) => state.Cancel();
            pane.PreviewKeyDown += (_, key) => { if (key.Key == Key.Escape) state.Cancel(); };
            pane.DataContextChanged += (_, change) =>
            {
                state.Cancel();
                if (change.OldValue is WorkspaceViewModel previous) previous.ObjectList.SetActive(false);
                UpdateObjectListActivity(pane, pane.IsLoaded && pane.IsVisible);
            };
        }
        UpdateObjectListActivity(pane, pane.IsVisible);
    }

    private void OnTimelineObjectListUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TimelineObjectListPane pane) UpdateObjectListActivity(pane, false);
    }

    private void OnTimelineObjectListVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TimelineObjectListPane pane) UpdateObjectListActivity(pane, pane.IsVisible && pane.IsLoaded);
    }

    private void UpdateObjectListActivity(TimelineObjectListPane pane, bool active)
    {
        if (!active) _objectListInteractions.GetOrCreateValue(pane).Cancel();
        if (pane.DataContext is WorkspaceViewModel workspace) workspace.ObjectList.SetActive(active);
        pane.IsActive = active;
        if (pane.DataContext is WorkspaceViewModel failed && failed.ObjectList.ErrorText is { Length: > 0 } error)
            _session.SetStatusMessage($"Object list: {error}", isError: true);
    }

    internal static bool TryGetTimelineObjectOwner(WorkspaceViewModel workspace, out TimelineObjectOwner owner)
    {
        owner = default;
        if (workspace is TimelineWorkspaceViewModel { IsSegment: true, ObjectId: MidoraId segmentId } segment)
        {
            owner = new(segment.TabIconKind == WorkspaceTabIconKind.PureMidiTrack
                ? ProjectTimelineOwnerKind.DirectMidiSegment : ProjectTimelineOwnerKind.LogicalSegment, segmentId);
            return true;
        }
        if (workspace is InstrumentWorkspaceViewModel { ObjectId: MidoraId instrumentId, ActiveSubVoiceId: MidoraId voiceId })
        {
            owner = new(ProjectTimelineOwnerKind.SubVoice, voiceId, instrumentId);
            return true;
        }
        return false;
    }

    private TimelineSurface? ObjectListCommandSurface(WorkspaceViewModel workspace, bool notes) =>
        FindWorkspaceElement<TimelineSurface>(workspace is InstrumentWorkspaceViewModel
            ? notes ? "SubVoiceNotes" : "SubVoiceEvents"
            : notes ? "PrimaryTimeline" : "ParameterLanes")
        ?? FindWorkspaceElement<TimelineSurface>(workspace is InstrumentWorkspaceViewModel ? "SubVoiceNotes" : "PrimaryTimeline");

    private async void OnTimelineObjectListSelectionRequested(object? sender, TimelineObjectListSelectionEventArgs e)
    {
        if (sender is not TimelineObjectListPane pane) return;
        await SelectObjectListRangeAsync(pane, e);
    }

    private Task SelectObjectListRowAsync(TimelineObjectListPane pane, TimelineObjectListRow row) =>
        SelectObjectListRangeAsync(pane, new(0, 0, ModifierKeys.None, row, forceSingle: true));

    private async Task SelectObjectListRangeAsync(TimelineObjectListPane pane, TimelineObjectListSelectionEventArgs e)
    {
        if (pane.DataContext is not WorkspaceViewModel workspace || pane.Source is not { } source
            || _session.Project is not MidoraProject project || !TryGetTimelineObjectOwner(workspace, out var owner)) return;
        ObjectListInteraction state = _objectListInteractions.GetOrCreateValue(pane);
        state.Cancel();
        CancellationToken token = state.Cancellation.Token;
        if (e.First < 0)
        {
            workspace.Selection.Clear();
            _session.RefreshWorkspaceSelection(workspace);
            return;
        }
        long selectionRevision = workspace.Selection.Revision;
        long documentRevision = _session.Document!.PublicationRevision;
        CompressedMidoraIdSet basis = workspace.Selection.SharedIds;
        try
        {
            IReadOnlyCollection<MidoraId> range = e.First == e.Last && e.Primary is { } row
                ? row.SelectionIds.ToArray() : await source.ReadSelectionAsync(e.First, e.Last, token);
            var prepared = await Task.Run(() =>
            {
                CompressedMidoraIdSet ids = CompressedMidoraIdSet.Create(range, token);
                if (!e.ForceSingle && e.Modifiers.HasFlag(ModifierKeys.Control))
                    ids = e.Modifiers.HasFlag(ModifierKeys.Shift) ? basis.Union(ids) : basis.SymmetricExcept(ids);
                TimelineObjectSelection partition = TimelineObjectSelection.Capture(project, owner, ids, token);
                return partition;
            }, token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(pane.Source, source) || _session.Document?.PublicationRevision != documentRevision
                || workspace.Selection.Revision != selectionRevision || !_session.Workspaces.Contains(workspace)) return;
            MidoraId? primary = e.Primary is { } selected && prepared.Ids.Contains(selected.Id) ? selected.Id
                : prepared.Ids.TryGetMinimum(out MidoraId first) ? first : null;
            if (prepared.HomogeneousSource is { } homogeneous)
                workspace.Selection.RegisterTimelineMaterialization(prepared.Ids, homogeneous,
                    WorkspaceSelectionRangeMode.Replace, prepared.Ids.Count, 0);
            workspace.Selection.AdoptMaterialized(prepared.Ids, primary);
            CacheObjectListSelection(workspace, prepared);
            _lastTimelineCommandSurface = ObjectListCommandSurface(workspace, prepared.Notes.Count != 0);
            _session.RefreshWorkspaceSelection(workspace);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _session.SetStatusMessage($"Select timeline objects: {ex.Message}", isError: true); }
    }

    private void CacheObjectListSelection(WorkspaceViewModel workspace, TimelineObjectSelection selection)
    {
        ObjectListSelectionCache cache = _objectListSelections.GetOrCreateValue(workspace);
        cache.Value = selection;
        cache.SelectionRevision = workspace.Selection.Revision;
        cache.DocumentRevision = _session.Document?.PublicationRevision ?? -1;
    }

    private TimelineObjectSelection? GetCachedObjectListSelection(WorkspaceViewModel workspace) =>
        _objectListSelections.TryGetValue(workspace, out var cache)
        && cache.SelectionRevision == workspace.Selection.Revision
        && cache.DocumentRevision == _session.Document?.PublicationRevision ? cache.Value : null;

    private bool IsObjectListSelectionCommandContext(WorkspaceViewModel workspace) =>
        TryGetTimelineObjectOwner(workspace, out _)
        && (GetCachedObjectListSelection(workspace) is not null
            || FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject) is not null
            || workspace.Selection.Ids.Count > 1 && IsTimelineInteractionFocus());

    private async void InvokeObjectListShortcut(Key key, WorkspaceViewModel workspace)
    {
        using IDisposable? restoreFocus = PreserveObjectListCommandFocus();
        try
        {
            // An edit/Undo invalidates the cached partition. Re-resolve off the
            // Dispatcher instead of requiring another click to enable tools.
            var pane = FindVisualAncestor<TimelineObjectListPane>(Keyboard.FocusedElement as DependencyObject);
            CancellationToken token = pane is null ? default : _objectListInteractions.GetOrCreateValue(pane).Cancellation.Token;
            var selection = await ReadObjectListSelectionAsync(workspace, token);
            if (selection is null || selection.Ids.Count == 0 || selection.IsMixed
                || !ReferenceEquals(_session.ActiveWorkspace, workspace) || IsTransientInputSurfaceOpen()) return;
            if (selection.InstrumentMembers.Count != 0)
            {
                if (key is Key.P or Key.Q) RunInstrumentAction(workspace, key == Key.P ? "Properties" : "Scale", () => pane?.Focus());
                return;
            }
            if (key == Key.P)
            {
                if (!selection.ContainsOpaque || selection.Ids.Count == 1)
                    await OpenWorkspacePropertiesCoreAsync(workspace);
                return;
            }
            if (!_session.CanEditProject || selection.HomogeneousSource is not { } source) return;
            _lastTimelineCommandSurface = ObjectListCommandSurface(workspace, selection.Notes.Count != 0);
            _timelineSelectionOperationContext = CreateTimelineSelectionOperationContext(source, selection.Ids);
            if (!PrepareForModalSurface()) return;
            switch (key)
            {
                case Key.Q: OnScaleSelectionClick(this, new()); break;
                case Key.T when selection.Notes.Count != 0: OnTransposeSelectionClick(this, new()); break;
                case Key.E: OnBatchEditSelectionClick(this, new()); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _session.SetStatusMessage($"Timeline objects: {ex.Message}", isError: true); }
    }

    private async Task<TimelineObjectSelection?> ReadObjectListSelectionAsync(WorkspaceViewModel workspace,
        CancellationToken token = default)
    {
        if (GetCachedObjectListSelection(workspace) is { } cached) return cached;
        if (_session.Project is not MidoraProject project || !TryGetTimelineObjectOwner(workspace, out var owner)) return null;
        long revision = _session.Document!.PublicationRevision, selectedRevision = workspace.Selection.Revision;
        var ids = workspace.Selection.SharedIds;
        var selection = await Task.Run(() => TimelineObjectSelection.Capture(project, owner, ids, token), token);
        token.ThrowIfCancellationRequested();
        if (_session.Document?.PublicationRevision != revision || workspace.Selection.Revision != selectedRevision
            || !_session.Workspaces.Contains(workspace)) return null;
        CacheObjectListSelection(workspace, selection);
        return selection;
    }

    private async void OnTimelineObjectListContextRequested(object? sender, TimelineObjectListContextEventArgs e)
    {
        if (sender is not TimelineObjectListPane pane || pane.DataContext is not WorkspaceViewModel workspace) return;
        try
        {
            if (e.Row is { } row && !row.IsSelected(workspace.Selection.IdSet))
                await SelectObjectListRowAsync(pane, row);
            if (!ReferenceEquals(pane.Source, e.Source) || !pane.IsVisible) return;
            ContextMenu menu = new() { PlacementTarget = pane, Placement = PlacementMode.MousePoint };
            menu.SetResourceReference(StyleProperty, typeof(ContextMenu));
            pane.ContextMenu = menu;
            menu.IsOpen = true;
            await PopulateObjectListMenuAsync(menu, workspace, e.Row);
        }
        catch (Exception ex) { _session.SetStatusMessage($"Object menu: {ex.Message}", isError: true); }
    }

    private void LocateObjectListRow(TimelineObjectListPane pane, TimelineObjectListRow row)
    {
        if (pane.DataContext is not WorkspaceViewModel workspace) return;
        if (row.IsInstrumentChange)
        {
            if (workspace is TimelineWorkspaceViewModel t) { t.StartTick = Math.Max(0, row.Tick - t.TickSpan / 2); t.IsLowerEditorVisible = true; }
            if (workspace is InstrumentWorkspaceViewModel v) { v.TimelineStartTick = Math.Max(0, row.Tick - v.TimelineTickSpan / 2); v.IsLowerEditorVisible = true; }
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                LaneTabHeader.Find<LaneTabHost>(this, host => host.IsVisible && ReferenceEquals(host.DataContext, workspace))?.ShowInstrumentTarget()));
            return;
        }
        int visibleKeys = Math.Clamp(ObjectListCommandSurface(workspace, true)?.VisibleLaneCount ?? 24, 1, 128);
        int firstKeyLane = Math.Clamp(127 - row.Key - visibleKeys / 2, 0, 128 - visibleKeys);
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            timeline.StartTick = Math.Max(0, row.Tick - timeline.TickSpan / 2);
            if (row.IsNote) timeline.FirstLane = firstKeyLane;
            else
            {
                timeline.IsLowerEditorVisible = true;
                if (row.DirectMidiTarget is { } target) timeline.AddDirectMidiLaneTarget(target);
                _session.RefreshWorkspace(workspace);
                timeline.ActiveParameterLaneIndex = timeline.ParameterLaneOptions.ToList().FindIndex(lane =>
                    row.IsOpaque ? lane.IsOpaqueMidiLane : row.Kind == TimelineItemKind.LogicalParameterPoint
                        ? lane.ParameterId == row.ParameterId : lane.DirectMidiTarget == row.DirectMidiTarget);
                timeline.PreferCurrentParameterLaneOnNextRebuild();
                _session.RefreshWorkspace(workspace);
                ShowActiveEventLaneTab(workspace);
            }
        }
        else if (workspace is InstrumentWorkspaceViewModel instrument)
        {
            instrument.TimelineStartTick = Math.Max(0, row.Tick - instrument.TimelineTickSpan / 2);
            if (row.IsNote) instrument.TimelineFirstLane = firstKeyLane;
            else
            {
                instrument.IsLowerEditorVisible = true;
                instrument.ActiveLowerEditorIndex = 2;
                instrument.ActiveRenderLaneIndex = instrument.RenderLanes.ToList().FindIndex(lane =>
                    lane.EventMappingTarget == row.MidiTarget);
                instrument.PreferCurrentRenderLaneOnNextRebuild();
                _session.RefreshWorkspace(workspace);
                ShowActiveEventLaneTab(workspace);
            }
        }
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(_session.ActiveWorkspace, workspace)) return;
            _lastTimelineCommandSurface = ObjectListCommandSurface(workspace, row.IsNote);
            _lastTimelineCommandSurface?.Focus();
        }));
    }

    private async Task PopulateObjectListMenuAsync(ContextMenu menu, WorkspaceViewModel workspace,
        TimelineObjectListRow? hitRow = null, bool requireOpen = true, CancellationToken cancellationToken = default)
    {
        long revision = workspace.Selection.Revision;
        long documentRevision = _session.Document?.PublicationRevision ?? -1;
        menu.Items.Clear();
        menu.Items.Add(new MenuItem { Header = "Reading selection…", IsEnabled = false });
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RoutedEventHandler closed = (_, _) => cancellation.Cancel();
        menu.Closed += closed;
        try
        {
            var selection = await ReadObjectListSelectionAsync(workspace, cancellation.Token);
            if (requireOpen && !menu.IsOpen || selection is null || !Current()) return;
            menu.Items.Clear();
            bool canEdit = _session.CanEditProject;
            if (selection.InstrumentMembers.Count != 0)
            {
                var instrumentMenu = InstrumentMenu(workspace, true);
                ItemCollection items = menu.Items;
                if (selection.IsMixed) { var parent = new MenuItem { Header = "For Instrument Changes" }; menu.Items.Add(parent); items = parent.Items; }
                foreach (var item in instrumentMenu.Items.Cast<object>().ToArray()) { instrumentMenu.Items.Remove(item); items.Add(item); }
            }
            if (selection.IsMixed)
            {
                MenuItem notes = new() { Header = "For Notes" };
                MenuItem events = new() { Header = "For Events" };
                if (selection.Notes.Count != 0) { menu.Items.Add(notes); PopulateType(notes.Items, true); }
                if (selection.Events.Count != 0) { menu.Items.Add(events); PopulateType(events.Items, false); }
                menu.Items.Add(new Separator());
                Add(menu.Items, "Delete All Selected", () => DeleteAll(), canEdit, "Delete");
                Add(menu.Items, "Copy", () => { }, false, "Ctrl+C");
                Add(menu.Items, "Cut", () => { }, false, "Ctrl+X");
            }
            else if (selection.InstrumentMembers.Count != 0) { }
            else if (selection.Ids.Count != 0) PopulateType(menu.Items, selection.Notes.Count != 0);
            else Add(menu.Items, "Properties…", () => { }, false, "Ctrl+P");
            menu.Items.Add(new Separator());
            Add(menu.Items, "Batch Create Notes…", () =>
            {
                _lastTimelineCommandSurface = ObjectListCommandSurface(workspace, true);
                OnBatchCreateTimelineObjectsClick(this, new());
            }, canEdit);
            var eventSurface = ObjectListCommandSurface(workspace, false);
            bool eventCreation = eventSurface is not null && GetTimelineSelectionSource(workspace, eventSurface)
                is { } eventCreationSource && !IsTimelineNoteGenerationSource(eventCreationSource)
                && IsTimelineGenerationSource(eventCreationSource);
            Add(menu.Items, "Batch Create Events…", () =>
            {
                _lastTimelineCommandSurface = eventSurface;
                OnBatchCreateTimelineObjectsClick(this, new());
            }, canEdit && eventCreation);
            if (selection.IsMixed || selection.InstrumentMembers.Count == 0)
                Add(menu.Items, "Paste", () => OnPasteClick(this, new()), canEdit, "Ctrl+V");
            Add(menu.Items, "Deselect All", () =>
            {
                workspace.Selection.Clear(); _session.RefreshWorkspaceSelection(workspace);
            }, selection.Ids.Count != 0);
            if (hitRow is { } row && menu.PlacementTarget is TimelineObjectListPane pane)
                Add(menu.Items, "Locate", () => LocateObjectListRow(pane, row));

            void DeleteAll() => Run(async () => await ExecuteStagedProjectOperationAsync("Delete timeline objects",
                ProjectDomainEditCommands.DeleteTimelineObjects(selection.Owner, selection.Ids), workspace,
                _lastTimelineCommandSurface));

            void PopulateType(ItemCollection items, bool notes)
            {
                CompressedMidoraIdSet ids = notes ? selection.Notes : selection.Events;
                CompressedMidoraIdSet retained = (notes ? selection.Events : selection.Notes).Union(selection.InstrumentMembers);
                WorkspaceTimelineSelectionSource? source = notes ? selection.NoteSource : selection.EventSource;
                WorkspaceTimelineSelectionSource? quantizeSource = notes ? source : selection.EventQuantizeScope;
                TimelineSelectionOperationContext? operation = source is { } homogeneous
                    ? CreateTimelineSelectionOperationContext(homogeneous, ids) : null;
                if (operation is not null) operation = operation with
                    { RetainedIds = retained.Count == 0 ? null : retained, FrozenSelectionRevision = revision };
                TimelineSelectionOperationContext? quantize = quantizeSource is { } scope
                    ? CreateTimelineSelectionOperationContext(scope, ids) : null;
                if (quantize is not null) quantize = quantize with
                    { RetainedIds = retained.Count == 0 ? null : retained, FrozenSelectionRevision = revision };
                bool tools = notes || selection.CanUseEventValueTools;
                bool clipboard = notes || !selection.ContainsOpaque && (selection.EventSource is not null
                    || selection.Owner.Kind != ProjectTimelineOwnerKind.LogicalSegment);
                Add(items, "Copy", () => Run(() => CopySubset(false)), clipboard, selection.IsMixed ? null : "Ctrl+C");
                Add(items, "Cut", () => Run(() => CopySubset(true)), clipboard && canEdit, selection.IsMixed ? null : "Ctrl+X");
                Add(items, "Delete", () => Run(async () => await ExecuteStagedProjectOperationAsync("Delete timeline objects",
                    ProjectDomainEditCommands.DeleteTimelineObjects(selection.Owner, ids), workspace,
                    _lastTimelineCommandSurface, retainedSelectionIds: retained.Count == 0 ? null : retained)), canEdit);
                items.Add(new Separator());
                AddTool("Flip Horizontal", OnFlipSelectionHorizontalClick, tools);
                if (notes) AddTool("Flip Vertical", OnFlipSelectionVerticalClick);
                AddTool("Scale…", OnScaleSelectionClick, tools, "Ctrl+Q");
                if (notes) AddTool("Transpose…", OnTransposeSelectionClick, true, "Ctrl+T");
                AddTool("Batch Edit…", OnBatchEditSelectionClick, tools, "Ctrl+E");
                if (notes)
                {
                    AddTool("Humanize…", OnHumanizeSelectionClick);
                    AddTool("Split…", OnSplitNotesClick);
                    AddTool("Join…", OnJoinNotesClick);
                }
                Add(items, "Quantize…", () =>
                {
                    SetContexts(); _timelineQuantizeOperationContext = quantize;
                    OnQuantizeSelectionClick(this, new());
                }, canEdit && quantize is not null);
                items.Add(new Separator());
                bool properties = notes || !selection.ContainsOpaque || ids.Count == 1;
                Add(items, "Properties…", () => Run(async () =>
                {
                    SetContexts();
                    if (ids.Count == 1 && selection.ContainsOpaque)
                        OpenOpaqueObjectProperties(workspace, ids.First());
                    else await OpenObjectSubsetPropertiesAsync(workspace, ids, retained, source, revision);
                }), properties, selection.IsMixed ? null : "Ctrl+P");

                void SetContexts()
                {
                    _timelineSelectionOperationContext = operation;
                    _timelineQuantizeOperationContext = quantize;
                    _lastTimelineCommandSurface = ObjectListCommandSurface(workspace, notes);
                }
                void AddTool(string text, RoutedEventHandler handler, bool enabled = true, string? gesture = null)
                    => Add(items, text, () => { SetContexts(); handler(this, new()); },
                        canEdit && enabled && operation is not null, selection.IsMixed ? null : gesture);
                async Task CopySubset(bool cut)
                {
                    if (_session.Document is not { } document || _session.Project is not { } project
                        || !ids.TryGetMinimum(out var primary)) return;
                    SetContexts();
                    ClipboardTransferRequest request = ResolveWorkspaceClipboardRequest(document, project, workspace, ids, primary);
                    await RunClipboardTransferAsync(document, workspace, request.Copy,
                        cut ? ProjectDomainEditCommands.DeleteTimelineObjects(selection.Owner, ids) : null, retained);
                }
            }
            void Add(ItemCollection items, string text, Action action, bool enabled = true, string? gesture = null)
            {
                MenuItem item = new() { Header = text, IsEnabled = enabled, InputGestureText = gesture ?? "" };
                SetTimelineCommandIcon(item);
                item.Click += (_, _) =>
                {
                    if (!Current()) return;
                    TimelineObjectListPane? previous = _pendingObjectListCommandFocus;
                    _pendingObjectListCommandFocus = menu.PlacementTarget as TimelineObjectListPane;
                    try { action(); }
                    finally { _pendingObjectListCommandFocus = previous; }
                };
                items.Add(item);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            menu.IsOpen = false;
            _session.SetStatusMessage($"Read selection menu: {ex.Message}", isError: true);
        }
        finally { menu.Closed -= closed; }
        bool Current() => _session.Document?.PublicationRevision == documentRevision
            && workspace.Selection.Revision == revision && ReferenceEquals(_session.ActiveWorkspace, workspace);
        async void Run(Func<Task> action)
        {
            using IDisposable? restoreFocus = PreserveObjectListCommandFocus();
            try { await action(); }
            catch (Exception ex) { ShowError("Timeline objects", ex.Message); }
        }
    }

    private async Task OpenObjectSubsetPropertiesAsync(WorkspaceViewModel workspace, CompressedMidoraIdSet ids,
        CompressedMidoraIdSet retained, WorkspaceTimelineSelectionSource? source, long selectionRevision)
    {
        if (!PrepareForModalSurface() || _session.Project is not MidoraProject project) return;
        var context = ObjectPropertiesSelectionContext.Capture(workspace) with { Ids = ids, Source = source };
        ObjectPropertiesViewModel? properties = null;
        bool read = await RunOperationAsync("Read Properties", async token =>
        {
            using DispatcherCoalescingProgress<TimelineEditPreparationProgress> progress = new(Dispatcher,
                TimeSpan.FromMilliseconds(100), value => _session.ActiveForegroundTask?.Report(
                    FormatTimelineEditPreparationProgress(value), value.IsIndeterminate ? null : value.OverallFraction));
            properties = await Task.Run(() => ObjectPropertiesProjection.ReadMultiSelection(project, context, token, progress), token);
            progress.Flush();
        }, canCancel: true);
        if (read && properties is { Fields.Count: > 0 } && workspace.Selection.Revision == selectionRevision)
        {
            ObjectPropertiesDialog dialog = new(_session, workspace, properties, context, retained) { Owner = this };
            ShowModalDialog(dialog);
            _session.RefreshWorkspaceSelection(workspace);
        }
        RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);
    }

    private void OpenOpaqueObjectProperties(WorkspaceViewModel workspace, MidoraId id)
    {
        if (!PrepareForModalSurface() || workspace is not TimelineWorkspaceViewModel timeline
            || _session.Project is not { } project) return;
        // Read-only projection with a frozen single identity; the real mixed
        // Workspace selection is never temporarily replaced.
        using TimelineWorkspaceViewModel target = new(timeline.Key, timeline.Header, timeline.Mode);
        target.Selection.Replace(id);
        ObjectPropertiesViewModel properties = new();
        ObjectPropertiesProjection.Rebuild(properties, project, target, _instrumentCatalogResolver);
        ObjectPropertiesDialog dialog = new(properties, _ => true) { Owner = this };
        ShowModalDialog(dialog);
        RestoreModalCommandFocus(workspace, _lastTimelineCommandSurface);
    }
}
