using System.Text;
using Midora.Common;
using Midora.Domain;

namespace Midora.Persistence;

internal sealed class SessionContentDirectoryLease : IDisposable
{
    private const string SessionPrefix = "session-";
    private const string ManifestFileName = "session.manifest";
    private const string ActiveLockFileName = "session.active.lock";
    private const string ManifestMagic = "MIDORA_SESSION_CONTENT_V1";
    private static readonly object CreationSync = new();
    private readonly string _rootPath;
    private readonly FileStream _activeLock;
    private int _disposed;

    private SessionContentDirectoryLease(string rootPath)
    {
        _rootPath = NormalizeRoot(rootPath);
        lock (CreationSync)
        {
            Directory.CreateDirectory(_rootPath);
            _ = ClearInactiveDirectoriesCore(_rootPath, excludedSessionPath: null);

            string sessionName = SessionPrefix + Guid.NewGuid().ToString("N");
            DirectoryPath = ValidateSessionPath(
                _rootPath,
                Path.Combine(_rootPath, sessionName));
            FileStream? activeLock = null;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                File.WriteAllText(
                    Path.Combine(DirectoryPath, ManifestFileName),
                    ManifestMagic + "\n" + sessionName + "\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                activeLock = new FileStream(
                    Path.Combine(DirectoryPath, ActiveLockFileName),
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
                _activeLock = activeLock;
            }
            catch
            {
                activeLock?.Dispose();
                TryDeleteOwnedDirectory(_rootPath, DirectoryPath);
                throw;
            }
        }
    }

    public string DirectoryPath { get; }

    public static SessionContentDirectoryLease Create(MidoraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        SessionContentDirectoryLease lease = new(DefaultRootPath());
        try
        {
            project.RegisterRuntimeResource(lease);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static SessionContentDirectoryLease CreateForTests(
        MidoraProject project,
        string rootPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        SessionContentDirectoryLease lease = new(rootPath);
        try
        {
            project.RegisterRuntimeResource(lease);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    internal static int ClearInactiveDirectories(string rootPath) =>
        ClearInactiveDirectoriesSynchronized(NormalizeRoot(rootPath));

    internal static int ClearDefaultInactiveDirectories() =>
        ClearInactiveDirectoriesSynchronized(NormalizeRoot(DefaultRootPath()));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _activeLock.Dispose();
        TryDeleteOwnedDirectory(_rootPath, DirectoryPath);
    }

    private static string DefaultRootPath() =>
        MidoraProgramData.Current.SessionContentDirectory;

    private static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
    }

    private static int ClearInactiveDirectoriesCore(
        string rootPath,
        string? excludedSessionPath)
    {
        string[] candidates;
        try
        {
            if (!Directory.Exists(rootPath)) return 0;
            candidates = Directory.GetDirectories(rootPath, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DirectoryNotFoundException)
        {
            return 0;
        }

        int removed = 0;
        foreach (string candidate in candidates)
        {
            try
            {
                string path = ValidateDirectChild(rootPath, candidate);
                if ((excludedSessionPath is not null
                        && string.Equals(
                            path,
                            excludedSessionPath,
                            StringComparison.OrdinalIgnoreCase))
                    || IsReparsePoint(path))
                {
                    continue;
                }

                bool currentSession = IsCurrentSessionPath(path) && HasValidManifest(path);
                if (!currentSession || IsSessionActive(path))
                {
                    continue;
                }

                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or DirectoryNotFoundException)
            {
                // Cleanup is best effort. A later Project session retries this child.
            }
        }
        return removed;
    }

    private static int ClearInactiveDirectoriesSynchronized(string rootPath)
    {
        lock (CreationSync)
        {
            return ClearInactiveDirectoriesCore(rootPath, excludedSessionPath: null);
        }
    }

    private static string ValidateSessionPath(string rootPath, string candidate)
    {
        string path = ValidateDirectChild(rootPath, candidate);
        if (!IsCurrentSessionPath(path))
        {
            throw new InvalidDataException(
                "The session-content directory does not use the current owned name format.");
        }
        return path;
    }

    private static string ValidateDirectChild(string rootPath, string candidate)
    {
        string path = Path.GetFullPath(candidate);
        if (!string.Equals(
            Path.GetDirectoryName(path),
            rootPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The session-content directory is outside the Midora session-content root.");
        }
        return path;
    }

    private static bool IsCurrentSessionPath(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith(SessionPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name[SessionPrefix.Length..], "N", out _);
    }

    private static bool HasValidManifest(string sessionPath)
    {
        try
        {
            string name = Path.GetFileName(sessionPath);
            string content = File.ReadAllText(
                Path.Combine(sessionPath, ManifestFileName),
                Encoding.UTF8);
            return string.Equals(
                content,
                ManifestMagic + "\n" + name + "\n",
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsSessionActive(string sessionPath)
    {
        try
        {
            using FileStream ignored = new(
                Path.Combine(sessionPath, ActiveLockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or FileNotFoundException)
        {
            return true;
        }
    }

    private static void TryDeleteOwnedDirectory(string rootPath, string sessionPath)
    {
        try
        {
            string path = ValidateSessionPath(rootPath, sessionPath);
            if (!IsReparsePoint(path)
                && Directory.Exists(path)
                && HasValidManifest(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or DirectoryNotFoundException)
        {
            // Cleanup is best effort. The next Project session retries this directory.
        }
    }
}
