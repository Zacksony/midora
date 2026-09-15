using System.Windows.Threading;
using Midora.Compiler;

namespace Midora.Desktop;

/// <summary>One serial producer, one replaceable pending request, one explicit preview owner.</summary>
internal interface IInstrumentPresetAuditionTarget
{
    void Enter();
    void Exit();
    void Start(Guid owner, InstrumentPresetPreviewRequest request, bool held);
    void Stop(Guid owner);
    void Release(Guid owner);
}

internal sealed class InstrumentPresetAuditionSession(IInstrumentPresetAuditionTarget target, Dispatcher dispatcher,
    Action<string?> status)
{
    public InstrumentPresetAuditionSession(DesktopSessionController session, Dispatcher dispatcher, Action<string?> status)
        : this(new DesktopTarget(session), dispatcher, status) { }
    private sealed class DesktopTarget(DesktopSessionController session) : IInstrumentPresetAuditionTarget
    {
        public void Enter() => session.BeginPresetPreviewTransition();
        public void Exit() => session.EndPresetPreviewTransition();
        public void Start(Guid owner, InstrumentPresetPreviewRequest request, bool held) => session.StartInstrumentPresetPreview(owner, request, held);
        public void Stop(Guid owner) => session.StopInstrumentPresetPreview(owner);
        public void Release(Guid owner) => session.ReleaseInstrumentPresetPreviewKey(owner);
    }
    private readonly object _sync = new();
    private readonly Guid _owner = Guid.NewGuid();
    private Request? _pending;
    private Task _worker = Task.CompletedTask;
    private long _revision;
    private bool _closing;
    private sealed record Request(long Revision, InstrumentPresetPreviewRequest? Value, bool Held, bool Release,
        TaskCompletionSource? Completion = null);

    public void Play(InstrumentPresetPreviewRequest request, bool held) => Queue(request, held, false);
    public void Release() => Queue(null, false, true);
    public void Stop() => Queue(null, false, false);
    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_pending?.Completion is { } existing) return existing.Task;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = new(++_revision, null, false, false, completion);
            if (_worker.IsCompleted) _worker = Task.Run(Drain);
            return completion.Task;
        }
    }

    private void Queue(InstrumentPresetPreviewRequest? value, bool held, bool release)
    {
        lock (_sync)
        {
            if (_closing || _pending?.Completion is not null) return;
            _pending = new(++_revision, value, held, release);
            if (_worker.IsCompleted) _worker = Task.Run(Drain);
        }
    }

    public Task CloseAsync()
    {
        lock (_sync)
        {
            _closing = true;
            if (_pending?.Completion is { } existing) return existing.Task;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = new(++_revision, null, false, false, completion);
            if (_worker.IsCompleted) _worker = Task.Run(Drain);
            return completion.Task;
        }
    }

    private void Drain()
    {
        while (true)
        {
            Request request;
            lock (_sync)
            {
                if (_pending is null) { _worker = Task.CompletedTask; return; }
                request = _pending; _pending = null;
            }
            string? failure = null;
            Exception? stopFailure = null;
            target.Enter();
            try
            {
                if (request.Release) target.Release(_owner);
                else
                {
                    target.Stop(_owner);
                    bool current;
                    lock (_sync) current = request.Revision == _revision && !_closing;
                    if (current && request.Value is { } value)
                        target.Start(_owner, value, request.Held);
                }
            }
            catch (Exception exception)
            {
                failure = "Preview unavailable: " + exception.Message;
                if (request.Value is null && !request.Release) stopFailure = exception;
                try { target.Stop(_owner); } catch { }
            }
            finally { target.Exit(); }
            if (stopFailure is null) request.Completion?.TrySetResult();
            else request.Completion?.TrySetException(stopFailure);
            string? message = failure;
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) continue;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                lock (_sync) if (_closing || request.Revision != _revision) return;
                status(message);
            }));
        }
    }
}
