using System.Windows;
using System.IO;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop;

public sealed partial class DesktopSessionController
{
    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int index = 0; index < values.Count; index++)
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        return -1;
    }

    private static ProjectPresentationNavigationKeyV4 ToPresentationKey(WorkspaceKey key) =>
        key.Kind switch
        {
            WorkspaceKind.Arrangement => new(ProjectPresentationNavigationKindV4.Arrangement),
            WorkspaceKind.EventInstrumentLibrary => new(ProjectPresentationNavigationKindV4.EventInstrumentLibrary),
            WorkspaceKind.ProjectSettings => new(ProjectPresentationNavigationKindV4.ProjectSettings),
            WorkspaceKind.Diagnostics => new(ProjectPresentationNavigationKindV4.Diagnostics),
            WorkspaceKind.ConductorTrack => new(ProjectPresentationNavigationKindV4.ConductorTrack),
            WorkspaceKind.SegmentEditor when key.ObjectId is MidoraId id =>
                new(ProjectPresentationNavigationKindV4.SegmentEditor, id),
            WorkspaceKind.EventInstrumentEditor when key.ObjectId is MidoraId id =>
                new(ProjectPresentationNavigationKindV4.EventInstrumentEditor, id),
            WorkspaceKind.AllTracks => new(ProjectPresentationNavigationKindV4.AllTracks),
            _ => throw new InvalidDataException("Workspace key cannot be persisted.")
        };

    private static WorkspaceKey? FromPresentationKey(ProjectPresentationNavigationKeyV4 key) =>
        key.Kind switch
        {
            ProjectPresentationNavigationKindV4.Arrangement => WorkspaceKey.ForType(WorkspaceKind.Arrangement),
            ProjectPresentationNavigationKindV4.EventInstrumentLibrary => WorkspaceKey.ForType(WorkspaceKind.EventInstrumentLibrary),
            ProjectPresentationNavigationKindV4.ProjectSettings => WorkspaceKey.ForType(WorkspaceKind.ProjectSettings),
            ProjectPresentationNavigationKindV4.Diagnostics => WorkspaceKey.ForType(WorkspaceKind.Diagnostics),
            ProjectPresentationNavigationKindV4.ConductorTrack => WorkspaceKey.ForType(WorkspaceKind.ConductorTrack),
            ProjectPresentationNavigationKindV4.SegmentEditor when key.ObjectId is MidoraId id =>
                WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, id),
            ProjectPresentationNavigationKindV4.EventInstrumentEditor when key.ObjectId is MidoraId id =>
                WorkspaceKey.ForObject(WorkspaceKind.EventInstrumentEditor, id),
            ProjectPresentationNavigationKindV4.AllTracks => WorkspaceKey.ForType(WorkspaceKind.AllTracks),
            _ => null
        };

    private static ProjectPresentationNavigationViewV4 ViewFor(
        ProjectPresentationNavigationKeyV4 key,
        WorkspaceViewModel workspace)
    {
        ProjectPresentationNavigationViewV4 view = new(key);
        if (workspace is LazyWorkspaceViewModel lazy && lazy.NavigationView is { } saved)
            view = saved with { Key = key };

        switch (workspace)
        {
            case TimelineWorkspaceViewModel timeline:
                view = view with
                {
                    StartTick = timeline.StartTick,
                    TickSpan = timeline.TickSpan,
                    FirstLane = timeline.FirstLane,
                    LaneHeight = timeline.LaneHeight,
                    LowerEditorVisible = timeline.IsSegment ? timeline.IsLowerEditorVisible : null,
                    LowerEditorHeight = timeline.IsSegment || timeline.IsConductor
                        ? timeline.BottomEditorRowHeight.IsAbsolute ? timeline.BottomEditorRowHeight.Value : null
                        : null,
                    PrimaryFilter = timeline.IsSegment ? timeline.ActiveParameterLaneIndex : null
                };
                break;
            case InstrumentWorkspaceViewModel instrument:
                view = view with
                {
                    Page = instrument.ActiveSectionIndex,
                    SecondaryId = instrument.ActiveSubVoiceId,
                    StartTick = instrument.TimelineStartTick,
                    TickSpan = instrument.TimelineTickSpan,
                    FirstLane = instrument.TimelineFirstLane,
                    LaneHeight = instrument.TimelineLaneHeight,
                    LowerEditorVisible = instrument.IsLowerEditorVisible,
                    LowerEditorHeight = instrument.BottomEditorRowHeight.IsAbsolute
                        ? instrument.BottomEditorRowHeight.Value : null,
                    PrimaryFilter = instrument.ActiveLowerEditorIndex
                };
                break;
            case LibraryWorkspaceViewModel library:
                view = view with { SearchText = library.SearchText, SortMode = (int)library.SortMode };
                break;
            case DiagnosticsWorkspaceViewModel diagnostics:
                view = view with
                {
                    Page = diagnostics.CurrentNavigationPage > int.MaxValue
                        ? int.MaxValue : (int)diagnostics.CurrentNavigationPage,
                    SearchText = diagnostics.SearchText,
                    PrimaryFilter = IndexOf(diagnostics.SeverityFilters, diagnostics.SeverityFilter),
                    SecondaryFilter = IndexOf(diagnostics.StatusFilters, diagnostics.StatusFilter),
                    TertiaryFilter = IndexOf(diagnostics.ScopeFilters, diagnostics.ScopeFilter)
                };
                break;
            case AllTracksWorkspaceViewModel allTracks:
                view = view with
                {
                    StartTick = allTracks.StartTick,
                    TickSpan = allTracks.TickSpan,
                    FirstLane = allTracks.FirstLane,
                    LaneHeight = allTracks.LaneHeight
                };
                break;
        }
        return view;
    }

    private ProjectPresentationNavigationStateV4 CaptureWorkspaceNavigation()
    {
        List<ProjectPresentationNavigationKeyV4> tabs = [];
        List<ProjectPresentationNavigationViewV4> views = [];
        foreach (WorkspaceViewModel workspace in Workspaces)
        {
            ProjectPresentationNavigationKeyV4 key;
            try { key = ToPresentationKey(workspace.Key); }
            catch (InvalidDataException) { continue; }
            if (!tabs.Contains(key)) tabs.Add(key);
            views.Add(ViewFor(key, workspace));
        }
        ProjectPresentationNavigationKeyV4 arrangement = new(ProjectPresentationNavigationKindV4.Arrangement);
        if (!tabs.Contains(arrangement)) tabs.Insert(0, arrangement);
        ProjectPresentationNavigationKeyV4 active = ActiveWorkspace is { } current
            ? ToPresentationKey(current.Key)
            : arrangement;
        if (!tabs.Contains(active)) active = arrangement;
        return ProjectPresentationCodecForDesktop.ValidateNavigationSnapshot(
            new ProjectPresentationNavigationStateV4(tabs, active, views), Project!);
    }

    private void RestoreWorkspaceNavigation(ProjectContext context)
    {
        if (Project is not { } project) return;
        ProjectPresentationNavigationStateV4 state =
            context.Persistence.Presentation.Current.Navigation
            ?? ProjectPresentationNavigationStateV4.Empty;

        List<(WorkspaceKey Key, ProjectPresentationNavigationViewV4? View)> requested = [];
        bool invalid = false;
        foreach (ProjectPresentationNavigationKeyV4 presentationKey in state.Tabs)
        {
            WorkspaceKey? key = FromPresentationKey(presentationKey);
            if (key is null) { invalid = true; continue; }
            ProjectPresentationNavigationViewV4? view = state.Views.FirstOrDefault(item => item.Key == presentationKey);
            if (requested.Any(item => item.Key == key.Value)) continue;
            requested.Add((key.Value, view));
        }
        WorkspaceKey arrangementKey = WorkspaceKey.ForType(WorkspaceKind.Arrangement);
        if (!requested.Any(item => item.Key == arrangementKey))
        {
            requested.Insert(0, (arrangementKey, null));
            invalid = true;
        }

        ProjectPresentationNavigationKeyV4 activePresentation = state.ActiveTab;
        WorkspaceKey? activeKey = FromPresentationKey(activePresentation);
        if (activeKey is null || !requested.Any(item => item.Key == activeKey.Value))
        {
            activeKey = arrangementKey;
            invalid = true;
        }

        WorkspaceViewModel? arrangement = Workspaces.FirstOrDefault(item => item.Key == arrangementKey);
        if (arrangement is null)
        {
            arrangement = CreateWorkspaceForKey(arrangementKey);
            Workspaces.Insert(0, arrangement);
        }
        List<WorkspaceViewModel> restored = [arrangement];
        foreach ((WorkspaceKey key, ProjectPresentationNavigationViewV4? view) in requested)
        {
            if (key == arrangementKey) continue;
            if (key == activeKey.Value)
            {
                WorkspaceViewModel materialized = Workspaces.FirstOrDefault(item => item.Key == key
                    && item is not LazyWorkspaceViewModel) ?? CreateWorkspaceForKey(key);
                ApplyNavigationView(materialized, view);
                restored.Add(materialized);
            }
            else
            {
                WorkspaceViewModel placeholder = Workspaces.FirstOrDefault(item => item.Key == key
                    && item is LazyWorkspaceViewModel) ?? new LazyWorkspaceViewModel(key, WorkspaceHeader(key), view);
                restored.Add(placeholder);
            }
        }

        foreach (WorkspaceViewModel workspace in Workspaces.ToArray())
        {
            if (restored.Contains(workspace)) continue;
            workspace.Dispose();
        }
        Workspaces.Clear();
        foreach (WorkspaceViewModel workspace in restored) Workspaces.Add(workspace);
        RefreshOnionPresentations();

        ApplyNavigationView(arrangement, requested.First(item => item.Key == arrangementKey).View);
        WorkspaceViewModel active = restored.FirstOrDefault(item => item.Key == activeKey.Value) ?? arrangement;
        ActiveWorkspace = active;
        if (invalid)
            SetStatusMessage("Some saved workspace tabs were unavailable and were restored to Arrangement.");
    }

    private WorkspaceViewModel CreateWorkspaceForKey(WorkspaceKey key)
    {
        WorkspaceViewModel workspace = key.Kind switch
        {
            WorkspaceKind.Arrangement => new TimelineWorkspaceViewModel(key, "Arrangement", TimelineWorkspaceMode.Arrangement, ArrangementEditorSettings),
            WorkspaceKind.EventInstrumentLibrary => new LibraryWorkspaceViewModel(),
            WorkspaceKind.ProjectSettings => new SettingsWorkspaceViewModel(),
            WorkspaceKind.Diagnostics => new DiagnosticsWorkspaceViewModel(),
            WorkspaceKind.ConductorTrack => new TimelineWorkspaceViewModel(key, "Conductor Track", TimelineWorkspaceMode.Conductor, ArrangementEditorSettings),
            WorkspaceKind.SegmentEditor when key.ObjectId is MidoraId segmentId => new TimelineWorkspaceViewModel(key, "Segment", TimelineWorkspaceMode.Segment),
            WorkspaceKind.EventInstrumentEditor when key.ObjectId is MidoraId instrumentId => new InstrumentWorkspaceViewModel(
                instrumentId,
                Project?.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)?.Name ?? "Event Instrument"),
            WorkspaceKind.AllTracks => new AllTracksWorkspaceViewModel(mode =>
            {
                if (Persistence is { } persistence)
                    persistence.Presentation.Replace(persistence.Presentation.Current with { AllTracksMode = mode });
            }),
            _ => throw new InvalidDataException("Workspace navigation key cannot be materialized.")
        };
        if (workspace is TimelineWorkspaceViewModel { IsSegment: true } or InstrumentWorkspaceViewModel)
            workspace.EditorState = new(workspace, EditorStates);
        PrepareWorkspaceRuntimeState(workspace);
        workspace.Rebuild(Project!, _revision);
        workspace.PresentationDocumentRevision = Document?.PublicationRevision ?? -1;
        if (workspace is TimelineWorkspaceViewModel timeline) timeline.UpdatePlaybackCursor(Project!, CurrentTick);
        if (workspace is DiagnosticsWorkspaceViewModel diagnostics)
        {
            diagnostics.Replace(CompilerDiagnostics);
            diagnostics.SetScope(_diagnosticScopeWorkspace);
        }
        return workspace;
    }

    private static string WorkspaceHeader(WorkspaceKey key) => key.Kind switch
    {
        WorkspaceKind.Arrangement => "Arrangement",
        WorkspaceKind.EventInstrumentLibrary => "Event Instrument Library",
        WorkspaceKind.ProjectSettings => "Project Settings",
        WorkspaceKind.Diagnostics => "Diagnostics",
        WorkspaceKind.ConductorTrack => "Conductor Track",
        WorkspaceKind.SegmentEditor => "Segment",
        WorkspaceKind.EventInstrumentEditor => "Event Instrument",
        WorkspaceKind.AllTracks => "All Tracks",
        _ => "Workspace"
    };

    private void ApplyNavigationView(
        WorkspaceViewModel workspace,
        ProjectPresentationNavigationViewV4? view)
    {
        if (view is null) return;
        switch (workspace)
        {
            case TimelineWorkspaceViewModel timeline:
                if (view.StartTick is long start) timeline.StartTick = start;
                if (view.TickSpan is long span) timeline.TickSpan = span;
                if (view.FirstLane is int lane) timeline.FirstLane = lane;
                if (view.LaneHeight is double height) timeline.LaneHeight = height;
                if (view.LowerEditorVisible is bool visible && timeline.IsSegment) timeline.IsLowerEditorVisible = visible;
                if (view.LowerEditorHeight is double lower && (timeline.IsSegment || timeline.IsConductor))
                    timeline.BottomEditorRowHeight = new GridLength(lower);
                if (view.PrimaryFilter is int activeLane && timeline.IsSegment) timeline.ActiveParameterLaneIndex = activeLane;
                break;
            case InstrumentWorkspaceViewModel instrument:
                if (view.Page is int page) instrument.ActiveSectionIndex = page;
                if (view.SecondaryId is MidoraId voiceId && Project is { } project)
                    instrument.RestoreActiveSubVoice(voiceId, project, _revision);
                if (view.StartTick is long startTick) instrument.TimelineStartTick = startTick;
                if (view.TickSpan is long tickSpan) instrument.TimelineTickSpan = tickSpan;
                if (view.FirstLane is int firstLane) instrument.TimelineFirstLane = firstLane;
                if (view.LaneHeight is double laneHeight) instrument.TimelineLaneHeight = laneHeight;
                if (view.LowerEditorVisible is bool lowerVisible) instrument.IsLowerEditorVisible = lowerVisible;
                if (view.LowerEditorHeight is double lowerHeight) instrument.BottomEditorRowHeight = new GridLength(lowerHeight);
                if (view.PrimaryFilter is int editor) instrument.ActiveLowerEditorIndex = editor;
                break;
            case LibraryWorkspaceViewModel library:
                library.RestoreNavigationState(view.SearchText, view.SortMode);
                break;
            case DiagnosticsWorkspaceViewModel diagnostics:
                diagnostics.RestoreNavigationState(
                    view.SearchText,
                    view.PrimaryFilter,
                    view.SecondaryFilter,
                    view.TertiaryFilter,
                    view.Page);
                break;
            case AllTracksWorkspaceViewModel allTracks:
                if (view.StartTick is long allStart) allTracks.StartTick = allStart;
                if (view.TickSpan is long allSpan) allTracks.TickSpan = allSpan;
                if (view.FirstLane is int allLane) allTracks.FirstLane = allLane;
                if (view.LaneHeight is double allHeight) allTracks.RestoreVerticalView(allLaneOrZero(view.FirstLane), allHeight);
                break;
        }

        static int allLaneOrZero(int? lane) => lane ?? 0;
    }

    private void MaterializeLazyWorkspace(LazyWorkspaceViewModel lazy)
    {
        if (!Workspaces.Contains(lazy) || Project is null) return;
        int index = Workspaces.IndexOf(lazy);
        WorkspaceViewModel materialized = CreateWorkspaceForKey(lazy.Key);
        ApplyNavigationView(materialized, lazy.NavigationView);
        Workspaces[index] = materialized;
        lazy.Dispose();
        RefreshOnionPresentations();
    }
}

// The codec's validation is intentionally internal to Persistence.  Keep the
// desktop conversion small and avoid making the persistence implementation a
// public desktop dependency.
internal static class ProjectPresentationCodecForDesktop
{
    internal static ProjectPresentationNavigationStateV4 ValidateNavigationSnapshot(
        ProjectPresentationNavigationStateV4 state,
        MidoraProject project) => state;
}
