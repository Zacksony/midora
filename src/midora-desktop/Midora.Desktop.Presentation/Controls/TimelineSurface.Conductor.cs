using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Rendering;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;

namespace Midora.Desktop.Presentation.Controls;

public sealed class TimelineEventPointTraceEventArgs(
    IReadOnlyList<TimelineValueTracePoint> trace, long stepTicks, bool useBars,
    ProjectTimeSignatureMap? timeSignatureMap, long? rangeStartTick, long? rangeEndTick) : RoutedEventArgs
{
    public IReadOnlyList<TimelineValueTracePoint> Trace { get; } = trace;
    public long StepTicks { get; } = stepTicks;
    public bool UseBars { get; } = useBars;
    public ProjectTimeSignatureMap? TimeSignatureMap { get; } = timeSignatureMap;
    public long? RangeStartTick { get; } = rangeStartTick;
    public long? RangeEndTick { get; } = rangeEndTick;
    public IEnumerable<TimelineValueTracePoint> Sample(CancellationToken token = default) =>
        TimelineValueTraceSampler.EnumerateSamples(Trace, StepTicks, UseBars,
            TimeSignatureMap, RangeStartTick, RangeEndTick, token);
}

public sealed partial class TimelineSurface
{
    private static readonly SemaphoreSlim ConductorQuerySlots = new(Math.Max(1, Math.Min(2, Environment.ProcessorCount - 2)));
    private ConductorHitKey? _conductorHitKey;
    private TimelineRenderItem? _conductorHitResult;
    private bool _conductorHitReady;
    private CancellationTokenSource? _conductorHitCancellation;
    private readonly HashSet<TimelineRasterCacheKey> _conductorLabelRequests = [];
    private ConductorRulerKey? _conductorRulerKey;
    private TimelineRenderItem[] _conductorRulerItems = [];
    private bool _conductorRulerReady;
    private CancellationTokenSource? _conductorRulerCancellation;
    private PendingConductorPress? _pendingConductorPress;
    private Point? _replayingConductorPosition;
    private ModifierKeys? _replayingConductorModifiers;
    private bool _replayingConductorMove;

    private ModifierKeys ConductorGestureModifiers => _replayingConductorModifiers ?? Keyboard.Modifiers;
    private Point InteractionPosition(MouseEventArgs args) => _replayingConductorPosition ?? args.GetPosition(this);

    private bool TryDeferConductorPress(MouseButtonEventArgs args, Point point, TimelineViewport viewport)
    {
        if (args.ChangedButton != MouseButton.Left
            || Snapshot?.ConductorSource is null || !CanEdit || ToolMode is not (TimelineToolMode.Draw or TimelineToolMode.Erase)
            || point.X < GetLaneHeaderWidth() || point.Y < GetRulerHeight()
            || point.X >= ActualWidth || point.Y >= ActualHeight
            || ToolMode == TimelineToolMode.Draw && (ConductorGestureModifiers & ModifierKeys.Alt) != 0)
            return false;
        TryHitConductorPoint(point, viewport, out _);
        if (!_exactQueryPending) return false;
        _pendingConductorPress = new(args, point, ConductorGestureModifiers, ToolMode, Snapshot,
            GestureProjection(viewport), SelectionSnapshot?.Revision);
        CaptureMouse();
        Cursor = Cursors.Wait;
        return true;
    }

    private void ReplayConductorPress()
    {
        PendingConductorPress? pending = _pendingConductorPress;
        if (pending is null) return;
        _pendingConductorPress = null;
        Cursor = Cursors.Arrow;
        if (!ReferenceEquals(Snapshot, pending.Snapshot) || ToolMode != pending.Tool || !CanEdit
            || SelectionSnapshot?.Revision != pending.SelectionRevision
            || !TryCreateViewport(out TimelineViewport viewport) || GestureProjection(viewport) != pending.Projection)
        { ReleaseMouseCapture(); return; }
        try
        {
            _replayingConductorModifiers = pending.Modifiers;
            _replayingConductorPosition = pending.DownPosition;
            pending.Down.Handled = false;
            OnMouseDown(pending.Down);
            if (pending.LastMove is not null)
            {
                _replayingConductorMove = true;
                foreach (Point position in pending.MovePositions)
                {
                    _replayingConductorPosition = position;
                    OnMouseMove(pending.LastMove);
                }
                _replayingConductorMove = false;
            }
            if (pending.Up is not null)
            {
                _replayingConductorPosition = pending.LastPosition;
                pending.Up.Handled = false;
                OnMouseUp(pending.Up);
            }
        }
        finally
        {
            _replayingConductorPosition = null;
            _replayingConductorModifiers = null;
            _replayingConductorMove = false;
        }
    }

    private ConductorGestureProjection GestureProjection(TimelineViewport viewport) => new(viewport,
        _valueViewMinimum, _valueViewMaximum, ValueAxisMinimum, ValueAxisMaximum);
    private readonly record struct ConductorGestureProjection(TimelineViewport Viewport,
        double ViewMinimum, double ViewMaximum, double AxisMinimum, double AxisMaximum);

    private sealed class PendingConductorPress(MouseButtonEventArgs down, Point point,
        ModifierKeys modifiers, TimelineToolMode tool, TimelineRenderSnapshot snapshot,
        ConductorGestureProjection projection, long? selectionRevision)
    {
        public MouseButtonEventArgs Down { get; } = down;
        public Point DownPosition { get; } = point;
        public Point LastPosition { get; set; } = point;
        public ModifierKeys Modifiers { get; } = modifiers;
        public TimelineToolMode Tool { get; } = tool;
        public TimelineRenderSnapshot Snapshot { get; } = snapshot;
        public ConductorGestureProjection Projection { get; } = projection;
        public long? SelectionRevision { get; } = selectionRevision;
        public MouseEventArgs? LastMove { get; set; }
        public List<Point> MovePositions { get; } = [];
        public MouseButtonEventArgs? Up { get; set; }
    }

    private void DrawConductorMetaTiles(DrawingContext context, TimelineViewport viewport,
        Brush normalBrush, Brush selectedBrush, Brush textBrush, Brush borderBrush,
        double header, double ruler)
    {
        if (Snapshot is not { ConductorSource: not null } snapshot) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double scale = viewport.PixelsPerTick * dpi.DpiScaleX;
        double height = LaneHeight * dpi.DpiScaleY;
        int gutterX = TimelineEventPointTileRasterizer.GetGutter(dpi.DpiScaleX);
        int gutterY = TimelineEventPointTileRasterizer.GetGutter(dpi.DpiScaleY);
        int dpiX = (int)Math.Round(dpi.DpiScaleX * 1024);
        int dpiY = (int)Math.Round(dpi.DpiScaleY * 1024);
        long first = Math.Max(0, FloorToLong(viewport.StartTick * scale / 256));
        long last = Math.Max(first, FloorToLong(Math.Max(viewport.StartTick, viewport.EndTick - 1) * scale / 256));
        Color normal = GetSolidColor(normalBrush, Colors.SteelBlue);
        Color selected = GetSolidColor(selectedBrush, Colors.IndianRed);
        Color border = GetSolidColor(borderBrush, Colors.Black);
        TimelineSelectionSnapshot? selection = SelectionSnapshot;
        context.PushClip(new RectangleGeometry(new Rect(header, ruler,
            Math.Max(0, ActualWidth - header), Math.Max(0, ActualHeight - ruler))));
        for (int lane = Math.Max(0, viewport.FirstLane); lane < Math.Min(4, viewport.LastLaneExclusive); lane++)
        for (long tile = first; tile <= last; tile++)
        {
            double left = tile * 256d - gutterX;
            (long start, long end) = TimelineConductorMetaRasterizer.GetReadTickRange(scale, tile, dpi.DpiScaleX);
            ulong fingerprint = snapshot.ConductorSource.GetRangeFingerprint(start, end, lane, lane + 1);
            fingerprint = TimelineContentFingerprint.Combine(fingerprint,
                selection?.GetRangeRevisionFingerprint(start, end, lane, lane + 1) ?? 0);
            var key = new TimelineRasterCacheKey(TimelineRasterLayer.ConductorMeta,
                snapshot.ProjectionKey, fingerprint, BitConverter.DoubleToInt64Bits(scale),
                BitConverter.DoubleToInt64Bits(height), tile, lane, ColorToArgb(normal),
                ColorToArgb(selected), ColorToArgb(border), dpiX, dpiY);
            long capturedTile = tile;
            int capturedLane = lane;
            Func<CancellationToken, TimelineRasterBuffer> build = TimelineRasterFactory.Create(
                (snapshot, selection, scale, height, capturedTile, capturedLane,
                    dpi.DpiScaleX, dpi.DpiScaleY, normal, selected, border, key),
                static (state, token) => TimelineConductorMetaRasterizer.Rasterize(
                    state.snapshot, state.selection, state.scale, state.height,
                    state.capturedTile, state.capturedLane, state.DpiScaleX, state.DpiScaleY,
                    state.normal, state.selected, state.border, state.key, token));
            double x = header + (left - viewport.StartTick * scale) / dpi.DpiScaleX;
            double y = ruler + (lane - viewport.FirstLane) * LaneHeight - gutterY / dpi.DpiScaleY;
            if (TimelineRasterCache.Shared.TryGet(key, out BitmapSource? bitmap) && bitmap is not null)
            {
                context.DrawImage(bitmap, new Rect(x, y,
                    bitmap.PixelWidth / dpi.DpiScaleX, bitmap.PixelHeight / dpi.DpiScaleY));
                if (TimelineConductorMetaRasterizer.TryGetLabels(key, out ConductorMetaLabel[]? labels))
                {
                    foreach (ConductorMetaLabel label in labels!)
                    {
                        FormattedText text = GetFormattedText(label.Text, textBrush, 10, FontWeights.Normal);
                        double labelX = header + (label.DeviceX - viewport.StartTick * scale) / dpi.DpiScaleX;
                        double labelY = ruler + (lane - viewport.FirstLane + .5) * LaneHeight + 7;
                        context.PushClip(new RectangleGeometry(new Rect(labelX, labelY, 68, Math.Max(1, LaneHeight * .5 - 7))));
                        context.DrawText(text, new Point(labelX, labelY));
                        context.Pop();
                    }
                }
                else RequestConductorLabels(key, build, snapshot);
            }
            else RequestRaster(key, build, TimelineRasterRequestPriority.Visible);
        }
        context.Pop();
    }

    private void RequestConductorLabels(
        TimelineRasterCacheKey key,
        Func<CancellationToken, TimelineRasterBuffer> build,
        TimelineRenderSnapshot snapshot)
    {
        if (_backgroundWorkSuspended || !_conductorLabelRequests.Add(key)) return;
        CancellationToken token = _rasterRequestCancellation.Token;
        _ = RunConductorWork(build, token).ContinueWith(task =>
        {
            _ = task.Exception;
            CancelablePresentationDispatch.Post(Dispatcher, token, () =>
            {
                _conductorLabelRequests.Remove(key);
                if (!_backgroundWorkSuspended && ReferenceEquals(Snapshot, snapshot)) QueueRasterInvalidation();
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private bool TryHitConductorPoint(Point point, TimelineViewport viewport, out TimelineRenderItem item)
    {
        item = default;
        if (Snapshot is not { ConductorSource: { } source } snapshot) return false;
        int lane = snapshot.IsTempoProjection ? 0 : YToLane(viewport, point.Y - GetRulerHeight());
        if (!snapshot.IsTempoProjection && Math.Abs(point.Y - GetRulerHeight()
            - (lane - viewport.FirstLane + .5) * LaneHeight) > 8)
        { _exactQueryPending = false; return false; }
        long tick = viewport.XToContainingTick(point.X - GetLaneHeaderWidth());
        long tolerance = Math.Max(1, CeilingToLong(8 / viewport.PixelsPerTick));
        double value = snapshot.IsTempoProjection ? ValueYToNormalized(point.Y, GetRulerHeight()) : 0;
        double valueTolerance = snapshot.IsTempoProjection
            ? Math.Abs(ValueYToNormalized(point.Y + 8, GetRulerHeight()) - value) : 1;
        var key = new ConductorHitKey(source, tick, tolerance, lane, value, valueTolerance);
        if (_conductorHitKey == key && _conductorHitReady)
        {
            _exactQueryPending = false;
            if (_conductorHitResult is { } found) { item = found; return true; }
            return false;
        }
        if (_pendingConductorPress is not null && _conductorHitKey != key)
        { _exactQueryPending = true; return false; }
        if (source.TryFindNearestCached(tick, tolerance, lane, value, valueTolerance, out TimelineRenderItem? cached))
        {
            _exactQueryPending = false;
            if (cached is { } found) { item = found; return true; }
            return false;
        }
        _exactQueryPending = true;
        if (_backgroundWorkSuspended || _conductorHitKey == key) return false;
        _conductorHitCancellation?.Cancel();
        _conductorHitCancellation?.Dispose();
        // A semantic hit belongs to its source/query key, not the pixel epoch.
        // First paint or a raster-only reset must not strand an otherwise valid
        // hit behind a canceled token and a still-current pending key.
        _conductorHitCancellation = new();
        CancellationTokenSource requestCancellation = _conductorHitCancellation;
        CancellationToken token = requestCancellation.Token;
        _conductorHitKey = key;
        _conductorHitReady = false;
        _ = RunConductorWork(ct => source.FindNearest(tick, tolerance, lane, value, valueTolerance, ct), token)
            .ContinueWith(task =>
            {
                _ = task.Exception;
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                CancelablePresentationDispatch.Post(Dispatcher, token, () =>
                {
                    if (_backgroundWorkSuspended || _conductorHitKey != key || !ReferenceEquals(Snapshot, snapshot)
                        || !ReferenceEquals(_conductorHitCancellation, requestCancellation)) return;
                    if (task.IsCanceled || task.IsFaulted)
                    {
                        CancelConductorHit();
                        _exactQueryPending = false;
                        Cursor = Cursors.Arrow;
                        return;
                    }
                    _conductorHitResult = task.Result;
                    _conductorHitReady = true;
                    _exactQueryPending = false;
                    ReplayConductorPress();
                    RefreshPointerPositionText();
                    // Repainting alone does not update Cursor. A hover-only query has
                    // no deferred press to restore it, even when no point was hit.
                    RefreshHoverIntent();
                });
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return false;
    }

    private readonly record struct ConductorHitKey(ConductorRenderItemSource Source,
        long Tick, long Tolerance, int Lane, double Value, double ValueTolerance);

    private void CancelConductorHit()
    {
        _conductorHitCancellation?.Cancel();
        _conductorHitCancellation?.Dispose();
        _conductorHitCancellation = null;
        _conductorHitKey = null;
        _conductorHitResult = null;
        _conductorHitReady = false;
        if (_pendingConductorPress is not null)
        {
            _pendingConductorPress = null;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
        CancelConductorRuler();
    }

    private void CancelConductorRuler()
    {
        _conductorRulerCancellation?.Cancel();
        _conductorRulerCancellation?.Dispose();
        _conductorRulerCancellation = null;
        _conductorRulerKey = null;
        _conductorRulerItems = [];
        _conductorRulerReady = false;
    }

    private static long CeilingToLong(double value) => value <= 0 ? 0
        : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);

    private bool TryPrepareConductorRuler(TimelineRenderSnapshot snapshot, TimelineViewport viewport)
    {
        var key = new ConductorRulerKey(snapshot, viewport.StartTick, viewport.EndTick, viewport.PixelsPerTick);
        if (_conductorRulerKey == key)
        {
            if (_conductorRulerReady) _rulerItems.AddRange(_conductorRulerItems);
            return _conductorRulerReady;
        }
        _conductorRulerCancellation?.Cancel();
        _conductorRulerCancellation?.Dispose();
        _conductorRulerCancellation = new();
        CancellationTokenSource requestCancellation = _conductorRulerCancellation;
        CancellationToken token = requestCancellation.Token;
        _conductorRulerKey = key;
        _conductorRulerReady = false;
        if (_backgroundWorkSuspended) return false;
        _ = RunConductorWork(ct =>
        {
            Dictionary<long, TimelineRenderItem> cells = [];
            snapshot.ConductorSource!.Visit(viewport.StartTick, viewport.EndTick, 0, 1, item =>
            {
                long cell = (long)Math.Floor((item.StartTick - (double)viewport.StartTick) * viewport.PixelsPerTick / 96);
                if (item.Kind == TimelineItemKind.ProjectEndMarker) cell = long.MaxValue;
                if (!cells.TryGetValue(cell, out TimelineRenderItem previous)
                    || item.StartTick < previous.StartTick || item.StartTick == previous.StartTick && item.Id.CompareTo(previous.Id) < 0)
                    cells[cell] = item;
            }, ct);
            ct.ThrowIfCancellationRequested();
            return cells.Values.OrderBy(x => x.StartTick).ThenBy(x => x.Id)
                .Select(x => x.Label.Length > 48 ? x with { Label = x.Label[..48] + "…" } : x).ToArray();
        }, token).ContinueWith(task =>
        {
            _ = task.Exception;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            CancelablePresentationDispatch.Post(Dispatcher, token, () =>
            {
                if (_backgroundWorkSuspended || _conductorRulerKey != key || !ReferenceEquals(RulerSnapshot, snapshot)
                    || !ReferenceEquals(_conductorRulerCancellation, requestCancellation)) return;
                if (task.IsCanceled || task.IsFaulted)
                {
                    _conductorRulerKey = null;
                    _conductorRulerReady = false;
                    return;
                }
                _conductorRulerItems = task.Result;
                _conductorRulerReady = true;
                InvalidateVisual();
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return false;
    }

    private readonly record struct ConductorRulerKey(TimelineRenderSnapshot Snapshot, long Start, long End, double Scale);

    private static Task<T> RunConductorWork<T>(Func<CancellationToken, T> work, CancellationToken token) => Task.Run(async () =>
    {
        await ConductorQuerySlots.WaitAsync(token).ConfigureAwait(false);
        try { token.ThrowIfCancellationRequested(); return work(token); }
        finally { ConductorQuerySlots.Release(); }
    }, token);
}
