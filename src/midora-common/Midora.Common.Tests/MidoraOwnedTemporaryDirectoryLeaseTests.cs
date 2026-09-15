using System.Collections.Concurrent;
using System.Text;
using Midora.Common;

namespace Midora.Common.Tests;

public sealed class MidoraOwnedTemporaryDirectoryLeaseTests
{
    [Fact]
    public void CleanupDeletesOnlyManifestedInactiveDirectChildren()
    {
        using TemporaryDirectory root = new();
        string staleName = "compiler-run-" + Guid.NewGuid().ToString("N");
        string stale = Path.Combine(root.Path, staleName);
        Directory.CreateDirectory(stale);
        File.WriteAllText(
            Path.Combine(stale, "midora-temp.manifest"),
            "MIDORA_OWNED_TEMPORARY_V1\n" + staleName + "\ncompiler-run\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(stale, "midora-temp.active.lock"), string.Empty);
        string unknown = Path.Combine(root.Path, "user-content");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");

        int removed = MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(unknown));
    }

    [Fact]
    public void ActiveLeaseSurvivesCleanupAndDisposeRemovesIt()
    {
        using TemporaryDirectory root = new();
        string path;
        using (MidoraOwnedTemporaryDirectoryLease lease =
            MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "audio-worker"))
        {
            path = lease.DirectoryPath;
            Assert.Equal(0, MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path));
            Assert.True(Directory.Exists(path));
        }

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void CreatingALeaseDoesNotSweepOrRemoveExistingStaleSiblings()
    {
        using TemporaryDirectory root = new();
        string stale = CreateStaleDirectory(root.Path);
        string unknown = Path.Combine(root.Path, "user-content");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");
        string firstPath;
        using (var first = MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "bulk-edit"))
        {
            firstPath = first.DirectoryPath;
            using var second = MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "bulk-edit");
            // Before this regression fix, creating either lease swept the root
            // and deleted this valid but inactive sibling as an implicit effect.
            Assert.True(Directory.Exists(stale));
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(second.DirectoryPath));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(unknown, "keep.txt")));
            Assert.Equal(1, MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path));
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(first.DirectoryPath));
            Assert.True(Directory.Exists(second.DirectoryPath));
        }
        Assert.False(Directory.Exists(firstPath));
        Assert.True(Directory.Exists(unknown));
    }

    [Fact]
    public async Task ConcurrentCreationAndExplicitCleanupNeverDeleteActiveOrUnknownDirectories()
    {
        using TemporaryDirectory root = new();
        string stale = CreateStaleDirectory(root.Path);
        string unknown = Path.Combine(root.Path, "not-owned");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "unmodified");
        var leases = new ConcurrentBag<MidoraOwnedTemporaryDirectoryLease>();
        var firstCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCleanupComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int creators = 4, perCreator = 8;
        int removed = 0;
        try
        {
            Task[] creation = Enumerable.Range(0, creators).Select(_ => Task.Run(async () =>
            {
                try
                {
                    for (int index = 0; index < perCreator; index++)
                    {
                        var lease = MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "bulk-edit");
                        leases.Add(lease);
                        File.WriteAllText(Path.Combine(lease.DirectoryPath, "records.pages"), "active");
                        firstCreated.TrySetResult();
                        if (index == 0) await firstCleanupComplete.Task;
                        Assert.True(Directory.Exists(lease.DirectoryPath));
                        await Task.Yield();
                    }
                }
                catch (Exception exception)
                {
                    firstCreated.TrySetException(exception);
                    throw;
                }
            })).ToArray();
            Task cleanup = Task.Run(async () =>
            {
                try
                {
                    await firstCreated.Task;
                    // Creators retain every lease until all checks are complete.
                    // The first pass is guaranteed to overlap an active owner.
                    removed += MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path);
                    firstCleanupComplete.TrySetResult();
                    for (int index = 0; index < 64; index++)
                    {
                        removed += MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path);
                        await Task.Yield();
                    }
                }
                catch (Exception exception)
                {
                    firstCleanupComplete.TrySetException(exception);
                    throw;
                }
            });
            await Task.WhenAll(creation.Append(cleanup));
            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(stale));
            Assert.Equal(creators * perCreator, leases.Count);
            Assert.Equal(leases.Count, leases.Select(static value => value.DirectoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var lease in leases)
                Assert.Equal("active", File.ReadAllText(Path.Combine(lease.DirectoryPath, "records.pages")));
            Assert.Equal("unmodified", File.ReadAllText(Path.Combine(unknown, "keep.txt")));
            Assert.Equal(0, MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(root.Path));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
        Assert.All(leases, static lease => Assert.False(Directory.Exists(lease.DirectoryPath)));
        Assert.True(Directory.Exists(unknown));
    }

    [Fact]
    public void RelativeRootsAndInvalidPurposesAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MidoraOwnedTemporaryDirectoryLease.Create("relative", "compiler-run"));
        using TemporaryDirectory root = new();
        Assert.Throws<ArgumentException>(() =>
            MidoraOwnedTemporaryDirectoryLease.Create(root.Path, "Compiler_Run"));
    }

    private static string CreateStaleDirectory(string root)
    {
        string name = "compiler-run-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "midora-temp.manifest"),
            "MIDORA_OWNED_TEMPORARY_V1\n" + name + "\ncompiler-run\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(path, "midora-temp.active.lock"), string.Empty);
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                ".tmp",
                "midora-owned-temp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
