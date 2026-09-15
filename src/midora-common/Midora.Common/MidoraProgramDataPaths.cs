using System.Security.Cryptography;
using System.Text;

namespace Midora.Common;

/// <summary>
/// Resolves Midora-owned portable data from the executable directory. This type never
/// consults the process current directory, LocalApplicationData, or the system temp path.
/// </summary>
public sealed record MidoraProgramDataPaths(
    string ProgramRoot,
    string DataRoot,
    string TemporaryRoot,
    string PreferencesDirectory,
    string RecentDirectory,
    string CatalogsDirectory,
    string PresetsDirectory,
    string DiagnosticsDirectory,
    string AudioCacheDirectory,
    string SessionContentDirectory,
    string CompilerRunsDirectory,
    string AudioWorkerExchangeDirectory)
{
    public string PreferencesFilePath =>
        Path.Combine(PreferencesDirectory, "preferences-v2.json");

    public string RecentProjectsFilePath =>
        Path.Combine(RecentDirectory, "recent-projects-v1.json");

    public string SingleInstanceScope
    {
        get
        {
            string identity = Environment.UserName + "\n" + ProgramRoot.ToUpperInvariant();
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }
}

public static class MidoraProgramData
{
    private static readonly Lazy<MidoraProgramDataPaths> CurrentValue = new(
        () => Resolve(AppContext.BaseDirectory),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static MidoraProgramDataPaths Current => CurrentValue.Value;

    public static MidoraProgramDataPaths Resolve(string programRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programRoot);
        if (!Path.IsPathFullyQualified(programRoot))
        {
            throw new ArgumentException(
                "The Midora program root must be fully qualified.",
                nameof(programRoot));
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programRoot));
        if (new Uri(root + Path.DirectorySeparatorChar).IsUnc)
        {
            throw new InvalidOperationException(
                "Midora portable storage requires a local fixed drive; UNC paths are not supported.");
        }

        string data = Path.Combine(root, "Data");
        string temporary = Path.Combine(root, ".tmp");
        return new(
            root,
            data,
            temporary,
            Path.Combine(data, "Preferences"),
            Path.Combine(data, "Recent"),
            Path.Combine(data, "Catalogs"),
            Path.Combine(data, "Presets"),
            Path.Combine(data, "Diagnostics"),
            Path.Combine(temporary, "AudioCache"),
            Path.Combine(temporary, "SessionContent"),
            Path.Combine(temporary, "CompilerRuns"),
            Path.Combine(temporary, "AudioWorkerExchange"));
    }

    /// <summary>
    /// Creates the owned directory tree and verifies the filesystem capabilities needed
    /// by Midora's transactional stores before the primary application window is created.
    /// </summary>
    public static void EnsureReadyAndProbe(MidoraProgramDataPaths? paths = null)
    {
        paths ??= Current;
        RequireFixedLocalDrive(paths.ProgramRoot);
        RequireOrdinaryDirectory(paths.ProgramRoot, mustExist: true);

        string[] directories =
        [
            paths.DataRoot,
            paths.TemporaryRoot,
            paths.PreferencesDirectory,
            paths.RecentDirectory,
            paths.CatalogsDirectory,
            paths.PresetsDirectory,
            paths.DiagnosticsDirectory,
            paths.AudioCacheDirectory,
            paths.SessionContentDirectory,
            paths.CompilerRunsDirectory,
            paths.AudioWorkerExchangeDirectory
        ];
        foreach (string directory in directories)
        {
            RequireDescendant(paths.ProgramRoot, directory);
            Directory.CreateDirectory(directory);
            RequireOrdinaryDirectory(directory, mustExist: true);
        }

        // A leading dot is a visible naming convention on Windows. Never set Hidden.
        FileAttributes temporaryAttributes = File.GetAttributes(paths.TemporaryRoot);
        if ((temporaryAttributes & FileAttributes.Hidden) != 0)
        {
            File.SetAttributes(
                paths.TemporaryRoot,
                temporaryAttributes & ~FileAttributes.Hidden);
        }

        ProbeTransactionalDirectory(paths.DataRoot);
        ProbeTransactionalDirectory(paths.TemporaryRoot);
        _ = MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(
            paths.CompilerRunsDirectory);
        _ = MidoraOwnedTemporaryDirectoryLease.ClearInactiveDirectories(
            paths.AudioWorkerExchangeDirectory);
    }

    private static void ProbeTransactionalDirectory(string root)
    {
        string probe = Path.Combine(root, ".midora-capability-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probe);
        string current = Path.Combine(probe, "current.bin");
        string replacement = Path.Combine(probe, "replacement.bin");
        string backup = Path.Combine(probe, "replace-backup.bin");
        string lockPath = Path.Combine(probe, "exclusive.lock");
        try
        {
            WriteAndFlush(current, [0x4d, 0x49, 0x44, 0x4f, 0x52, 0x41]);
            WriteAndFlush(replacement, [0x03]);
            File.Replace(replacement, current, backup);
            if (File.ReadAllBytes(current) is not [0x03]
                || File.ReadAllBytes(backup) is not [0x4d, 0x49, 0x44, 0x4f, 0x52, 0x41])
            {
                throw new IOException(
                    "The portable storage root did not preserve atomic replace semantics.");
            }

            using FileStream owner = new(
                lockPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            bool exclusiveLockRejected = false;
            try
            {
                using FileStream unexpected = new(
                    lockPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException)
            {
                exclusiveLockRejected = true;
            }
            if (!exclusiveLockRejected)
            {
                throw new IOException(
                    "The portable storage root did not enforce exclusive file locks.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(probe, recursive: true);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException)
            {
                throw new IOException(
                    "The portable storage root cannot remove owned temporary data.",
                    exception);
            }
        }
    }

    private static void WriteAndFlush(string path, ReadOnlySpan<byte> bytes)
    {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void RequireFixedLocalDrive(string path)
    {
        string root = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("The Midora program root has no drive root.");
        DriveInfo drive = new(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
        {
            throw new InvalidOperationException(
                "Midora portable storage requires a ready local fixed drive.");
        }
    }

    private static void RequireOrdinaryDirectory(string path, bool mustExist)
    {
        if (mustExist && !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"The Midora portable storage directory does not exist: {path}");
        }
        if (Directory.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Midora portable storage does not allow reparse-point directories: {path}");
        }
    }

    private static void RequireDescendant(string root, string candidate)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalizedCandidate = Path.GetFullPath(candidate);
        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "A Midora-owned data directory resolved outside the program root.");
        }
    }
}
