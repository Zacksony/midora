using System.Windows.Threading;
using Midora.Desktop.Presentation.Rendering;

namespace Midora.Desktop;

/// <summary>
/// A presentation completion owns its captured graph only until cancellation or
/// dispatch. A canceled operation can remain in Dispatcher internals without
/// retaining its former Workspace, Project, or result snapshots.
/// </summary>
internal static class WorkspacePresentationDispatch
{
    internal static void Post(Dispatcher dispatcher, CancellationToken token, Action callback) =>
        CancelablePresentationDispatch.Post(dispatcher, token, callback);
}
