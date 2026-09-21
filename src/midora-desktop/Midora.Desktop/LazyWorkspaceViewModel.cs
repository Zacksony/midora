using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Desktop;

/// <summary>
/// A persisted tab placeholder.  It is intentionally not a second editor
/// implementation: it only keeps the value-only navigation record until the
/// user activates the tab, at which point the session replaces it with the
/// real workspace VM.
/// </summary>
internal sealed class LazyWorkspaceViewModel : WorkspaceViewModel
{
    internal LazyWorkspaceViewModel(
        WorkspaceKey key,
        string header,
        ProjectPresentationNavigationViewV4? navigationView)
        : base(key, header)
    {
        NavigationView = navigationView;
    }

    internal ProjectPresentationNavigationViewV4? NavigationView { get; }

    public override void Rebuild(MidoraProject project, long revision)
    {
        // Deliberately empty.  The session must materialize this placeholder
        // before it can become active or receive project-dependent state.
    }
}
