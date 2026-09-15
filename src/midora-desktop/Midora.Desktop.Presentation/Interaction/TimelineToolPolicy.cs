using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using System.Windows.Input;

namespace Midora.Desktop.Presentation.Interaction;

public enum TimelinePointerIntent
{
    Default,
    Crosshair,
    Erase,
    Split,
    Move,
    ResizeHorizontal,
    ResizeVertical
}

public enum ArrangementSharedGroupDropZone
{
    Before,
    Body,
    After
}

public static class TimelineToolPolicy
{
    public const int RightDoubleClickIntervalMilliseconds = 300;
    public const double RightDoubleClickToleranceDips = 6;

    public static bool IsRightDoubleClick(
        long elapsedMilliseconds,
        double horizontalDistance,
        double verticalDistance) =>
        elapsedMilliseconds >= 0
        && elapsedMilliseconds < RightDoubleClickIntervalMilliseconds
        && double.IsFinite(horizontalDistance)
        && double.IsFinite(verticalDistance)
        && Math.Abs(horizontalDistance) <= RightDoubleClickToleranceDips
        && Math.Abs(verticalDistance) <= RightDoubleClickToleranceDips;

    public static TimelineToolMode ResolveDrawSelectToggle(TimelineToolMode current) =>
        current == TimelineToolMode.Select
            ? TimelineToolMode.Draw
            : TimelineToolMode.Select;

    public static long ResolvePositiveFixedStepCreationDelta(
        long pointerDeltaTicks,
        long operationStepTicks)
    {
        if (operationStepTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(operationStepTicks));
        if (pointerDeltaTicks <= 0) return 0;
        // Equivalent to ceil(pointerDelta / step) without overflowing the
        // numerator near long.MaxValue. The caller adds this delta to the
        // frozen initial Note length rather than snapping the final length.
        long units = checked(((pointerDeltaTicks - 1) / operationStepTicks) + 1);
        return checked(units * operationStepTicks);
    }

    public static long ResolveResizeMinimumLength(long currentLengthTicks, long operationStepTicks)
    {
        if (currentLengthTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(currentLengthTicks));
        if (operationStepTicks < 1)
            throw new ArgumentOutOfRangeException(nameof(operationStepTicks));
        return Math.Min(currentLengthTicks, operationStepTicks);
    }

    public const double DirectEditEdgeTolerancePixels = 5;

    public static ArrangementSharedGroupDropZone ResolveArrangementSharedGroupDropZone(
        double pointerY,
        double groupTop,
        double groupBottom,
        bool differentGroup,
        bool retainedJoin)
    {
        if (!double.IsFinite(pointerY)
            || !double.IsFinite(groupTop)
            || !double.IsFinite(groupBottom)
            || groupBottom <= groupTop)
        {
            throw new ArgumentOutOfRangeException(nameof(pointerY));
        }

        double exteriorStrip = differentGroup
            ? retainedJoin ? 4 : 12
            : 8;
        if (pointerY < groupTop + exteriorStrip)
        {
            return ArrangementSharedGroupDropZone.Before;
        }
        return pointerY > groupBottom - exteriorStrip
            ? ArrangementSharedGroupDropZone.After
            : ArrangementSharedGroupDropZone.Body;
    }

    public static double ResolveArrangementSharedGroupBoundaryY(
        double groupTop,
        double groupBottom,
        ArrangementSharedGroupDropZone zone) => zone switch
        {
            ArrangementSharedGroupDropZone.Before => groupTop,
            ArrangementSharedGroupDropZone.After => groupBottom,
            _ => throw new ArgumentOutOfRangeException(nameof(zone))
        };

    public static bool IsDirectEditingSurface(TimelineSurfaceMode surfaceMode) =>
        surfaceMode is TimelineSurfaceMode.Arrangement
            or TimelineSurfaceMode.PianoRoll
            or TimelineSurfaceMode.EventLanes
            or TimelineSurfaceMode.Conductor;

    public static bool StartsMarqueeBeforeItemHit(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        int clickCount) =>
        clickCount == 1
        && toolMode == TimelineToolMode.Select
        && IsDirectEditingSurface(surfaceMode);

    public static bool DefersControlSelectionToggleForPotentialDrag(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers,
        bool isSelected) =>
        isSelected
        && (modifiers & ModifierKeys.Control) != 0
        && CanBeginItemEdit(toolMode, surfaceMode, itemKind);

    public static (int FirstLane, int LastLaneExclusive) ResolveSemanticMarqueeLaneRange(
        int anchorLane,
        TimelineViewport currentViewport,
        double currentContentY)
    {
        currentViewport.Validate();
        int movingLane = currentViewport.YToLane(currentContentY);
        return (
            Math.Min(anchorLane, movingLane),
            checked(Math.Max(anchorLane, movingLane) + 1));
    }

    public static (double Minimum, double Maximum) ResolveSemanticMarqueeValueRange(
        double anchorNormalizedValue,
        double movingNormalizedValue)
    {
        if (!double.IsFinite(anchorNormalizedValue)
            || !double.IsFinite(movingNormalizedValue))
        {
            throw new ArgumentOutOfRangeException(nameof(anchorNormalizedValue));
        }
        double anchor = Math.Clamp(anchorNormalizedValue, 0, 1);
        double moving = Math.Clamp(movingNormalizedValue, 0, 1);
        return (Math.Min(anchor, moving), Math.Max(anchor, moving));
    }

    public static WorkspaceSelectionRangeMode ResolveMarqueeSelectionMode(
        ModifierKeys modifiers)
    {
        bool control = (modifiers & ModifierKeys.Control) != 0;
        bool alt = (modifiers & ModifierKeys.Alt) != 0;
        if (control && alt) return WorkspaceSelectionRangeMode.Toggle;
        if (alt) return WorkspaceSelectionRangeMode.Remove;
        return control || (modifiers & ModifierKeys.Shift) != 0
            ? WorkspaceSelectionRangeMode.Add
            : WorkspaceSelectionRangeMode.Replace;
    }

    public static bool ForcesValueTrace(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        MouseButton button,
        ModifierKeys modifiers) =>
        button == MouseButton.Left
        && (modifiers & ModifierKeys.Alt) != 0
        && (surfaceMode == TimelineSurfaceMode.Velocity
            || surfaceMode == TimelineSurfaceMode.EventLanes
                && toolMode == TimelineToolMode.Draw);

    public static bool RequestsHorizontalValueTrace(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        MouseButton button,
        ModifierKeys modifiers) =>
        toolMode == TimelineToolMode.Draw
        && surfaceMode == TimelineSurfaceMode.EventLanes
        && button == MouseButton.Right
        && (modifiers & ModifierKeys.Shift) != 0;

    public static bool RequestsTimeLockedPointCreation(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        MouseButton button,
        ModifierKeys modifiers) =>
        toolMode == TimelineToolMode.Draw
        && surfaceMode == TimelineSurfaceMode.EventLanes
        && button == MouseButton.Left
        && (modifiers & ModifierKeys.Shift) != 0;

    public static bool RequestsTimeLockedNotePlacement(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        MouseButton button,
        ModifierKeys modifiers) =>
        toolMode == TimelineToolMode.Draw
        && surfaceMode == TimelineSurfaceMode.PianoRoll
        && button == MouseButton.Left
        && (modifiers & ModifierKeys.Shift) != 0;

    public static bool RequestsTimeLockedItemMove(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        TimelineItemEditKind editKind,
        ModifierKeys modifiers) =>
        toolMode == TimelineToolMode.Draw
        && editKind == TimelineItemEditKind.Move
        && (modifiers & ModifierKeys.Shift) != 0
        && (surfaceMode == TimelineSurfaceMode.PianoRoll
                && itemKind is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote
            || surfaceMode == TimelineSurfaceMode.EventLanes
                && itemKind is TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
                    or TimelineItemKind.DirectMidiEvent
                    or TimelineItemKind.OpaqueMidiEvent);

    public static bool ForcesItemMove(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Alt) != 0
        && toolMode == TimelineToolMode.Draw
        && surfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll
        && IsDirectManipulationItem(itemKind);

    public static TimelineItemEditKind ResolveItemEditKind(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        ModifierKeys modifiers,
        bool isNearStart,
        bool isNearEnd)
    {
        if (itemKind is TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
                or TimelineItemKind.DirectMidiEvent
                or TimelineItemKind.OpaqueMidiEvent
                or TimelineItemKind.ConductorEvent
                or TimelineItemKind.Marker
                or TimelineItemKind.ProjectEndMarker
            || ForcesItemMove(toolMode, surfaceMode, itemKind, modifiers))
        {
            return TimelineItemEditKind.Move;
        }
        if (isNearStart) return TimelineItemEditKind.ResizeStart;
        return isNearEnd ? TimelineItemEditKind.ResizeEnd : TimelineItemEditKind.Move;
    }

    public static bool CanBeginItemEdit(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind) =>
        IsDirectEditingSurface(surfaceMode)
            ? toolMode == TimelineToolMode.Draw && IsDirectManipulationItem(itemKind)
            : toolMode != TimelineToolMode.Erase;

    public static bool SupportsCopyDrag(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        TimelineItemEditKind editKind) =>
        toolMode == TimelineToolMode.Draw
        && SupportsCopyDragCore(surfaceMode, itemKind, editKind);

    public static bool SupportsSelectionFloatingToolCopyDrag(
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        TimelineItemEditKind editKind) =>
        SupportsCopyDragCore(surfaceMode, itemKind, editKind);

    public static bool SupportsSelectionFloatingToolResize(TimelineItemKind itemKind) =>
        itemKind == TimelineItemKind.Segment
        || itemKind is TimelineItemKind.LogicalNote
            or TimelineItemKind.DirectMidiNote
            or TimelineItemKind.TemplateNote;

    private static bool SupportsCopyDragCore(
        TimelineSurfaceMode surfaceMode,
        TimelineItemKind itemKind,
        TimelineItemEditKind editKind) =>
        editKind == TimelineItemEditKind.Move
        && ((surfaceMode is TimelineSurfaceMode.Arrangement or TimelineSurfaceMode.PianoRoll
                && itemKind is TimelineItemKind.Segment
                    or TimelineItemKind.LogicalNote
                    or TimelineItemKind.DirectMidiNote
                    or TimelineItemKind.TemplateNote)
            || (surfaceMode == TimelineSurfaceMode.EventLanes
                && itemKind is TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
                    or TimelineItemKind.DirectMidiEvent
                    or TimelineItemKind.OpaqueMidiEvent));

    public static bool RequestsBackgroundCreation(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        int clickCount) =>
        IsDirectEditingSurface(surfaceMode)
            ? toolMode == TimelineToolMode.Draw
            : clickCount == 2 || toolMode == TimelineToolMode.Draw;

    public static TimelinePointerIntent GetPointerIntent(
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        bool isInContent,
        TimelineItemKind? itemKind,
        bool isNearHorizontalEdge,
        ModifierKeys modifiers = ModifierKeys.None)
    {
        if (!isInContent) return TimelinePointerIntent.Default;
        if (ForcesValueTrace(
                toolMode,
                surfaceMode,
                MouseButton.Left,
                modifiers))
        {
            return TimelinePointerIntent.Crosshair;
        }
        if (!IsDirectEditingSurface(surfaceMode))
        {
            if (toolMode == TimelineToolMode.Draw) return TimelinePointerIntent.Crosshair;
            if (toolMode == TimelineToolMode.Erase) return TimelinePointerIntent.Erase;
            if (itemKind is null) return TimelinePointerIntent.Default;
            return isNearHorizontalEdge
                ? TimelinePointerIntent.ResizeHorizontal
                : TimelinePointerIntent.Move;
        }
        if (toolMode == TimelineToolMode.Select) return TimelinePointerIntent.Crosshair;
        if (toolMode == TimelineToolMode.Erase) return TimelinePointerIntent.Erase;
        if (itemKind is null) return TimelinePointerIntent.Default;
        if (toolMode == TimelineToolMode.Split)
        {
            return itemKind == TimelineItemKind.Segment
                ? TimelinePointerIntent.Split
                : TimelinePointerIntent.Default;
        }
        if (toolMode != TimelineToolMode.Draw || !IsDirectManipulationItem(itemKind.Value))
        {
            return TimelinePointerIntent.Default;
        }
        if (itemKind is TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint or TimelineItemKind.DirectMidiEvent)
        {
            return TimelinePointerIntent.ResizeVertical;
        }
        if (itemKind == TimelineItemKind.OpaqueMidiEvent)
        {
            return TimelinePointerIntent.Move;
        }
        if (itemKind is TimelineItemKind.ConductorEvent
                or TimelineItemKind.Marker
                or TimelineItemKind.ProjectEndMarker)
        {
            return TimelinePointerIntent.Move;
        }
        if (ForcesItemMove(toolMode, surfaceMode, itemKind.Value, modifiers))
        {
            return TimelinePointerIntent.Move;
        }
        return isNearHorizontalEdge
            ? TimelinePointerIntent.ResizeHorizontal
            : TimelinePointerIntent.Move;
    }

    public static int FindPreferredDirectEditEdgeCandidate(
        IReadOnlyList<TimelineRenderItem> candidates,
        TimelineViewport viewport,
        double contentX,
        TimelineToolMode toolMode,
        TimelineSurfaceMode surfaceMode,
        TimelineSelectionSnapshot? selection = null,
        double tolerancePixels = DirectEditEdgeTolerancePixels)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        viewport.Validate();
        if (!double.IsFinite(contentX)
            || !double.IsFinite(tolerancePixels)
            || tolerancePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentX));
        }

        int bestIndex = -1;
        bool bestIsInteriorSide = false;
        double bestDistance = double.PositiveInfinity;
        bool bestIsPrimary = false;
        bool bestIsSelected = false;
        bool bestIsEnd = false;
        for (int index = 0; index < candidates.Count; index++)
        {
            TimelineRenderItem candidate = candidates[index];
            if (!CanBeginItemEdit(toolMode, surfaceMode, candidate.Kind))
            {
                continue;
            }

            Consider(viewport.TickToX(candidate.StartTick), isEnd: false);
            Consider(viewport.TickToX(candidate.EndTick), isEnd: true);

            void Consider(double edgeX, bool isEnd)
            {
                double distance = Math.Abs(contentX - edgeX);
                if (distance > tolerancePixels)
                {
                    return;
                }

                // At a shared boundary, the pointer side resolves the ambiguity:
                // left edits the left item's end; right edits the right item's start.
                // Exact ties prefer current selection, then the ending edge so a
                // half-open [start,end) interval cannot hide that resize affordance.
                bool isInteriorSide = isEnd ? contentX <= edgeX : contentX >= edgeX;
                bool isPrimary = selection?.Primary == candidate.Id
                    || selection is null && candidate.State.HasFlag(TimelineItemState.Primary);
                bool isSelected = selection?.Contains(candidate.Id)
                    ?? candidate.State.HasFlag(TimelineItemState.Selected);
                if (bestIndex >= 0
                    && CompareCandidate(
                        isInteriorSide,
                        distance,
                        isPrimary,
                        isSelected,
                        isEnd,
                        bestIsInteriorSide,
                        bestDistance,
                        bestIsPrimary,
                        bestIsSelected,
                        bestIsEnd) >= 0)
                {
                    return;
                }

                bestIndex = index;
                bestIsInteriorSide = isInteriorSide;
                bestDistance = distance;
                bestIsPrimary = isPrimary;
                bestIsSelected = isSelected;
                bestIsEnd = isEnd;
            }
        }
        return bestIndex;
    }

    private static int CompareCandidate(
        bool isInteriorSide,
        double distance,
        bool isPrimary,
        bool isSelected,
        bool isEnd,
        bool otherIsInteriorSide,
        double otherDistance,
        bool otherIsPrimary,
        bool otherIsSelected,
        bool otherIsEnd)
    {
        int byInteriorSide = otherIsInteriorSide.CompareTo(isInteriorSide);
        if (byInteriorSide != 0) return byInteriorSide;
        int byDistance = distance.CompareTo(otherDistance);
        if (byDistance != 0) return byDistance;
        int byPrimary = otherIsPrimary.CompareTo(isPrimary);
        if (byPrimary != 0) return byPrimary;
        int bySelection = otherIsSelected.CompareTo(isSelected);
        if (bySelection != 0) return bySelection;
        return otherIsEnd.CompareTo(isEnd);
    }

    private static bool IsDirectManipulationItem(TimelineItemKind itemKind) =>
        itemKind is TimelineItemKind.Segment
            or TimelineItemKind.LogicalNote
            or TimelineItemKind.DirectMidiNote
            or TimelineItemKind.TemplateNote
            or TimelineItemKind.TempoPoint or TimelineItemKind.LogicalParameterPoint
            or TimelineItemKind.DirectMidiEvent
            or TimelineItemKind.OpaqueMidiEvent
            or TimelineItemKind.ConductorEvent
            or TimelineItemKind.Marker
            or TimelineItemKind.ProjectEndMarker;
}
