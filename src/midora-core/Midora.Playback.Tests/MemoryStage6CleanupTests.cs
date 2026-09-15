using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Compiler;
using Midora.Domain;

namespace Midora.Playback.Tests;

public sealed class MemoryStage6CleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedWorkerStillReleasesOwnedStorageAndMirror(bool retainExternalReader)
    {
        using MidoraProject project = PreparationStorageCacheTests.CreateProject();
        using ProjectCompilationSession session = new(project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        (WeakReference canonicalResult, WeakReference compilationMirror) = CaptureOwnedResults(session);
        IDisposable? reader = retainExternalReader
            ? RetainCurrentCanonical(session) : null;
        InvalidOperationException injected = new("Injected compiler notification failure.");
        session.CompilationChanged += (_, _) => throw injected;
        Task worker = ReadPrivate<Task>(session, "_compileWorker");
        // This internal transaction does not emit the caller-thread notification;
        // only the real worker's Compiling notification triggers the fault.
        QueueEdit(session);
        Exception workerFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(injected, workerFailure);

        Assert.Same(injected, Assert.Throws<InvalidOperationException>(session.Dispose));
        session.Dispose();

        AssertDisposed(session, project);
        Assert.Equal(retainExternalReader ? 1 : 0, session.PreparationStorage.ActiveLeases);
        Assert.Equal(0, session.PreparationStorage.BaselineBytes);
        if (retainExternalReader)
        {
            Assert.True(session.PreparationStorage.ActiveBytes > 0);
            VerifyReadable(canonicalResult);
        }
        reader?.Dispose();
        if (reader is not null)
        {
            Assert.Null(ReadPrivate<object?>(reader, "_owner"));
            Assert.Null(ReadPrivate<object?>(reader, "_description"));
        }
        Assert.Equal(0, session.PreparationStorage.TotalRetainedBytes);
        AssertCollected(canonicalResult, compilationMirror);
        // Keeping the disposed lease alive must not retain its former storage.
        GC.KeepAlive(reader);
        // Ending the editing-time ownership must also have happened after fault.
        using ProjectEditingTimeSession reopenedTime = new(project);
        GC.KeepAlive(session);
    }

    [Fact]
    public async Task FailingCancellationRegistrationDoesNotSkipWorkerOrResourceCleanup()
    {
        using MidoraProject project = PreparationStorageCacheTests.CreateProject();
        using ProjectCompilationSession session = new(project,
            executionMode: ProjectCompilationExecutionMode.Background,
            backgroundDebounce: TimeSpan.Zero);
        InvalidOperationException injected = new("Injected cancellation registration failure.");
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CompilationStartingForTests = token =>
        {
            token.Register(() => throw injected);
            entered.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        };
        Task worker = ReadPrivate<Task>(session, "_compileWorker");
        session.ApplyReversibleEdit(_ => { }, _ => { }, ProjectChangeSet.Everything);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        AggregateException failure = Assert.Throws<AggregateException>(session.Dispose);

        Assert.Contains(injected, failure.Flatten().InnerExceptions);
        Assert.True(worker.IsCompleted);
        AssertDisposed(session, project);
        Assert.Equal(0, session.PreparationStorage.TotalRetainedBytes);
        using ProjectEditingTimeSession reopenedTime = new(project);
        session.Dispose();
    }

    private static void AssertDisposed(ProjectCompilationSession session, MidoraProject project)
    {
        Assert.Throws<ObjectDisposedException>(() => session.LastAttempt);
        Assert.Null(session.LastSuccessfulResult);
        Assert.Same(project, ReadPrivate<MidoraProject>(session, "_compilationProject"));
        Assert.Null(ReadPrivate<object?>(session, "_compileWorker"));
        Assert.Throws<ObjectDisposedException>(() => ReadPrivate<SemaphoreSlim>(session, "_compileSignal").Release());
        Assert.Throws<ObjectDisposedException>(() => ReadPrivate<CancellationTokenSource>(session, "_disposeCancellation").Cancel());
        Assert.Throws<ObjectDisposedException>(() => ReadPrivate<MidoraCompiler>(session, "_compiler").CompileFull(project));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Result, WeakReference Mirror) CaptureOwnedResults(ProjectCompilationSession session) =>
        (new(session.LastAttempt), new(ReadPrivate<MidoraProject>(session, "_compilationProject")));

    // Do not let the LastAttempt argument become a strong temporary in the async test frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IDisposable RetainCurrentCanonical(ProjectCompilationSession session) =>
        session.RetainPreparationStorage(session.LastAttempt);

    // ApplyReversibleEdit returns the old canonical result while queuing background work.
    // Discard it outside the async test frame: Debug JIT may otherwise keep that unused
    // return temporary alive across the following await and invalidate this ownership test.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void QueueEdit(ProjectCompilationSession session) =>
        session.ApplyReversibleEdit(_ => { }, _ => { }, ProjectChangeSet.Everything);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyReadable(WeakReference result)
    {
        CanonicalCompiledResult value = Assert.IsType<CanonicalCompiledResult>(result.Target);
        Assert.True(value.IsConsumable);
        Assert.False(value.Events.IsEmpty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(WeakReference canonicalResult, WeakReference compilationMirror)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(canonicalResult.IsAlive, "The canonical result remained reachable after session and external lease disposal.");
        Assert.False(compilationMirror.IsAlive, "The compilation mirror remained reachable after session disposal.");
    }

    private static T ReadPrivate<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
