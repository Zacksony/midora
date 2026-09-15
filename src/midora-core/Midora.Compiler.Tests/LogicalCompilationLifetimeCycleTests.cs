using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class LogicalCompilationLifetimeCycleTests
{
    [Fact]
    public void TwentyEditCancelFailureCloseCyclesReleaseOwnersAndPreserveOldResults()
    {
        List<WeakReference> owners = [];
        for (int cycle = 0; cycle < 20; cycle++) owners.AddRange(CompileCycle());
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        Assert.All(owners, owner => Assert.False(owner.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CompileCycle()
    {
        using var fixture = LogicalCompiledMemoryOracle.Create("expansion", 4, 64, 4, 2);
        using var invalid = LogicalCompiledMemoryOracle.Create("invalid", 8);
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult first = compiler.CompileFull(fixture.Project);
        Assert.True(first.HasPagedLogicalEvents);
        string digest = LogicalCompiledMemoryOracle.Digest(first);
        fixture.Segment.Notes[0].StartTick++;
        CanonicalCompiledResult edited = compiler.CompileIncremental(fixture.Project,
            new ProjectChangeSet { TrackIds = { fixture.Track.Id } });
        Assert.NotEqual(digest, LogicalCompiledMemoryOracle.Digest(edited));
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => compiler.CompileFull(fixture.Project,
            cancellationToken: cancel.Token));
        Assert.False(compiler.CompileFull(invalid.Project).IsConsumable);
        fixture.Segment.Notes[0].StartTick--;
        CanonicalCompiledResult restored = compiler.CompileFull(fixture.Project);
        Assert.Equal(digest, LogicalCompiledMemoryOracle.Digest(restored));
        compiler.ClearCache();
        Assert.Equal(digest, LogicalCompiledMemoryOracle.Digest(first));
        return [new(fixture.Project), new(compiler), new(first), new(edited), new(restored)];
    }

    [Fact]
    public void TwentySpillAbortCyclesDoNotAccumulateFilesOrPoisonAnIndependentReader()
    {
        CompilerStorageBudget originalBudget = new(0);
        using CompilerValueStore<long> original = new(originalBudget);
        for (int i = 0; i < 8200; i++) original.Add(i);
        original.Seal();
        long originalSpill = originalBudget.SpillBytes;
        using IEnumerator<long> reader = original.GetEnumerator();
        for (int cycle = 0; cycle < 20; cycle++)
        {
            CompilerStorageBudget budget = new(0);
            using CompilerValueStore<long> pending = new(budget);
            for (int i = 0; i < 8200; i++) pending.Add(-i);
            pending.Seal();
            string path = (string)typeof(CompilerValueStore<long>)
                .GetField("_path", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pending)!;
            Assert.True(File.Exists(path));
            budget.Abort(); budget.Abort();
            Assert.False(File.Exists(path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
            Assert.Equal(0, budget.ResidentBytes);
            Assert.Equal(0, budget.MetadataBytes);
            Assert.Equal(0, budget.SpillBytes);
            Assert.True(reader.MoveNext()); Assert.Equal(cycle, reader.Current);
            Assert.Equal(originalSpill, originalBudget.SpillBytes);
        }
    }
}
