using System.Text.Json;
using System.Text.Json.Serialization;
using Midora.Common;

namespace Midora.Application;

public sealed record RecentProjectEntry(string Path, bool IsCurrentlyAvailable);

public sealed record RecentProjectsLoadResult(
    IReadOnlyList<string> Paths,
    ApplicationPreferenceNotice? Notice);

public sealed record RecentProjectsSaveResult(
    bool Succeeded,
    ApplicationPreferenceNotice? Notice);

public enum RecentProjectsUpdateStatus
{
    Applied,
    NoChange,
    Failed
}

public sealed record RecentProjectsUpdateResult(
    RecentProjectsUpdateStatus Status,
    ApplicationPreferenceNotice? Notice = null)
{
    public bool Succeeded => Status is
        RecentProjectsUpdateStatus.Applied or RecentProjectsUpdateStatus.NoChange;
}

public sealed class RecentProjectsStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 10;
    private const int MaximumFileBytes = 1024 * 1024;
    private readonly string _filePath;

    public RecentProjectsStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? GetDefaultFilePath());
    }

    public string FilePath => _filePath;

    public static string GetDefaultFilePath() =>
        MidoraProgramData.Current.RecentProjectsFilePath;

    public RecentProjectsLoadResult Load()
    {
        try
        {
            byte[] json;
            using (FileStream stream = new(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException(
                        $"The Recent Projects file exceeds {MaximumFileBytes} bytes.");
                }
                json = new byte[checked((int)stream.Length)];
                stream.ReadExactly(json);
            }

            RejectDuplicateJsonProperties(json);
            RecentProjectsJsonV1 dto = JsonSerializer.Deserialize(
                json,
                RecentProjectsJsonContextV1.Default.RecentProjectsJsonV1)
                ?? throw new InvalidDataException("The Recent Projects JSON is null.");
            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported Recent Projects schema version {dto.SchemaVersion}.");
            }
            if (dto.Paths is null)
            {
                throw new InvalidDataException("Recent Projects paths are required.");
            }
            string[] paths = ValidateAndNormalizePaths(dto.Paths);
            return new(Array.AsReadOnly(paths), null);
        }
        catch (FileNotFoundException)
        {
            return new(Array.Empty<string>(), null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(Array.Empty<string>(), null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            return new(
                Array.Empty<string>(),
                new ApplicationPreferenceNotice(
                    "RecentProjectsReadFailed",
                    "Recent Projects could not be read; an empty list is in use.",
                    exception));
        }
    }

    public RecentProjectsSaveResult Save(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] normalized = ValidateAndNormalizePaths(paths);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException(
                    "The Recent Projects path has no parent directory.");
            Directory.CreateDirectory(directory);
            RecentProjectsJsonV1 dto = new()
            {
                SchemaVersion = CurrentSchemaVersion,
                Paths = normalized
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                dto,
                RecentProjectsJsonContextV1.Default.RecentProjectsJsonV1);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.SequentialScan))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
            temporaryPath = null;
            return new(true, null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException)
        {
            return new(
                false,
                new ApplicationPreferenceNotice(
                    "RecentProjectsWriteFailed",
                    "Recent Projects could not be saved; the previous list remains in use.",
                    exception));
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // A failed best-effort cleanup does not replace the original write error.
                }
            }
        }
    }

    internal static string NormalizeProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(
                "A Recent Project path must be fully qualified.",
                nameof(path));
        }
        return Path.GetFullPath(path);
    }

    private static string[] ValidateAndNormalizePaths(IEnumerable<string> paths)
    {
        List<string> normalized = [];
        HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string fullPath = NormalizeProjectPath(path);
            if (!unique.Add(fullPath))
            {
                throw new ArgumentException(
                    "Recent Project paths must be unique using Windows path comparison.",
                    nameof(paths));
            }
            normalized.Add(fullPath);
            if (normalized.Count > MaximumEntries)
            {
                throw new ArgumentException(
                    $"Recent Projects cannot contain more than {MaximumEntries} entries.",
                    nameof(paths));
            }
        }
        return normalized.ToArray();
    }

    private static void RejectDuplicateJsonProperties(ReadOnlySpan<byte> json)
    {
        Utf8JsonReader reader = new(json, isFinalBlock: true, state: default);
        Stack<HashSet<string>> objectProperties = new();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.PropertyName:
                    if (objectProperties.Count == 0
                        || !objectProperties.Peek().Add(reader.GetString()!))
                    {
                        throw new InvalidDataException(
                            $"Duplicate JSON property '{reader.GetString()}'.");
                    }
                    break;
                case JsonTokenType.EndObject:
                    _ = objectProperties.Pop();
                    break;
            }
        }
    }
}

public sealed class RecentProjectsService
{
    private readonly object _sync = new();
    private readonly RecentProjectsStore _store;
    private string[] _paths;

    public RecentProjectsService(RecentProjectsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        RecentProjectsLoadResult loaded = _store.Load();
        _paths = loaded.Paths.ToArray();
        StartupNotice = loaded.Notice;
    }

    public ApplicationPreferenceNotice? StartupNotice { get; }

    public IReadOnlyList<RecentProjectEntry> Current
    {
        get
        {
            lock (_sync)
            {
                return Array.AsReadOnly(_paths
                    .Select(path => new RecentProjectEntry(path, File.Exists(path)))
                    .ToArray());
            }
        }
    }

    public RecentProjectsUpdateResult RecordSuccessfulProjectActivation(string projectPath)
    {
        string path = RecentProjectsStore.NormalizeProjectPath(projectPath);
        lock (_sync)
        {
            if (_paths.Length != 0
                && string.Equals(_paths[0], path, StringComparison.OrdinalIgnoreCase))
            {
                return new(RecentProjectsUpdateStatus.NoChange);
            }
            string[] candidate = _paths
                .Where(existing => !string.Equals(
                    existing,
                    path,
                    StringComparison.OrdinalIgnoreCase))
                .Prepend(path)
                .Take(RecentProjectsStore.MaximumEntries)
                .ToArray();
            return Persist(candidate);
        }
    }

    public RecentProjectsUpdateResult Remove(string projectPath)
    {
        string path = RecentProjectsStore.NormalizeProjectPath(projectPath);
        lock (_sync)
        {
            string[] candidate = _paths
                .Where(existing => !string.Equals(
                    existing,
                    path,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return candidate.Length == _paths.Length
                ? new(RecentProjectsUpdateStatus.NoChange)
                : Persist(candidate);
        }
    }

    public RecentProjectsUpdateResult Clear()
    {
        lock (_sync)
        {
            return _paths.Length == 0
                ? new(RecentProjectsUpdateStatus.NoChange)
                : Persist([]);
        }
    }

    private RecentProjectsUpdateResult Persist(string[] candidate)
    {
        RecentProjectsSaveResult saved = _store.Save(candidate);
        if (!saved.Succeeded)
        {
            return new(RecentProjectsUpdateStatus.Failed, saved.Notice);
        }
        _paths = candidate;
        return new(RecentProjectsUpdateStatus.Applied);
    }
}

internal sealed class RecentProjectsJsonV1
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; set; }

    [JsonPropertyOrder(1)]
    public string[]? Paths { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RecentProjectsJsonV1))]
internal sealed partial class RecentProjectsJsonContextV1 : JsonSerializerContext;
