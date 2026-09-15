using System.Text;
using Midora.Domain;
using Midora.Persistence;

namespace Midora.Persistence.Tests;

public sealed class SessionContentDirectoryLeaseTests
{
    [Fact]
    public void CreatingLeaseRemovesOnlyManifestedCurrentInactiveDirectories()
    {
        using TemporaryDirectory root = new();
        string staleName = "session-" + Guid.NewGuid().ToString("N");
        string stale = Path.Combine(root.Path, staleName);
        Directory.CreateDirectory(stale);
        File.WriteAllText(
            Path.Combine(stale, "session.manifest"),
            "MIDORA_SESSION_CONTENT_V1\n" + staleName + "\n",
            new UTF8Encoding(false));
        string legacy = Path.Combine(root.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "mt_1.mpk"), [1, 2, 3]);
        string unknown = Path.Combine(root.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep.txt"), "keep");

        using MidoraProject project = new(192);
        SessionContentDirectoryLease lease =
            SessionContentDirectoryLease.CreateForTests(project, root.Path);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(legacy));
        Assert.True(Directory.Exists(unknown));
        Assert.True(Directory.Exists(lease.DirectoryPath));
    }

    [Fact]
    public void CleanupPreservesActiveAndUnknownDirectories()
    {
        using TemporaryDirectory root = new();
        using MidoraProject project = new(192);
        SessionContentDirectoryLease lease =
            SessionContentDirectoryLease.CreateForTests(project, root.Path);
        string active = lease.DirectoryPath;
        string unrelated = Path.Combine(root.Path, "user-content");
        Directory.CreateDirectory(unrelated);

        int removed = SessionContentDirectoryLease.ClearInactiveDirectories(root.Path);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void ProjectDisposalDeletesOnlyItsOwnedDirectory()
    {
        using TemporaryDirectory root = new();
        string unrelated = Path.Combine(root.Path, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        string owned;
        MidoraProject project = new(192);
        SessionContentDirectoryLease lease =
            SessionContentDirectoryLease.CreateForTests(project, root.Path);
        owned = lease.DirectoryPath;

        project.Dispose();

        Assert.False(Directory.Exists(owned));
        Assert.True(File.Exists(unrelated));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"midora-session-content-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
