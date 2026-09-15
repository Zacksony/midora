using System.Collections.Concurrent;
using System.Windows.Threading;
using Midora.Compiler;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class InstrumentAuditionQueueTests
{
    [Fact]
    public async Task FastSelectionsKeepOnePendingRequestAndCloseWaitsForItsOwnStop()
    {
        using var target = new Target();
        var audition = new InstrumentPresetAuditionSession(target, Dispatcher.CurrentDispatcher, _ => { });
        audition.Play(new(0, 0, 0), false);
        Assert.True(target.FirstEntered.Wait(TimeSpan.FromSeconds(5)));
        for (int n = 1; n <= 127; n++) audition.Play(new(0, 0, n), false);
        target.AllowFirst.Set();
        Assert.True(target.SecondEntered.Wait(TimeSpan.FromSeconds(5)));
        await audition.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 0, 127 }, target.Programs.ToArray());
        Assert.Equal(1, target.MaximumConcurrent); Assert.Equal(0, target.Active);
        audition.Play(new(0, 0, 60), true);
        await audition.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, target.Programs.Count); Assert.Equal(0, target.Active);
        Assert.Single(target.Owners.Distinct());
    }

    [Fact]
    public async Task StopBarrierCancelsQueuedStartAndWaitsThroughPreparing()
    {
        using var target = new Target();
        var audition = new InstrumentPresetAuditionSession(target, Dispatcher.CurrentDispatcher, _ => { });
        audition.Play(new(0, 0, 1), true);
        Assert.True(target.FirstEntered.Wait(TimeSpan.FromSeconds(5)));
        audition.Play(new(0, 0, 2), true);
        Task stopped = audition.StopAsync();
        audition.Play(new(0, 0, 3), true); // cannot replace a stop acknowledgement
        Assert.False(stopped.IsCompleted);
        target.AllowFirst.Set();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1 }, target.Programs.ToArray()); Assert.Equal(0, target.Active);
        await audition.CloseAsync();
    }

    [Fact]
    public async Task StopFailuresAreAcknowledgedAsErrorsAndCanBeRetried()
    {
        using var target = new Target { FailStops = true };
        var audition = new InstrumentPresetAuditionSession(target, Dispatcher.CurrentDispatcher, _ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => audition.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        target.FailStops = false;
        await audition.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, target.Active);
    }

    private sealed class Target : IInstrumentPresetAuditionTarget, IDisposable
    {
        public readonly ManualResetEventSlim FirstEntered = new(), AllowFirst = new(), SecondEntered = new();
        public readonly ConcurrentQueue<int> Programs = new();
        public readonly ConcurrentQueue<Guid> Owners = new();
        public volatile bool FailStops;
        public int Active, MaximumConcurrent;
        private int _concurrent;
        public void Enter() { int n = Interlocked.Increment(ref _concurrent); MaximumConcurrent = Math.Max(MaximumConcurrent, n); }
        public void Exit() => Interlocked.Decrement(ref _concurrent);
        public void Start(Guid owner, InstrumentPresetPreviewRequest request, bool held)
        {
            Owners.Enqueue(owner); Programs.Enqueue(request.Program);
            if (Programs.Count == 1)
            { FirstEntered.Set(); if (!AllowFirst.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); }
            Active = 1;
            if (Programs.Count == 2) SecondEntered.Set();
        }
        public void Stop(Guid owner)
        { Owners.Enqueue(owner); if (FailStops) throw new InvalidOperationException("Injected stop failure"); Active = 0; }
        public void Release(Guid owner) { Owners.Enqueue(owner); Active = 0; }
        public void Dispose() { AllowFirst.Set(); FirstEntered.Dispose(); AllowFirst.Dispose(); SecondEntered.Dispose(); }
    }
}
