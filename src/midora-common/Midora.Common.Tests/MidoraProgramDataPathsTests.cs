using Midora.Common;

namespace Midora.Common.Tests;

public sealed class MidoraProgramDataPathsTests
{
    [Fact]
    public void ResolveUsesOnlyTheCanonicalProgramRootLayout()
    {
        using TemporaryDirectory root = new();

        MidoraProgramDataPaths paths = MidoraProgramData.Resolve(root.Path);

        Assert.Equal(Path.GetFullPath(root.Path), paths.ProgramRoot);
        Assert.Equal(Path.Combine(root.Path, "Data", "Preferences"), paths.PreferencesDirectory);
        Assert.Equal(Path.Combine(root.Path, "Data", "Recent"), paths.RecentDirectory);
        Assert.Equal(Path.Combine(root.Path, "Data", "Catalogs"), paths.CatalogsDirectory);
        Assert.Equal(Path.Combine(root.Path, "Data", "Presets"), paths.PresetsDirectory);
        Assert.Equal(Path.Combine(root.Path, "Data", "Diagnostics"), paths.DiagnosticsDirectory);
        Assert.Equal(Path.Combine(root.Path, ".tmp", "AudioCache"), paths.AudioCacheDirectory);
        Assert.Equal(Path.Combine(root.Path, ".tmp", "SessionContent"), paths.SessionContentDirectory);
        Assert.Equal(Path.Combine(root.Path, ".tmp", "CompilerRuns"), paths.CompilerRunsDirectory);
        Assert.Equal(
            Path.Combine(root.Path, ".tmp", "AudioWorkerExchange"),
            paths.AudioWorkerExchangeDirectory);
        Assert.Equal(
            Path.Combine(root.Path, "Data", "Preferences", "preferences-v2.json"),
            paths.PreferencesFilePath);
    }

    [Fact]
    public void StartupProbeCreatesTheCompleteVisibleTreeAndLeavesNoProbeArtifacts()
    {
        using TemporaryDirectory root = new();
        MidoraProgramDataPaths paths = MidoraProgramData.Resolve(root.Path);

        MidoraProgramData.EnsureReadyAndProbe(paths);

        Assert.True(Directory.Exists(paths.DiagnosticsDirectory));
        Assert.True(Directory.Exists(paths.AudioCacheDirectory));
        Assert.True(Directory.Exists(paths.AudioWorkerExchangeDirectory));
        Assert.Equal(
            FileAttributes.None,
            File.GetAttributes(paths.TemporaryRoot) & FileAttributes.Hidden);
        Assert.Empty(Directory.GetFileSystemEntries(
            paths.DataRoot,
            ".midora-capability-probe-*",
            SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.GetFileSystemEntries(
            paths.TemporaryRoot,
            ".midora-capability-probe-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void SingleInstanceScopeIsStablePerUserAndProgramRoot()
    {
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();

        string firstScope = MidoraProgramData.Resolve(first.Path).SingleInstanceScope;
        string sameScope = MidoraProgramData.Resolve(first.Path + Path.DirectorySeparatorChar)
            .SingleInstanceScope;
        string secondScope = MidoraProgramData.Resolve(second.Path).SingleInstanceScope;

        Assert.Equal(firstScope, sameScope);
        Assert.NotEqual(firstScope, secondScope);
        Assert.Equal(64, firstScope.Length);
    }

    [Fact]
    public void StartupProbeFailsClosedWhenThePortableTreeCannotBeCreated()
    {
        using TemporaryDirectory root = new();
        MidoraProgramDataPaths paths = MidoraProgramData.Resolve(root.Path);
        File.WriteAllText(paths.DataRoot, "occupied");

        Assert.ThrowsAny<IOException>(() => MidoraProgramData.EnsureReadyAndProbe(paths));
        Assert.False(Directory.Exists(paths.TemporaryRoot));
    }

    [Fact]
    public void RelativeAndUncProgramRootsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => MidoraProgramData.Resolve("relative"));
        Assert.Throws<InvalidOperationException>(() =>
            MidoraProgramData.Resolve(@"\\server\share\Midora"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "midora-program-root-tests-" + Guid.NewGuid().ToString("N"));
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
