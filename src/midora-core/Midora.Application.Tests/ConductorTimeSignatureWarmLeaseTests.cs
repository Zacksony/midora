using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Application.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConductorTimeSignatureWarmLeaseCollection
{
    public const string Name = "Conductor time-signature warm handoff";
}

[Collection(ConductorTimeSignatureWarmLeaseCollection.Name)]
public sealed class ConductorTimeSignatureWarmLeaseTests
{
    [Fact]
    public void NewUnconsumedRevisionReleasesOldMapEvenWhenBothHistoriesRemainAlive()
    {
        using MidoraProject project = CreateProject();
        var first = project.Conductor.TimeSignatures.CaptureQuerySnapshot();
        var clone = project.Conductor.CloneFrozen();
        clone.TimeSignatures[1000] = clone.TimeSignatures[1000] with { Numerator = 3 };
        var second = clone.TimeSignatures.CaptureQuerySnapshot();
        var old = Prepare(first);
        var current = Prepare(second);
        using var oldLease = old.Lease;
        using var currentLease = current.Lease;
        ForceCollection();
        Assert.False(IsAlive(old.Map));
        Assert.True(IsAlive(current.Map));
        oldLease.Dispose();
        ForceCollection();
        Assert.True(IsAlive(current.Map));
        GC.KeepAlive(first);
        GC.KeepAlive(second);
        GC.KeepAlive(oldLease);
        GC.KeepAlive(currentLease);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumingWarmMapClearsHandoffWithoutHistoryOwningIt(bool cacheOnly)
    {
        using MidoraProject project = CreateProject();
        var snapshot = project.Conductor.TimeSignatures.CaptureQuerySnapshot();
        var warm = Prepare(snapshot);
        using var lease = warm.Lease;
        ForceCollection();
        Assert.True(IsAlive(warm.Map));
        Consume(project, warm.Map, cacheOnly);
        ForceCollection();
        Assert.False(IsAlive(warm.Map));
        GC.KeepAlive(snapshot);
        GC.KeepAlive(lease);
    }

    [Fact]
    public void DisposingUnpublishedLeaseReleasesPreparedMap()
    {
        using MidoraProject project = CreateProject();
        var snapshot = project.Conductor.TimeSignatures.CaptureQuerySnapshot();
        var warm = Prepare(snapshot);
        warm.Lease.Dispose();
        warm.Lease.Dispose();
        ForceCollection();
        Assert.False(IsAlive(warm.Map));
        GC.KeepAlive(snapshot);
        GC.KeepAlive(warm.Lease);
    }

    [Fact]
    public void CancelledPreparationDoesNotReplaceExistingHandoff()
    {
        using MidoraProject project = CreateProject();
        var snapshot = project.Conductor.TimeSignatures.CaptureQuerySnapshot();
        var warm = Prepare(snapshot);
        using var lease = warm.Lease;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ProjectTimeSignatureMap.AcquireWarmLease(960, snapshot, cancellation.Token));
        ForceCollection();
        Assert.True(IsAlive(warm.Map));
        GC.KeepAlive(snapshot);
        GC.KeepAlive(lease);
    }

    private static MidoraProject CreateProject()
    {
        MidoraProject project = new(480);
        project.Conductor.TimeSignatures.AddRange(Enumerable.Range(1, 1999)
            .Select(index => new TimeSignatureChange(project, index * 1920L, 4, 4)));
        return project;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ProjectTimeSignatureMap.WarmLease Lease, WeakReference<ProjectTimeSignatureMap> Map)
        Prepare(ConductorQuerySnapshot<TimeSignatureChange> snapshot)
    {
        ProjectTimeSignatureMap map = ProjectTimeSignatureMap.GetOrCreate(480, snapshot);
        var lease = ProjectTimeSignatureMap.AcquireWarmLease(480, snapshot);
        return (lease, new(map));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Consume(MidoraProject project, WeakReference<ProjectTimeSignatureMap> expected, bool cacheOnly)
    {
        Assert.True(expected.TryGetTarget(out var original));
        if (cacheOnly)
        {
            Assert.True(ProjectTimeSignatureMap.TryGetCached(project, out var map));
            Assert.Same(original, map);
        }
        else Assert.Same(original, ProjectTimeSignatureMap.GetOrCreate(project));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<ProjectTimeSignatureMap> map) => map.TryGetTarget(out _);

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
