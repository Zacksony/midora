using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Controls;
using Midora.Desktop.Presentation.Rendering;
using Midora.Desktop.Presentation.Typography;

namespace Midora.Desktop;

public partial class InstrumentChangeLane : UserControl
{
    public static readonly DependencyProperty PointerPositionTextProperty = DependencyProperty.Register(nameof(PointerPositionText), typeof(string), typeof(InstrumentChangeLane), new PropertyMetadata(""));
    public string PointerPositionText { get => (string)GetValue(PointerPositionTextProperty); private set => SetValue(PointerPositionTextProperty, value); }
    private WorkspaceViewModel? _workspace;
    private MidoraId? _owner;
    private InstrumentChangeProjection? _index;
    private InstrumentChangeProjection? _selectionIndex;
    private long _selectionRevision = -1;
    private ProjectionRequest? _latest;
    private ProjectionRequest? _pending;
    private ProjectionRequest? _indexed;
    private CancellationTokenSource? _reading;
    private bool _running;
    private int _lifetimeVersion;
    private long? _selectTick;
    private InstrumentChangeValue? _selectedValue;
    private sealed record ProjectionRequest(InstrumentChangeSet Root, long Revision, MidoraId Owner,
        Func<InstrumentChange, InstrumentChangeValue?> Read, long Start, long End, int Pixels,
        long? SelectTick, MidoraId? SelectedId, CompressedMidoraIdSet Selection, long SelectionRevision, long PreviewDelta);
    private readonly object _projectionGate = new();
    internal MidoraId? SelectedChangeId => _selectedValue?.Id;
    internal bool IsSelected(InstrumentChangeValue value) => _workspace?.Selection.IdSet is { } ids
        && ids.Contains(value.BankEventId) && ids.Contains(value.ProgramEventId)
        && (value.BankLsbEventId is not { } lsb || ids.Contains(lsb));
    internal long PreviewDelta => _dragging && _pressedHit is not null ? _delta : 0;
    internal bool CopyPreview => _dragging && _copy;
    internal MainWindow? CommandHost { get; set; }
    internal MainWindow? Host => CommandHost ?? Window.GetWindow(this) as MainWindow;
    internal TimelineEditorSettings? Settings => DataContext switch
    {
        TimelineWorkspaceViewModel timeline => timeline.LaneEditorSettings,
        InstrumentWorkspaceViewModel voice => voice.EventLaneEditorSettings,
        _ => null
    };
    public InstrumentChangeLane()
    {
        InitializeComponent(); Points.Lane = this; GestureOverlay.Lane = this;
        Points.MouseLeftButtonUp += OnPointClick;
        Points.MouseMove += OnPointMove;
        Points.MouseLeave += (_, _) => { _hoverPoint = null; GestureOverlay.InvalidateVisual(); if (!Points.IsMouseCaptured) PointerPositionText = ""; };
        Points.LostMouseCapture += (_, _) => { if (!_releasing) ResetGesture(); };
        Points.MouseRightButtonUp += OnContextClick;
        SizeChanged += (_, _) => { Backdrop.LaneHeight = Math.Max(20, Backdrop.ActualHeight - 24); Refresh(); };
        DataContextChanged += (_, _) => { Attach(); Refresh(); };
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    { Interlocked.Increment(ref _lifetimeVersion); ResetInteractionLifetime(); Attach(); Refresh(); }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Detach(); Points.Clear(); _pending = null; _latest = null; _reading?.Cancel(); _interaction.Cancel(); _hoverPoint = null; ResetGesture();
        _selectedValue = null; _selectTick = null;
        if (ContextMenu is { } menu) menu.IsOpen = false;
        ContextMenu = null;
        int version = Interlocked.Increment(ref _lifetimeVersion);
        _ = Task.Run(() => { lock (_projectionGate)
            if (version == Volatile.Read(ref _lifetimeVersion))
            { _index?.Dispose(); _index = null; _indexed = null; _selectionIndex?.Dispose(); _selectionIndex = null; _selectionRevision = -1; } });
    }
    private void Attach()
    {
        Detach();
        if (!IsLoaded || DataContext is not WorkspaceViewModel workspace) return;
        _workspace = workspace; workspace.PropertyChanged += OnWorkspaceChanged;
        string start = workspace is InstrumentWorkspaceViewModel ? "TimelineStartTick" : "StartTick";
        string span = workspace is InstrumentWorkspaceViewModel ? "TimelineTickSpan" : "TickSpan";
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.StartTickProperty, new Binding(start));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.TickSpanProperty, new Binding(span));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.EditCursorTickProperty, new Binding("EditCursorTick"));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.PlaybackCursorTickProperty, new Binding("PlaybackCursorTick"));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.GridVisibleProperty, new Binding("EditorSettings.GridVisible"));
        Backdrop.SetBinding(Presentation.Controls.TimelineSurface.TimeSignatureMapProperty, new Binding("EditorSettings.TimeSignatureMap"));
        if (workspace is TimelineWorkspaceViewModel)
            Backdrop.SetBinding(Presentation.Controls.TimelineSurface.ProjectTickOffsetProperty, new Binding("ProjectTickOffset"));
        Backdrop.Snapshot = new(0, "instrument-lane-background", [], laneLabels: ["Inst."]);
    }
    private void Detach()
    { if (_workspace is not null) _workspace.PropertyChanged -= OnWorkspaceChanged; _workspace = null; }
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Snapshot" or "SubVoiceSnapshot" or "SubVoiceEventSnapshot" or "ActiveSubVoiceId"
            or "StartTick" or "TickSpan" or "TimelineStartTick" or "TimelineTickSpan") Refresh();
        else if (_workspace?.Selection.Revision != _latest?.SelectionRevision) Refresh();
        Points.InvalidateVisual();
        GestureOverlay.InvalidateVisual();
    }
    internal void Refresh()
    {
        if (!IsLoaded) return;
        if (Host?.GetInstrumentLaneOwner(DataContext) is not { } context)
        { _pending = null; _reading?.Cancel(); Points.Clear(); _selectedValue = null; return; }
        InstrumentChangeSet root; long generation; Func<InstrumentChange, InstrumentChangeValue?> read;
        if (context.Midi is { } midi)
        {
            root = midi.InstrumentChanges; generation = midi.ChannelEvents.Generation;
            var snapshot = midi.ChannelEvents.CreateQuerySnapshot();
            read = group => InstrumentChangeResolver.TryRead(snapshot, group, out var value) ? value : null;
            ChangeOwner(midi.Id);
        }
        else if (context.Voice is { } voice)
        {
            root = voice.InstrumentChanges; generation = voice.Events.Generation;
            var snapshot = voice.Events.CreateQuerySnapshot();
            read = group => { InstrumentChangeSelectionQuery.PrepareSource(snapshot); return InstrumentChangeResolver.TryRead(snapshot, group, out var value) ? value : null; };
            ChangeOwner(voice.Id);
        }
        else return;

        var range = VisibleRange;
        if (_latest is { } prior && (prior.Owner != _owner || prior.Revision != generation || !ReferenceEquals(prior.Root, root)))
        { ResetInteractionLifetime(); ResetGesture(); }
        _latest = _pending = new(root, generation, _owner!.Value, read, range.Start, range.End,
            (int)Math.Clamp(Points.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX, 1, 16_384),
            _selectTick, SelectedChangeId, _workspace?.Selection.SharedIds ?? CompressedMidoraIdSet.Empty,
            _workspace?.Selection.Revision ?? 0, PreviewDelta);
        _reading?.Cancel();
        if (!_running) _ = ReadProjectionAsync();
    }

    private async Task ReadProjectionAsync()
    {
        _running = true;
        try
        {
            while (_pending is { } request && IsLoaded)
            {
                _pending = null;
                using var cancel = new CancellationTokenSource(); _reading = cancel;
                try
                {
                    var result = await Task.Run(() =>
                    {
                        lock (_projectionGate)
                        {
                        if (_index is null || _indexed is null || !ReferenceEquals(request.Root, _indexed.Root)
                            || request.Revision != _indexed.Revision || request.Owner != _indexed.Owner)
                        {
                            _index?.Dispose(); _index = null; _indexed = null;
                            _selectionIndex?.Dispose(); _selectionIndex = null; _selectionRevision = -1;
                            _index = InstrumentChangeProjection.Create(request.Root, request.Read, cancel.Token);
                            _indexed = request;
                        }
                        var visible = _index.ReadVisible(request.Start, request.End, request.Pixels, cancel.Token);
                        if (_selectionIndex is null || _selectionRevision != request.SelectionRevision)
                        {
                            _selectionIndex?.Dispose(); _selectionIndex = null;
                            _selectionIndex = request.Selection.Count >= request.Root.Count
                                ? _index.SelectMembers(request.Selection, cancel.Token)
                                : InstrumentChangeProjection.Create(InstrumentChangeSelectionQuery.EnumerateGroups(
                                    request.Root, request.Selection, cancel.Token), request.Read, cancel.Token);
                            _selectionRevision = request.SelectionRevision;
                        }
                        long selectedStart = (long)Math.Clamp((decimal)request.Start - request.PreviewDelta, 0, long.MaxValue);
                        long selectedEnd = (long)Math.Clamp((decimal)request.End - request.PreviewDelta, 0, long.MaxValue);
                        var selectedValues = _selectionIndex.ReadVisible(selectedStart, selectedEnd, request.Pixels, cancel.Token);
                        InstrumentChangeValue? selected = request.SelectTick is { } tick
                            ? _index.ReadAtTick(tick, cancel.Token)
                            : request.SelectedId is { } id && request.Root.TryGet(id, out var group) ? request.Read(group) : null;
                        return (visible, selected, selectedValues);
                        }
                    });
                    if (!cancel.IsCancellationRequested && IsLoaded)
                    {
                        Points.SetValues(result.visible, result.selectedValues);
                        if (request.SelectTick is not null && _selectTick == request.SelectTick)
                        { Select(result.selected); _selectTick = null; }

                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (!cancel.IsCancellationRequested && IsLoaded)
                    { Points.Clear(); Host?.ReportInstrumentLaneFailure(exception); }
                }
                finally { _reading = null; }
            }
        }
        finally
        {
            _running = false;
        }
    }
    private void ChangeOwner(MidoraId owner)
    {
        if (_owner == owner) return;
        _owner = owner; _selectedValue = null; _selectTick = null;
        ResetInteractionLifetime(); ResetGesture();
        Points.Clear();
    }
    private void Select(InstrumentChangeValue? value)
    {
        _selectedValue = value;
        if (_workspace is { } workspace) Host?.PublishInstrumentSelection(workspace,
            CompressedMidoraIdSet.Create(value is { } row ? MemberIds(row) : []));
        Points.InvalidateVisual();
    }
    internal double TickX(long tick) => 64 + (tick - Backdrop.StartTick) * ((Points.ActualWidth - 64) / Math.Max(1, (double)Backdrop.TickSpan));
    internal long PointTick(double x, bool snap = true)
    {
        double relative = (x - 64) * Backdrop.TickSpan / Math.Max(1, Points.ActualWidth - 64);
        long delta = relative >= long.MaxValue ? long.MaxValue : relative <= long.MinValue ? long.MinValue : (long)Math.Round(relative);
        long tick = delta > 0 && Backdrop.StartTick > long.MaxValue - delta ? long.MaxValue - 1
            : Math.Max(0, Backdrop.StartTick + delta);
        return Math.Min(long.MaxValue - 1, snap ? Settings?.SnapAbsolute(tick) ?? tick : tick);
    }
    internal (long Start, long End) VisibleRange
    {
        get
        {
            var (start, span) = DataContext switch
            {
                TimelineWorkspaceViewModel w => (w.StartTick, w.TickSpan),
                InstrumentWorkspaceViewModel w => (w.TimelineStartTick, w.TimelineTickSpan),
                _ => (Backdrop.StartTick, Backdrop.TickSpan)
            };
            return (start, start > long.MaxValue - span ? long.MaxValue : start + span);
        }
    }
    internal string Label(InstrumentChangeValue value) => Host?.InstrumentChangeLabel(value)
        ?? $"{value.BankMsb}.{value.BankLsb}.{value.Program}";
    internal void SelectAt(long tick)
    {
        _selectTick = tick;
        // The background index resolves off-screen creation too. No raw source
        // or cold page is synchronously scanned by the UI selection callback.
        Refresh(); Points.InvalidateVisual(); Focus();
    }
}

public sealed class InstrumentChangeLaneVisual : FrameworkElement
{
    internal InstrumentChangeLane? Lane;
    private InstrumentChangeValue[] _values = [];
    private InstrumentChangeValue[] _selected = [];
    internal void Clear() { _values = []; _selected = []; InvalidateVisual(); }
    internal void SetValues(InstrumentChangeValue[] values, InstrumentChangeValue[] selected)
    { _values = values; _selected = selected; InvalidateVisual(); }
    private int LowerBound(long tick)
    {
        int lo = 0, hi = _values.Length;
        while (lo < hi) { int mid = lo + (hi - lo) / 2; if (_values[mid].Tick < tick) lo = mid + 1; else hi = mid; }
        return lo;
    }
    internal InstrumentChangeValue? Find(MidoraId? id) => _values.FirstOrDefault(value => value.Id == id) is var value && value.Id != default ? value : null;
    internal InstrumentChangeValue? AtTick(long tick) => LowerBound(tick) is int index && index < _values.Length && _values[index].Tick == tick ? _values[index] : null;
    internal InstrumentChangeValue? Hit(Point point)
    {
        if (Lane is null || Math.Abs(point.Y - (24 + (ActualHeight - 24) * .5)) > 12) return null;
        int at = LowerBound(Lane.PointTick(point.X, snap: false));
        InstrumentChangeValue? result = null; double distance = 12;
        for (int index = Math.Max(0, at - 1); index < Math.Min(_values.Length, at + 1); index++)
        { double d = Math.Abs(Lane.TickX(_values[index].Tick) - point.X); if (d <= distance) { result = _values[index]; distance = d; } }
        return result;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Lane is null || ActualWidth <= 64 || ActualHeight <= 24) return;
        dc.PushClip(new RectangleGeometry(new Rect(64, 24, ActualWidth - 64, ActualHeight - 24)));
        var range = Lane.VisibleRange; int index = LowerBound(range.Start);
        double lastPixel = -1, labelEnd = -1, y = 24 + (ActualHeight - 24) * .5;
        int labels = 0; double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(TryFindResource("Font.UI") as FontFamily ?? EmbeddedFontFamilies.Ui,
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (; index < _values.Length && _values[index].Tick < range.End; index++)
        {
            var value = _values[index]; double x = Lane.TickX(value.Tick), pixel = Math.Floor(x * dpi);
            bool selected = Lane.IsSelected(value);
            if (selected && !Lane.CopyPreview) x = Lane.TickX((long)Math.Clamp((decimal)value.Tick + Lane.PreviewDelta, 0, long.MaxValue - 1));
            if (pixel != lastPixel || selected)
                dc.DrawEllipse(selected ? Brushes.IndianRed : Brushes.SlateGray, new Pen(Brushes.LightGray, 1), new(x, y), 4, 4);
            lastPixel = pixel;
            if (labels >= 128 || x < labelEnd) continue;
            var text = new FormattedText(Lane.Label(value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 11, Brushes.LightGray, dpi) { MaxTextWidth = 240, MaxTextHeight = 18, Trimming = TextTrimming.CharacterEllipsis };
            double width = Math.Min(248, text.Width + 8);
            dc.DrawRoundedRectangle(TryFindResource("Brush.Surface.1") as Brush ?? Brushes.Black,
                new Pen(TryFindResource("Brush.Border.Strong") as Brush ?? Brushes.DimGray, 1), new(x, y + 10, width, 24), 3, 3);
            dc.DrawText(text, new(x + 4, y + 13)); labelEnd = x + width + 8; labels++;
        }
        // Selection has its own bounded pixel projection. A selected member
        // need not be the normal layer's representative at a dense pixel.
        foreach (var value in _selected)
        {
            if (!Lane.IsSelected(value)) continue;
            long tick = (long)Math.Clamp((decimal)value.Tick + Lane.PreviewDelta, 0, long.MaxValue - 1);
            dc.DrawEllipse(Lane.PreviewDelta != 0 ? Brushes.DodgerBlue : Brushes.IndianRed,
                new Pen(Brushes.LightGray, 1), new(Lane.TickX(tick), y), 4, 4);
        }
        dc.Pop();
    }
}

/// <summary>Pointer-only feedback must not rebuild labels or query source pages.</summary>
public sealed class InstrumentChangeGestureVisual : FrameworkElement
{
    internal InstrumentChangeLane? Lane;
    protected override void OnRender(DrawingContext dc)
    {
        if (Lane is null || ActualWidth <= 64 || ActualHeight <= 24) return;
        dc.PushClip(new RectangleGeometry(new Rect(64, 24, ActualWidth - 64, ActualHeight - 24)));
        var typeface = new Typeface(TryFindResource("Font.UI") as FontFamily ?? EmbeddedFontFamilies.Ui,
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Lane.DrawGesture(dc, typeface, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.Pop();
    }
}
