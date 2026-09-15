using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Application.Tests;

[Collection("MemoryStage5HistoryOwnership")]
public sealed class BoundedWeakLifetimeFinalizationTests
{
    [Fact]
    public void ProjectCloseCannotSynchronouslyReleaseASourceAlreadyQueuedForFinalization()
    {
        string path = Path.Combine(AppContext.BaseDirectory, ".tmp", "weak-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        using MidoraProject project = new(480);
        using ManualResetEventSlim entered = new(), release = new();
        try
        {
            BoundedEditResources resources = new(new PagedEditResourceBudget(maximumResidentBytes: 0), path);
            WeakReference source = CreateSource(project, resources, entered, release);
            string activeLock = Assert.Single(Directory.GetFiles(path, "midora-temp.active.lock", SearchOption.AllDirectories));
            GC.Collect();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(source.IsAlive);
            // A short WeakReference clears before the finalizer runs. Project
            // closure cannot acquire that source to dispose its owned file.
            project.Dispose();
            Assert.Throws<IOException>(() =>
            {
                using FileStream opened = new(activeLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
            release.Set(); GC.WaitForPendingFinalizers();
            Assert.Equal(0, resources.SpillBytes);
            Assert.False(File.Exists(activeLock));
        }
        finally
        {
            release.Set(); GC.WaitForPendingFinalizers();
            project.Dispose();
            Directory.Delete(path, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateSource(MidoraProject project, BoundedEditResources resources,
        ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        using var scope = BulkEditPreparationContext.Enter(resources: resources, project: project);
        BoundedEditRecordStore<int> store = new(resources);
        for (int i = 0; i < 5000; i++) store.Add(i);
        store.Seal();
        return new WeakReference(new SuspendedFinalizerSource(store, entered, release));
    }

    private sealed class SuspendedFinalizerSource(BoundedEditRecordStore<int> store,
        ManualResetEventSlim entered, ManualResetEventSlim release) : BoundedImmutableValueSource<int>(store)
    {
        ~SuspendedFinalizerSource()
        {
            // Test-only, bounded coordination; base finalization then performs
            // the real source cleanup. Never add this wait to product code.
            try { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); }
            catch (ObjectDisposedException)
            {
                // An earlier assertion/setup failure may have already disposed
                // the test gates. Do not turn that failure into a process crash.
            }
        }
    }
}
