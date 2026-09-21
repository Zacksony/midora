using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Controls;

public enum TemplateTimelineMarker { TemplateEnd, LoopStart, LoopEnd, PreRoll }

public sealed class TemplateMarkerEditEventArgs(TemplateTimelineMarker marker, long oldTick, long tick, long revision) : EventArgs
{
    public TemplateTimelineMarker Marker { get; } = marker;
    public long OldTick { get; } = oldTick;
    public long Tick { get; } = tick;
    public long Revision { get; } = revision;
}

internal readonly record struct TemplateMarkerHandle(TemplateTimelineMarker Marker, long Tick, double AnchorX, Rect Bounds);

public sealed partial class TimelineSurface
{
    public static readonly DependencyProperty MinimumTemplateLengthProperty = DependencyProperty.Register(
        nameof(MinimumTemplateLength), typeof(long?), typeof(TimelineSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, TemplateMetadataChanged));
    public static readonly DependencyProperty TemplateEditRevisionProperty = DependencyProperty.Register(
        nameof(TemplateEditRevision), typeof(long), typeof(TimelineSurface),
        new FrameworkPropertyMetadata(0L, TemplateMetadataChanged));
    public long? MinimumTemplateLength { get => (long?)GetValue(MinimumTemplateLengthProperty); set => SetValue(MinimumTemplateLengthProperty, value); }
    public long TemplateEditRevision { get => (long)GetValue(TemplateEditRevisionProperty); set => SetValue(TemplateEditRevisionProperty, value); }
    public static readonly DependencyProperty TemplatePreRollTicksProperty = RegisterTemplateMetadata(nameof(TemplatePreRollTicks), typeof(long), 0L);
    public static readonly DependencyProperty TemplateLoopStartTickProperty = RegisterTemplateMetadata(nameof(TemplateLoopStartTick), typeof(long?), null);
    public static readonly DependencyProperty TemplateLoopEndTickProperty = RegisterTemplateMetadata(nameof(TemplateLoopEndTick), typeof(long?), null);
    public static readonly DependencyProperty TemplateLoopEditingEnabledProperty = RegisterTemplateMetadata(nameof(TemplateLoopEditingEnabled), typeof(bool), false);
    public long TemplatePreRollTicks { get => (long)GetValue(TemplatePreRollTicksProperty); set => SetValue(TemplatePreRollTicksProperty, value); }
    public long? TemplateLoopStartTick { get => (long?)GetValue(TemplateLoopStartTickProperty); set => SetValue(TemplateLoopStartTickProperty, value); }
    public long? TemplateLoopEndTick { get => (long?)GetValue(TemplateLoopEndTickProperty); set => SetValue(TemplateLoopEndTickProperty, value); }
    public bool TemplateLoopEditingEnabled { get => (bool)GetValue(TemplateLoopEditingEnabledProperty); set => SetValue(TemplateLoopEditingEnabledProperty, value); }

    private static readonly DependencyPropertyKey TemplatePreviewEndTickPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(TemplatePreviewEndTick), typeof(long?), typeof(TimelineSurface), new FrameworkPropertyMetadata(null));
    private static readonly DependencyPropertyKey TemplatePreviewPreRollTicksPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(TemplatePreviewPreRollTicks), typeof(long), typeof(TimelineSurface), new FrameworkPropertyMetadata(0L));
    private static readonly DependencyPropertyKey TemplatePreviewLoopStartTickPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(TemplatePreviewLoopStartTick), typeof(long?), typeof(TimelineSurface), new FrameworkPropertyMetadata(null));
    private static readonly DependencyPropertyKey TemplatePreviewLoopEndTickPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(TemplatePreviewLoopEndTick), typeof(long?), typeof(TimelineSurface), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty TemplatePreviewEndTickProperty = TemplatePreviewEndTickPropertyKey.DependencyProperty;
    public static readonly DependencyProperty TemplatePreviewPreRollTicksProperty = TemplatePreviewPreRollTicksPropertyKey.DependencyProperty;
    public static readonly DependencyProperty TemplatePreviewLoopStartTickProperty = TemplatePreviewLoopStartTickPropertyKey.DependencyProperty;
    public static readonly DependencyProperty TemplatePreviewLoopEndTickProperty = TemplatePreviewLoopEndTickPropertyKey.DependencyProperty;
    public long? TemplatePreviewEndTick => (long?)GetValue(TemplatePreviewEndTickProperty);
    public long TemplatePreviewPreRollTicks => (long)GetValue(TemplatePreviewPreRollTicksProperty);
    public long? TemplatePreviewLoopStartTick => (long?)GetValue(TemplatePreviewLoopStartTickProperty);
    public long? TemplatePreviewLoopEndTick => (long?)GetValue(TemplatePreviewLoopEndTickProperty);
    public event EventHandler<TemplateMarkerEditEventArgs>? TemplateMarkerEditCompleted;

    private TemplateMarkerDrag? _templateMarkerDrag;
    private TimelineTemplateMarkerCapCache? _templateMarkerCaps;
    private sealed record TemplateMarkerDrag(TemplateTimelineMarker Marker, long Original, long Minimum, long Maximum,
        long TemplateLength, long Revision, object? Owner,
        Point Down, double PixelsPerTick, long Step, bool Bars, ProjectTimeSignatureMap? Map)
    {
        public long Tick { get; set; } = Original;
    }

    private static DependencyProperty RegisterTemplateMetadata(string name, Type type, object? value) => DependencyProperty.Register(
        name, type, typeof(TimelineSurface), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender, TemplateMetadataChanged));
    private static void TemplateMetadataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var surface = (TimelineSurface)d;
        surface.CancelTemplateMarkerDrag();
        surface.RefreshTemplateMarkerPreview();
    }

    public bool IsTemplateRulerPoint(Point point) => SurfaceMode == TimelineSurfaceMode.PianoRoll
        && RangeEndTick is > 0 && point.X >= GetLaneHeaderWidth() && point.X <= ActualWidth
        && point.Y >= 0 && point.Y < GetRulerHeight();

    public long? GetTemplateRulerTick(Point point)
    {
        if (!IsTemplateRulerPoint(point) || !TryCreateViewport(out var viewport)) return null;
        try { return viewport.XToTick(point.X - GetLaneHeaderWidth()); }
        catch (OverflowException) { return null; }
    }

    public TemplateTimelineMarker? HitTemplateMarker(Point point)
    {
        if (!IsTemplateRulerPoint(point)) return null;
        Span<TemplateMarkerHandle> handles = stackalloc TemplateMarkerHandle[4];
        int count = GetTemplateMarkerHandles(handles);
        for (int i = 0; i < count; i++)
            if (handles[i].Bounds.Contains(point)) return handles[i].Marker;
        return null;
    }

    // Caps are laid out together so coincident/nearby ticks remain individually
    // targetable. Only the cap shifts; its stem still points to the true tick.
    internal int GetTemplateMarkerHandles(Span<TemplateMarkerHandle> handles)
    {
        if (handles.Length < 4) throw new ArgumentException("Four handle slots are required.", nameof(handles));
        if (MinimumTemplateLength is not > 0 || SurfaceMode != TimelineSurfaceMode.PianoRoll
            || RangeEndTick is not > 0 || !TryCreateViewport(out var viewport)) return 0;
        double left = GetLaneHeaderWidth(), right = ActualWidth;
        const double slot = 10;
        if (right - left < slot * 4) return 0;
        int count = 0;
        for (int kind = 0; kind < 4; kind++)
        {
            var marker = (TemplateTimelineMarker)kind;
            long? tick = _templateMarkerDrag?.Marker == marker ? _templateMarkerDrag.Tick : GetTemplateMarkerTick(marker);
            if (tick is null || marker == TemplateTimelineMarker.PreRoll && tick == 0 && _templateMarkerDrag?.Marker != marker) continue;
            double x = left + viewport.TickToX(tick.Value);
            if (x < left || x > right) continue;
            handles[count++] = new(marker, tick.Value, x, Rect.Empty);
        }
        for (int i = 1; i < count; i++)
            for (int j = i; j > 0 && handles[j].AnchorX < handles[j - 1].AnchorX; j--)
                (handles[j], handles[j - 1]) = (handles[j - 1], handles[j]);
        double previous = left - slot;
        for (int i = 0; i < count; i++)
        {
            double x = Math.Max(previous + slot, Math.Clamp(handles[i].AnchorX - slot / 2, left, right - slot));
            handles[i] = handles[i] with { Bounds = new(x, Math.Max(0, GetRulerHeight() - 11), slot, 10) };
            previous = x;
        }
        double next = right;
        for (int i = count - 1; i >= 0; i--)
        {
            Rect bounds = handles[i].Bounds;
            bounds.X = Math.Min(bounds.X, next - slot);
            handles[i] = handles[i] with { Bounds = bounds };
            next = bounds.X;
        }
        return count;
    }

    public long? GetTemplateMarkerTick(TemplateTimelineMarker marker) => marker switch
    {
        TemplateTimelineMarker.TemplateEnd => RangeEndTick,
        TemplateTimelineMarker.LoopStart => TemplateLoopStartTick,
        TemplateTimelineMarker.LoopEnd => TemplateLoopEndTick,
        TemplateTimelineMarker.PreRoll => TemplatePreRollTicks,
        _ => null
    };

    internal static bool TryGetTemplateMarkerBounds(TemplateTimelineMarker marker, long length, long minimumTemplate,
        long? loopStart, long? loopEnd, out long minimum, out long maximum)
    {
        minimum = 0; maximum = length;
        if (length <= 0) return false;
        switch (marker)
        {
            case TemplateTimelineMarker.TemplateEnd: minimum = minimumTemplate; maximum = long.MaxValue; break;
            case TemplateTimelineMarker.LoopStart:
                if (loopEnd is <= 0) return false;
                maximum = Math.Min(length, loopEnd ?? length) - 1; break;
            case TemplateTimelineMarker.LoopEnd:
                if (loopStart == long.MaxValue) return false;
                minimum = Math.Max(1, (loopStart ?? 0) + 1); break;
            case TemplateTimelineMarker.PreRoll: break;
            default: return false;
        }
        return minimum >= 0 && maximum >= minimum;
    }

    private bool BeginTemplateMarkerDrag(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsMouseCaptured
            || HitTemplateMarker(InteractionPosition(e)) is not { } marker || !TryCreateViewport(out var viewport)) return false;
        // An incompatible handle must not fall through into ruler navigation.
        if (!CanEdit || (marker is TemplateTimelineMarker.LoopStart or TemplateTimelineMarker.LoopEnd) && !TemplateLoopEditingEnabled)
            return true;
        if (!TryGetTemplateMarkerBounds(marker, RangeEndTick!.Value, MinimumTemplateLength!.Value,
            TemplateLoopStartTick, TemplateLoopEndTick, out long minimum, out long maximum)) return true;
        if (ContextMenu is { IsOpen: true } menu) menu.IsOpen = false;
        CancelPendingRightGesture(cancelDelayedMenu: true);
        Focus();
        _templateMarkerDrag = new(marker, GetTemplateMarkerTick(marker)!.Value, minimum, maximum, RangeEndTick.Value, TemplateEditRevision,
            DataContext, InteractionPosition(e), viewport.PixelsPerTick, OperationStepTicks, OperationUsesBars, TimeSignatureMap);
        if (!CaptureMouse()) { _templateMarkerDrag = null; return true; }
        Cursor = Cursors.SizeWE;
        InvalidateVisual();
        return true;
    }

    internal static long CalculateTemplateLength(long original, long minimum, long delta,
        long step, bool bars = false, ProjectTimeSignatureMap? map = null)
    {
        long target = (long)Int128.Clamp((Int128)original + delta, 0, long.MaxValue);
        long snapped;
        try { snapped = ProjectTimelineGrid.SnapDelta(delta, target, Math.Max(1, step), bars, map); }
        catch (OverflowException) { return delta < 0 ? minimum : long.MaxValue; }
        return (long)Int128.Clamp((Int128)original + snapped, minimum, long.MaxValue);
    }

    private bool UpdateTemplateMarkerDrag(Point point)
    {
        if (_templateMarkerDrag is not { } drag) return false;
        if (!CanEdit || drag.Revision != TemplateEditRevision || drag.TemplateLength != RangeEndTick
            || drag.Original != GetTemplateMarkerTick(drag.Marker)
            || !ReferenceEquals(drag.Owner, DataContext)) { CancelTemplateMarkerDrag(); return true; }
        double distance = (point.X - drag.Down.X) / drag.PixelsPerTick;
        if (!double.IsFinite(distance)) { CancelTemplateMarkerDrag(); return true; }
        long next = Math.Abs(point.X - drag.Down.X) < SystemParameters.MinimumHorizontalDragDistance
            ? drag.Original : Math.Min(drag.Maximum, CalculateTemplateLength(drag.Original, drag.Minimum,
                TimelineTickMath.RoundSignedDistance(distance), drag.Step, drag.Bars, drag.Map));
        if (drag.Tick != next) { drag.Tick = next; RefreshTemplateMarkerPreview(); InvalidateVisual(); }
        return true;
    }

    private void FinishTemplateMarkerDrag(Point point)
    {
        UpdateTemplateMarkerDrag(point);
        var drag = _templateMarkerDrag;
        CancelTemplateMarkerDrag();
        if (drag is not null && drag.Tick != drag.Original)
            TemplateMarkerEditCompleted?.Invoke(this, new(drag.Marker, drag.Original, drag.Tick, drag.Revision));
    }

    private void CancelTemplateMarkerDrag()
    {
        if (_templateMarkerDrag is null) return;
        _templateMarkerDrag = null;
        RefreshTemplateMarkerPreview();
        Cursor = Cursors.Arrow;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    private void RefreshTemplateMarkerPreview()
    {
        SetValue(TemplatePreviewEndTickPropertyKey, _templateMarkerDrag?.Marker == TemplateTimelineMarker.TemplateEnd ? _templateMarkerDrag.Tick : RangeEndTick);
        SetValue(TemplatePreviewPreRollTicksPropertyKey, _templateMarkerDrag?.Marker == TemplateTimelineMarker.PreRoll ? _templateMarkerDrag.Tick : TemplatePreRollTicks);
        SetValue(TemplatePreviewLoopStartTickPropertyKey, _templateMarkerDrag?.Marker == TemplateTimelineMarker.LoopStart ? _templateMarkerDrag.Tick : TemplateLoopStartTick);
        SetValue(TemplatePreviewLoopEndTickPropertyKey, _templateMarkerDrag?.Marker == TemplateTimelineMarker.LoopEnd ? _templateMarkerDrag.Tick : TemplateLoopEndTick);
    }

    public static string TemplateMarkerName(TemplateTimelineMarker marker) => marker switch
    {
        TemplateTimelineMarker.TemplateEnd => "Template Length",
        TemplateTimelineMarker.LoopStart => "Loop Start",
        TemplateTimelineMarker.LoopEnd => "Loop End",
        TemplateTimelineMarker.PreRoll => "Pre-Roll",
        _ => throw new ArgumentOutOfRangeException(nameof(marker))
    };

    private Brush TemplateMarkerBrush(TemplateTimelineMarker marker) => marker switch
    {
        TemplateTimelineMarker.TemplateEnd => Brush("Brush.Info", Color.FromRgb(95, 166, 231)),
        TemplateTimelineMarker.PreRoll => Brush("Brush.Timeline.PreRoll", Color.FromRgb(199, 138, 255)),
        _ => Brush("Brush.Warning", Color.FromRgb(232, 179, 75))
    };

    private void DrawTemplateMarkerHandles(DrawingContext context, TimelineViewport viewport)
    {
        Span<TemplateMarkerHandle> handles = stackalloc TemplateMarkerHandle[4];
        int count = GetTemplateMarkerHandles(handles);
        if (count == 0 && _templateMarkerDrag is null) return;
        double left = GetLaneHeaderWidth();
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        context.PushClip(new RectangleGeometry(new Rect(left, 0, Math.Max(0, ActualWidth - left), ActualHeight)));
        for (int i = 0; i < count; i++)
        {
            var handle = handles[i];
            Brush color = TemplateMarkerBrush(handle.Marker);
            Rect cap = handle.Bounds; cap.Inflate(-1, 0);
            _templateMarkerCaps ??= new(InvalidateVisual);
            var image = _templateMarkerCaps.Get(handle.Marker, cap.Size, color, dpi);
            context.DrawImage(image, TimelineTemplateMarkerCapCache.Destination(image, cap, dpi));
            context.DrawLine(new Pen(color, 1), new(cap.X + cap.Width / 2, cap.Bottom), new(handle.AnchorX, GetRulerHeight()));
            if (handle.Marker == TemplateTimelineMarker.TemplateEnd)
                context.DrawLine(new Pen(color, 1), new(handle.AnchorX, GetRulerHeight()), new(handle.AnchorX, ActualHeight));
        }
        if (_templateMarkerDrag is { } drag)
        {
            double x = left + viewport.TickToX(drag.Tick);
            Brush color = TemplateMarkerBrush(drag.Marker);
            string caption = string.Create(CultureInfo.InvariantCulture, $"{TemplateMarkerName(drag.Marker)} {drag.Tick} · {drag.Tick - drag.Original:+0;-0;0} Ticks");
            var text = GetFormattedText(caption, color, 11, FontWeights.Normal);
            double tx = Math.Clamp(x + 10, left + 3, Math.Max(left + 3, ActualWidth - text.Width - 8));
            context.DrawRoundedRectangle(Brush("Brush.Surface.1", Color.FromRgb(16, 18, 22)), null,
                new(tx - 3, GetRulerHeight() + 3, text.Width + 6, text.Height + 6), 3, 3);
            context.DrawText(text, new(tx, GetRulerHeight() + 6));
        }
        context.Pop();
    }
}
