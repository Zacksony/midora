using System.Windows;

namespace Midora.Desktop;

/// <summary>Workspace-only layout and lazy source lifetime. Never Project presentation.</summary>
public sealed class TimelineObjectListState : ObservableObject
{
    private bool _isVisible;
    private bool _isActive;
    private double _width = 400;
    private int _firstRow;
    private Func<TimelineObjectListSource?>? _factory;
    private TimelineObjectListSource? _source;
    private string _errorText = "";

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (!Set(ref _isVisible, value)) return;
            Raise(nameof(ColumnWidth));
            if (!value) SetActive(false);
        }
    }

    public GridLength ColumnWidth
    {
        get => new(IsVisible ? _width : 0);
        set
        {
            if (!IsVisible || !value.IsAbsolute || !double.IsFinite(value.Value)) return;
            _width = Math.Clamp(value.Value, 240, 700);
            Raise(nameof(ColumnWidth));
        }
    }

    public int FirstRow { get => _firstRow; set => Set(ref _firstRow, Math.Max(0, value)); }
    public TimelineObjectListSource? Source { get => _source; private set => Set(ref _source, value); }
    public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }

    internal void SetFactory(Func<TimelineObjectListSource?>? factory)
    {
        _factory = factory;
        if (_isActive) RefreshSource();
    }

    internal void SetActive(bool active)
    {
        _isActive = active && IsVisible;
        if (_isActive) RefreshSource();
        else ReleaseSource();
    }

    private void RefreshSource()
    {
        TimelineObjectListSource? next;
        try { next = _factory?.Invoke(); ErrorText = ""; }
        catch (Exception ex) { ReleaseSource(); ErrorText = ex.Message; return; }
        if (Source is not null && next is not null && Source.IsSameContent(next))
        {
            (next as IDisposable)?.Dispose();
            return;
        }
        ReleaseSource();
        Source = next;
    }

    private void ReleaseSource()
    {
        TimelineObjectListSource? previous = Source;
        Source = null;
        (previous as IDisposable)?.Dispose();
    }
}
