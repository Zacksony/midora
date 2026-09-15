using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop;

/// <summary>A navigation hint, not an owner of an editor or its source graph.</summary>
internal sealed class TimelineCommandTarget
{
    private WeakReference<WorkspaceViewModel>? _workspace;
    private WeakReference<TimelineSurface>? _surface;

    public void Set(TimelineSurface? surface)
    {
        if (surface?.DataContext is not WorkspaceViewModel { IsDisposed: false, IsPresentationSuspended: false } workspace)
        {
            Clear();
            return;
        }
        _workspace = new(workspace);
        _surface = new(surface);
    }

    public void Clear()
    {
        _workspace = null;
        _surface = null;
    }

    public TimelineSurface? Resolve(WorkspaceViewModel? activeWorkspace)
    {
        if (activeWorkspace is not { IsDisposed: false, IsPresentationSuspended: false }
            || _workspace?.TryGetTarget(out WorkspaceViewModel? workspace) != true
            || !ReferenceEquals(workspace, activeWorkspace)
            || _surface?.TryGetTarget(out TimelineSurface? surface) != true
            || surface is null
            || !ReferenceEquals(surface.DataContext, workspace)) return null;
        return surface;
    }

    public bool RestoreFocus(WorkspaceViewModel? activeWorkspace)
    {
        TimelineSurface? surface = Resolve(activeWorkspace);
        return surface is { IsVisible: true, IsEnabled: true, Focusable: true } && surface.Focus();
    }
}
