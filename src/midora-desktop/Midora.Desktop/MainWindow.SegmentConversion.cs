using System.Text;
using System.Windows;
using Midora.Application;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

public partial class MainWindow
{
    internal static bool RequiresArrangementSegmentConversion(TimelineRenderSnapshot snapshot,
        IReadOnlyCollection<MidoraId> selected, TimelineItemEditEventArgs edit)
    {
        if (edit.EditKind != TimelineItemEditKind.Move) return false;
        if (edit.LaneDelta == 0 && !edit.CopyRequested) return false;
        ArrangementLaneKind? firstKind = null;
        foreach (MidoraId id in selected)
        {
            if (!snapshot.TryGetItem(id, out TimelineRenderItem item) || item.Kind != TimelineItemKind.Segment)
                continue;
            int target = checked(item.Lane + edit.LaneDelta);
            if ((uint)item.Lane >= (uint)snapshot.ArrangementLanes.Count
                || (uint)target >= (uint)snapshot.ArrangementLanes.Count) return true;
            ArrangementLaneKind sourceKind = snapshot.ArrangementLanes[item.Lane].Kind;
            if (sourceKind != snapshot.ArrangementLanes[target].Kind) return true;
            if ((edit.CopyRequested || edit.LaneDelta != 0)
                && firstKind.HasValue && firstKind.Value != sourceKind) return true;
            firstKind = sourceKind;
        }
        return false;
    }

    private Task TransferArrangementSegmentsAsync(TimelineWorkspaceViewModel workspace,
        IReadOnlyCollection<MidoraId> ids, MidoraId primaryId, MidoraId targetTrackId, long primaryTick, bool copy)
    {
        ProjectDocumentSession document = _session.Document!;
        return ExecuteSegmentConversionAsync(workspace, (token, progress) =>
            SegmentConversionService.AnalyzeTransfer(document, ids, primaryId, targetTrackId,
                primaryTick, copy, token, progress));
    }

    private Task PasteArrangementSegmentsAsync(TimelineWorkspaceViewModel workspace,
        ProjectDocumentSession document, MidoraProject project, ProjectObjectClipboardPayload payload)
    {
        MidoraId targetTrackId = ResolveArrangementSegmentPasteTrack(project, workspace);
        long tick = workspace.EditCursorTick ?? 0;
        return ExecuteSegmentConversionAsync(workspace, (token, progress) =>
            SegmentConversionService.AnalyzePaste(document, payload, targetTrackId, tick, token, progress));
    }

    internal static MidoraId ResolveArrangementSegmentPasteTrack(MidoraProject project,
        TimelineWorkspaceViewModel workspace)
    {
        if (workspace.GetArrangementLane(workspace.ActiveLane ?? -1) is
            { CanContainSegments: true, ObjectId: MidoraId activeTrack }) return activeTrack;
        if (workspace.Selection.Primary is MidoraId selected)
        {
            if (TimelineWorkspaceViewModel.FindSegment(project, selected) is { } logical) return logical.Track.Id;
            if (TimelineWorkspaceViewModel.FindMidiSegment(project, selected) is { } direct) return direct.Track.Id;
        }
        return project.TracksInArrangementOrder().FirstOrDefault().TrackId is MidoraId first && first != default
            ? first : throw new InvalidOperationException("Create and select a destination Track before pasting Segments.");
    }

    private async Task ExecuteSegmentConversionAsync(TimelineWorkspaceViewModel workspace,
        Func<CancellationToken, IProgress<TimelineEditPreparationProgress>, SegmentConversionPlan> analyze)
    {
        SegmentConversionPlan? plan = null;
        var surface = _lastTimelineCommandSurface;
        try
        {
            bool reviewed = await RunOperationAsync("Review Segment Transfer", async token =>
            {
                using var progress = new DispatcherCoalescingProgress<TimelineEditPreparationProgress>(
                    Dispatcher, TimeSpan.FromMilliseconds(100), value => _session.ActiveForegroundTask?.Report(
                        FormatTimelineEditPreparationProgress(value), value.IsIndeterminate ? null : value.OverallFraction));
                plan = await Task.Run(() => analyze(token, progress), token);
                token.ThrowIfCancellationRequested();
                progress.Flush();
            }, canCancel: true);
            if (!reviewed || plan is null) return;
            if (plan.RequiresConfirmation && MessageDialog.Show(this,
                    FormatSegmentConversionLosses(plan.Losses), "Convert Segments",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            IProjectEditCommand command = plan.CreateCommand(confirmDataLoss: plan.RequiresConfirmation);
            await ExecuteStagedProjectOperationAsync(command.Name, command, workspace, surface);
        }
        finally
        {
            plan?.Dispose();
            RestoreModalCommandFocus(workspace, surface);
        }
    }

    internal static string FormatSegmentConversionLosses(SegmentConversionLossSummary losses)
    {
        var text = new StringBuilder("Converting these Segments keeps their complete notes and crop windows, including hidden notes. The following data cannot be retained:\n");
        Add("Logical parameter points", losses.LogicalParameterPoints);
        Add("Logical parameter lanes", losses.LogicalParameterLanes);
        Add("MIDI channel events", losses.MidiChannelEvents);
        Add("Imported Meta / SysEx events", losses.OpaqueEvents);
        Add("Non-zero Note Off velocities", losses.NonZeroNoteOffVelocities);
        Add("Duplicate notes at the same tick and key", losses.DuplicateNotes);
        return text.Append("\nDiscard the listed data and continue? Cancel leaves the source unchanged.").ToString();
        void Add(string label, long count) { if (count != 0) text.Append("\n• ").Append(label).Append(": ").Append(count.ToString("N0")); }
    }
}
