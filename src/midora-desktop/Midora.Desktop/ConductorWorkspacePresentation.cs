using System.Windows;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;

namespace Midora.Desktop;

public sealed partial class TimelineWorkspaceViewModel
{
    private readonly object _tempoAxisSync = new();
    private ConductorTrack? _conductor;
    private ConductorTimelineProjection? _conductorProjection;
    private ConductorEventListSource? _conductorListSource;
    private GridLength _conductorListWidth = new(420);
    private double _tempoAxisMinimum;
    private double _tempoAxisMaximum = 240;
    private long _conductorRevision;
    private long _tempoAxisRequest;
    private CancellationTokenSource? _tempoFitCancellation;
    private int _conductorListFirstRow;
    private GridLength _conductorTempoHeight = new(3, GridUnitType.Star);
    private GridLength _conductorMetaHeight = new(2, GridUnitType.Star);

    public ConductorEventListSource? ConductorListSource => _conductorListSource;
    public TimelineRenderSnapshot? ConductorTempoSnapshot => _conductorProjection?.TempoSnapshot;
    public double TempoAxisMinimum => _tempoAxisMinimum;
    public double TempoAxisMaximum => _tempoAxisMaximum;
    public int ConductorListFirstRow { get => _conductorListFirstRow; set => Set(ref _conductorListFirstRow, Math.Max(0, value)); }
    public GridLength ConductorTempoHeight
    {
        get => _conductorTempoHeight;
        set { if ((value.IsStar || value.IsAbsolute) && double.IsFinite(value.Value) && value.Value > 0) Set(ref _conductorTempoHeight, value); }
    }
    public GridLength ConductorMetaHeight
    {
        get => _conductorMetaHeight;
        set { if ((value.IsStar || value.IsAbsolute) && double.IsFinite(value.Value) && value.Value > 0) Set(ref _conductorMetaHeight, value); }
    }
    public GridLength ConductorListWidth
    {
        get => _conductorListWidth;
        set { if (value.IsAbsolute && double.IsFinite(value.Value)) Set(ref _conductorListWidth, new(Math.Clamp(value.Value, 260, 720))); }
    }

    public override void CancelBackgroundPresentationWork()
    {
        base.CancelBackgroundPresentationWork();
        if (!_midiTargetDiscoveryCancellation.IsCancellationRequested)
            _midiTargetDiscoveryCancellation.Cancel();
        _midiTargetDiscoveryGeneration++;
        lock (_tempoAxisSync)
        {
            _tempoFitCancellation?.Cancel();
            _tempoAxisRequest++;
        }
    }

    private void SuspendConductorPresentation()
    {
        _conductorListSource?.Dispose();
        _conductorListSource = null;
        Raise(nameof(ConductorListSource));
    }

    private void ResumeConductorPresentation()
    {
        if (IsConductor && _conductor is not null && _conductorListSource is null)
        {
            _conductorListSource = new(_conductor);
            Raise(nameof(ConductorListSource));
        }
        _midiTargetDiscoveryCancellation.Dispose();
        _midiTargetDiscoveryCancellation = new();
    }

    private void DisposeConductorPresentation()
    {
        SuspendConductorPresentation();
        _conductor = null;
        _conductorProjection = null;
        SelectedConductorEvent = null;
        Raise(nameof(ConductorTempoSnapshot));
    }

    private void RebuildConductorPaged(MidoraProject project, long revision)
    {
        lock (_tempoAxisSync)
        {
            _tempoFitCancellation?.Cancel();
            _tempoAxisRequest++;
            _conductor = project.Conductor;
            _conductorRevision = revision;
            _conductorListSource?.Dispose();
            _conductorListSource = IsPresentationSuspended ? null : new(project.Conductor);
            Raise(nameof(ConductorListSource));
            RulerSnapshot = null;
            RangeStartTick = null;
            RangeEndTick = null;
            RebuildConductorProjection();
            Context = $"{project.Conductor.Tempos.Count + project.Conductor.TimeSignatures.Count
                + project.Conductor.KeySignatures.Count + project.Conductor.Markers.Count
                + (project.Conductor.EndMarker is null ? 0 : 1):N0} events";
        }
    }

    private void RebuildConductorProjection()
    {
        if (_conductor is null) return;
        _conductorProjection = new(_conductor, _conductorRevision, _tempoAxisMinimum, _tempoAxisMaximum);
        Snapshot = _conductorProjection.MetaSnapshot;
        Raise(nameof(ConductorTempoSnapshot));
        RefreshConductorPrimary();
    }

    internal void SetTempoAxis(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum < 0 || maximum <= minimum
            || maximum >= (double)decimal.MaxValue || (decimal)maximum <= (decimal)minimum)
            throw new ArgumentException("The BPM display range must be finite, non-negative and increasing.");
        lock (_tempoAxisSync)
        {
            _tempoAxisMinimum = minimum;
            _tempoAxisMaximum = maximum;
            _tempoFitCancellation?.Cancel();
            _tempoAxisRequest++;
            Raise(nameof(TempoAxisMinimum));
            Raise(nameof(TempoAxisMaximum));
            RebuildConductorProjection();
        }
    }

    internal async Task FitVisibleTempoAsync(CancellationToken cancellationToken)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = preparation.Token;
        ConductorQuerySnapshot<TempoChange> source;
        long start, end, revision, request, span;
        lock (_tempoAxisSync)
        {
            if (IsDisposed || IsPresentationSuspended || _conductor is null) return;
            source = _conductor.Tempos.CaptureQuerySnapshot();
            start = StartTick;
            span = TickSpan;
            end = start <= long.MaxValue - span ? start + span : long.MaxValue;
            revision = _conductorRevision;
            _tempoFitCancellation?.Cancel();
            _tempoFitCancellation = preparation;
            request = ++_tempoAxisRequest;
        }
        try
        {
            var range = await Task.Run(() =>
            {
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                // Binary predecessor and ordered ordinal walk; no prefix scan.
                int low = 0, high = source.Count;
                while (low < high)
                {
                    int mid = low + (high - low) / 2;
                    if (source.GetByOrdinal(mid).Tick < start) low = mid + 1; else high = mid;
                }
                for (int i = Math.Max(0, low - 1); i < source.Count; i++)
                {
                    if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var value = source.GetByOrdinal(i);
                    if (value.Tick >= end) break;
                    double bpm = (double)value.BeatsPerMinute;
                    min = Math.Min(min, bpm); max = Math.Max(max, bpm);
                }
                return (min, max);
            }, cancellationToken);
            // Request validation and publication must be indivisible: a foreground axis
            // change must not slip between the generation check and the old Fit result.
            lock (_tempoAxisSync)
            {
                if (IsDisposed || IsPresentationSuspended || preparation.IsCancellationRequested || revision != _conductorRevision || request != _tempoAxisRequest
                    || start != StartTick || span != TickSpan || !double.IsFinite(range.min)) return;
                double padding = Math.Max(1, (range.max - range.min) * .1);
                SetTempoAxis(Math.Max(0, range.min - padding), range.max + padding);
            }
        }
        catch (OperationCanceledException) when (preparation.IsCancellationRequested) { }
        finally
        {
            lock (_tempoAxisSync)
            {
                if (ReferenceEquals(_tempoFitCancellation, preparation)) _tempoFitCancellation = null;
            }
        }
    }

    private void RefreshConductorPrimary()
    {
        SelectedConductorEvent = null;
        if (Selection.Primary is not MidoraId id) return;
        TimelineRenderItem item = default;
        bool tempo = ConductorTempoSnapshot?.TryGetItemCached(id, out item, out _) == true;
        if (!tempo && Snapshot?.TryGetItemCached(id, out item, out _) != true) return;
        string type = tempo ? "Tempo" : item.Lane switch
        { 0 => "Time Signature", 1 => "Key Signature", 2 => "Marker", _ => "Project End" };
        SelectedConductorEvent = new(id, item.StartTick, type, ConductorRenderItemSource.GetDisplayLabel(item));
    }

    internal IEnumerable<MidoraId> EnumerateConductorIds() =>
        (ConductorTempoSnapshot?.EnumerateAllItems() ?? []).Concat(Snapshot?.EnumerateAllItems() ?? []).Select(x => x.Id);
}
