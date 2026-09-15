using System.Text;

namespace Midora.Common;

/// <summary>
/// Owns one versioned direct child of a Midora temporary-data root. Creation
/// touches only the root and the newly owned child; it never scans siblings.
/// Startup/session recovery explicitly calls <see cref="ClearInactiveDirectories"/>.
/// Unknown, unmanifested, active, or reparse-point children are never deleted.
/// </summary>
public sealed class MidoraOwnedTemporaryDirectoryLease : IDisposable
{
    private const string ManifestFileName = "midora-temp.manifest";
    private const string ActiveLockFileName = "midora-temp.active.lock";
    private const string ManifestMagic = "MIDORA_OWNED_TEMPORARY_V1";
    private static readonly object Sync = new();
    private readonly string _rootPath;
    private readonly FileStream _activeLock;
    private int _disposed;

    private MidoraOwnedTemporaryDirectoryLease(string rootPath, string purpose)
    {
        _rootPath = NormalizeRoot(rootPath);
        ValidatePurpose(purpose);
        lock (Sync)
        {
            Directory.CreateDirectory(_rootPath);
            RequireOrdinaryDirectory(_rootPath);

            string directoryName = purpose + "-" + Guid.NewGuid().ToString("N");
            DirectoryPath = ValidateDirectChild(
                _rootPath,
                Path.Combine(_rootPath, directoryName));
            FileStream? activeLock = null;
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                WriteManifest(DirectoryPath, directoryName, purpose);
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

    public static MidoraOwnedTemporaryDirectoryLease Create(
        string rootPath,
        string purpose) => new(rootPath, purpose);

    public static int ClearInactiveDirectories(string rootPath)
    {
        string normalized = NormalizeRoot(rootPath);
        lock (Sync)
        {
            return ClearInactiveDirectoriesCore(normalized);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _activeLock.Dispose();
        TryDeleteOwnedDirectory(_rootPath, DirectoryPath);
    }

    private static int ClearInactiveDirectoriesCore(string rootPath)
    {
        string[] candidates;
        try
        {
            if (!Directory.Exists(rootPath))
            {
                return 0;
            }
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
                if (IsReparsePoint(path)
                    || !HasValidManifest(path)
                    || IsActive(path))
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
                // Recovery is best effort and retries at the next explicit
                // startup/session cleanup, never on an ordinary lease creation.
            }
        }
        return removed;
    }

    private static void WriteManifest(
        string directoryPath,
        string directoryName,
        string purpose)
    {
        string path = Path.Combine(directoryPath, ManifestFileName);
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
            ManifestMagic + "\n" + directoryName + "\n" + purpose + "\n");
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

    private static bool HasValidManifest(string directoryPath)
    {
        try
        {
            string name = Path.GetFileName(directoryPath);
            string[] lines = File.ReadAllLines(
                Path.Combine(directoryPath, ManifestFileName),
                Encoding.UTF8);
            return lines.Length == 3
                && string.Equals(lines[0], ManifestMagic, StringComparison.Ordinal)
                && string.Equals(lines[1], name, StringComparison.Ordinal)
                && IsValidPurpose(lines[2])
                && name.StartsWith(lines[2] + "-", StringComparison.Ordinal)
                && Guid.TryParseExact(name[(lines[2].Length + 1)..], "N", out _);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException
            or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsActive(string directoryPath)
    {
        try
        {
            using FileStream ignored = new(
                Path.Combine(directoryPath, ActiveLockFileName),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDeleteOwnedDirectory(string rootPath, string directoryPath)
    {
        try
        {
            string path = ValidateDirectChild(rootPath, directoryPath);
            if (Directory.Exists(path)
                && !IsReparsePoint(path)
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
            // The next explicit startup/session cleanup retries this owned
            // stale directory without burdening ordinary lease creation.
        }
    }

    private static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException(
                "A Midora temporary-data root must be fully qualified.",
                nameof(rootPath));
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
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
                "A Midora temporary directory is outside its owned root.");
        }
        return path;
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Midora temporary-data roots cannot be reparse-point directories.");
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

    private static void ValidatePurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        if (!IsValidPurpose(purpose))
        {
            throw new ArgumentException(
                "A temporary-directory purpose must contain 1-48 lower-case ASCII letters, digits, or hyphens.",
                nameof(purpose));
        }
    }

    private static bool IsValidPurpose(string purpose) =>
        purpose.Length is >= 1 and <= 48
        && purpose.All(value => value is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');
}
