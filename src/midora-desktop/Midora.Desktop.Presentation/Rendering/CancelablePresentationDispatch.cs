using System.Windows.Threading;

namespace Midora.Desktop.Presentation.Rendering;

/// <summary>
/// Delivers a presentation completion without retaining its captured graph after
/// cancellation, including while its Dispatcher is not pumping messages.
/// </summary>
public static class CancelablePresentationDispatch
{
    public static void Post(
        Dispatcher dispatcher,
        CancellationToken token,
        Action callback,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(callback);
        if (token.IsCancellationRequested || dispatcher.HasShutdownStarted) return;
        Pending pending = new(token, callback);
        pending.Register(token.UnsafeRegister(static state => ((Pending)state!).Cancel(), pending));
        pending.Queue(dispatcher, priority);
    }

    private sealed class Pending(CancellationToken token, Action callback)
    {
        private readonly object _sync = new();
        private Action? _callback = callback;
        private DispatcherOperation? _operation;
        private CancellationTokenRegistration _registration;

        internal void Register(CancellationTokenRegistration registration)
        {
            lock (_sync)
            {
                if (_callback is not null)
                {
                    _registration = registration;
                    return;
                }
            }
            _ = registration.Unregister();
        }

        internal void Queue(Dispatcher dispatcher, DispatcherPriority priority)
        {
            lock (_sync)
                if (_callback is null) return;
            if (dispatcher.HasShutdownStarted)
            {
                Cancel();
                return;
            }
            try
            {
                DispatcherOperation operation = dispatcher.BeginInvoke(priority, new Action(Run));
                operation.Aborted += (_, _) => Cancel();
                bool revoked;
                lock (_sync)
                {
                    revoked = _callback is null;
                    if (!revoked) _operation = operation;
                }
                if (revoked) operation.Abort();
                if (operation.Status == DispatcherOperationStatus.Aborted) Cancel();
            }
            catch (InvalidOperationException)
            {
                Cancel();
            }
        }

        internal void Cancel()
        {
            DispatcherOperation? operation;
            CancellationTokenRegistration registration;
            lock (_sync)
            {
                _callback = null;
                operation = _operation;
                _operation = null;
                registration = _registration;
                _registration = default;
            }
            // Never wait for a cancellation callback or enter Dispatcher code
            // under the delivery lock. Abort is also safe after Run started.
            _ = registration.Unregister();
            operation?.Abort();
        }

        private void Run()
        {
            Action? action;
            CancellationTokenRegistration registration;
            lock (_sync)
            {
                action = _callback;
                _callback = null;
                _operation = null;
                registration = _registration;
                _registration = default;
            }
            _ = registration.Unregister();
            if (!token.IsCancellationRequested) action?.Invoke();
        }
    }
}
