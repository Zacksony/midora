using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop.Presentation.Interaction;

public enum TimelineSelectionAction
{
    Copy, Cut, Delete, FlipHorizontal, FlipVertical, Scale, Transpose,
    BatchEdit, Humanize, Split, Join, Quantize, Properties, DeselectAll
}

public readonly record struct TimelineSelectionActionDescriptor(
    TimelineSelectionAction Action, string Label, string Icon, string? CompactIcon = null)
{
    public string ToolbarIcon => CompactIcon ?? Icon;
}

public static class TimelineSelectionActions
{
    public static IReadOnlyList<TimelineSelectionActionDescriptor> All { get; } = Array.AsReadOnly(new[]
    {
        new TimelineSelectionActionDescriptor(TimelineSelectionAction.Copy, "Copy", "Copy20Regular", "Copy16Regular"),
        new(TimelineSelectionAction.Cut, "Cut", "Cut20Regular", "Cut16Regular"),
        new(TimelineSelectionAction.Delete, "Delete", "Delete20Regular", "Delete16Regular"),
        new(TimelineSelectionAction.FlipHorizontal, "Flip Horizontal", "FlipHorizontal20Regular", "FlipHorizontal16Regular"),
        new(TimelineSelectionAction.FlipVertical, "Flip Vertical", "FlipVertical20Regular", "FlipVertical16Regular"),
        new(TimelineSelectionAction.Scale, "Scale", "ScaleFit20Regular", "ScaleFit16Regular"),
        new(TimelineSelectionAction.Transpose, "Transpose", "ArrowMaximizeVertical20Regular"),
        new(TimelineSelectionAction.BatchEdit, "Batch Edit", "DocumentSettings20Regular", "DocumentSettings16Regular"),
        new(TimelineSelectionAction.Humanize, "Humanize", "AnimalPawPrint20Regular", "AnimalPawPrint16Regular"),
        new(TimelineSelectionAction.Split, "Split", "SplitVertical20Regular", "SplitVertical16Regular"),
        new(TimelineSelectionAction.Join, "Join", "SquareDovetailJoint20Regular", "SquareDovetailJoint16Regular"),
        new(TimelineSelectionAction.Quantize, "Quantize", "TableMoveLeft20Regular", "TableMoveLeft16Regular"),
        new(TimelineSelectionAction.Properties, "Properties", "SettingsCogMultiple20Regular"),
        new(TimelineSelectionAction.DeselectAll, "Deselect All", "SelectObjectSkewDismiss20Regular")
    });

    public const double ButtonSize = 27;
    public const double IconSize = 15;
    public const double HeaderHeight = 28;
    public const double GroupGap = 2;
    public const double Padding = 4;
    public const int MaximumColumns = 6;
    public const int MaximumVisibleRows = 3;
    public const double MaximumBodyHeight = MaximumVisibleRows * ButtonSize + (MaximumVisibleRows - 1) * GroupGap + Padding * 2;
    public const string PinIcon = "Pin16Regular";
    public const string ResizeStartIcon = "ArrowExportRtl16Regular";
    public const string ResizeEndIcon = "ArrowExportLtr16Regular";
    public const string MoveIcon = "ArrowMove20Regular"; // No 16px variant in the pinned upstream revision.

    public static IReadOnlyList<TimelineSelectionAction> PrimaryActions { get; } = Array.AsReadOnly(new[]
    {
        TimelineSelectionAction.Properties, TimelineSelectionAction.DeselectAll
    });

    // A shared fixed-size layout vocabulary for the raster surface and Inst. UI.
    // Gestures + PrimaryActions form row 1; the two groups form rows 2 and 3.
    // Narrow hosts may wrap internally, but never enlarge the visible body past three rows.
    public static IReadOnlyList<IReadOnlyList<TimelineSelectionAction>> Groups { get; } = Array.AsReadOnly(new[]
    {
        Array.AsReadOnly(new[] { TimelineSelectionAction.Copy, TimelineSelectionAction.Cut, TimelineSelectionAction.Delete, TimelineSelectionAction.FlipHorizontal, TimelineSelectionAction.FlipVertical, TimelineSelectionAction.Scale }),
        Array.AsReadOnly(new[] { TimelineSelectionAction.Transpose, TimelineSelectionAction.BatchEdit, TimelineSelectionAction.Humanize, TimelineSelectionAction.Split, TimelineSelectionAction.Join, TimelineSelectionAction.Quantize })
    });

    public static int Columns(double width) => Math.Clamp((int)((width - Padding * 2) / ButtonSize), 1, MaximumColumns);
    public static double BodyHeight(int columns, int primaryCount)
    {
        int rows = (primaryCount + PrimaryActions.Count + columns - 1) / columns;
        foreach (var group in Groups) rows += (group.Count + columns - 1) / columns;
        return rows * ButtonSize + Groups.Count * GroupGap + Padding * 2;
    }

    // A constant-size type projection only. The Desktop command adapter performs
    // the final exact target/permission check through the original menu command.
    public static bool CanOffer(TimelineSelectionAction action, TimelineSelectionSnapshot selection, bool canEdit)
    {
        if (selection.Count == 0) return false;
        if (action == TimelineSelectionAction.DeselectAll) return true;
        if (!selection.MetricsAreComplete) return false;
        if (!canEdit && action is not (TimelineSelectionAction.Copy or TimelineSelectionAction.Properties)) return false;
        if (action is TimelineSelectionAction.Copy or TimelineSelectionAction.Cut or TimelineSelectionAction.Delete
            or TimelineSelectionAction.Properties) return true;
        bool notes = false, segments = false, points = false;
        foreach (var pair in selection.Metrics)
        {
            if (pair.Value.Count == 0) continue;
            notes |= pair.Key is TimelineItemKind.LogicalNote or TimelineItemKind.DirectMidiNote or TimelineItemKind.TemplateNote or TimelineItemKind.Velocity;
            segments |= pair.Key == TimelineItemKind.Segment;
            points |= pair.Key is TimelineItemKind.DirectMidiEvent or TimelineItemKind.LogicalParameterPoint or TimelineItemKind.TemplateEvent;
        }
        return action switch
        {
            TimelineSelectionAction.Humanize or TimelineSelectionAction.Split or TimelineSelectionAction.Join => notes,
            TimelineSelectionAction.FlipVertical or TimelineSelectionAction.Transpose => notes || segments,
            TimelineSelectionAction.Quantize => notes || points,
            _ => notes || segments || points
        };
    }
}
