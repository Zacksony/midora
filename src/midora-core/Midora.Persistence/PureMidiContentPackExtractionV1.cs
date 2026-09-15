using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

internal sealed class PureMidiContentPackExtractionV1 : IDisposable
{
    private readonly MidoraProject _project;
    private readonly string _root;
    private readonly bool _usesTrustedStagingContent;
    private readonly PureMidiContentPackDecodedCache _decodedCache;
    private bool _disposed;

    public PureMidiContentPackExtractionV1(
        MidoraProject project,
        string? trustedStagingContentRoot = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        if (trustedStagingContentRoot is null)
        {
            SessionContentDirectoryLease directoryLease =
                SessionContentDirectoryLease.Create(project);
            _root = directoryLease.DirectoryPath;
        }
        else
        {
            _root = Path.GetFullPath(trustedStagingContentRoot);
            if (!Directory.Exists(_root))
                throw new DirectoryNotFoundException(
                    "The save-owned staging content directory does not exist.");
            _usesTrustedStagingContent = true;
        }
        _project.RegisterRuntimeResource(this);
        _decodedCache = new();
        _project.RegisterRuntimeResource(_decodedCache);
    }

    public async Task AttachAsync(
        PureMidiTrack track,
        string packagePath,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestFileEntryJsonV1> manifestIndex,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(track);
        string expectedPath = MidoraPackagePathsV1.PureMidiContentPack(track.Id);
        if (!string.Equals(packagePath, expectedPath, StringComparison.Ordinal))
            throw new InvalidDataException("Pure MIDI Track content-pack path does not match its stable ID.");
        if (!manifestIndex.TryGetValue(packagePath, out ManifestFileEntryJsonV1? manifest)
            || manifest.Kind != "pure-midi-content-pack"
            || manifest.SchemaVersion != PersistenceContractV1.SchemaVersion)
        {
            throw new InvalidDataException(
                "Pure MIDI content pack is absent from the manifest or has an invalid kind/schemaVersion.");
        }
        if (!entries.TryGetValue(packagePath, out ZipArchiveEntry? entry))
            throw new InvalidDataException("Pure MIDI content pack is absent from the Zip container.");

        string destinationPath = _usesTrustedStagingContent
            ? ResolveTrustedStagingPath(packagePath)
            : Path.Combine(_root, $"mt_{track.Id.Value}.mpk");
        PureMidiContentPack? pack = null;
        try
        {
            if (_usesTrustedStagingContent)
            {
                if (!File.Exists(destinationPath)
                    || new FileInfo(destinationPath).Length != entry.Length)
                {
                    throw new InvalidDataException(
                        "The save-owned staged Pure MIDI content pack length is inconsistent.");
                }
                // Opening first holds a read handle that denies writers while
                // the Zip entry is validated. The staging file was the sole
                // source of the manifest hash and the stored Zip entry in this
                // still-unpublished save transaction.
                pack = PureMidiContentPack.Open(destinationPath, _decodedCache);
            }

            await ValidateAndMaybeExtractAsync(
                entry,
                manifest.Sha256,
                _usesTrustedStagingContent ? null : destinationPath,
                cancellationToken).ConfigureAwait(false);
            pack ??= PureMidiContentPack.Open(destinationPath, _decodedCache);
            HashSet<MidoraId> segmentIds = track.Segments.Select(value => value.Id).ToHashSet();
            if (pack.SegmentIds.Any(value => !segmentIds.Contains(value)))
                throw new InvalidDataException("Pure MIDI content pack references a Segment absent from its Track.");
            foreach (MidiSegment segment in track.Segments)
                segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            _project.RegisterRuntimeResource(pack);
            pack = null;
        }
        catch
        {
            if (!_usesTrustedStagingContent) TryDelete(destinationPath);
            throw;
        }
        finally
        {
            pack?.Dispose();
        }
    }

    private static async Task ValidateAndMaybeExtractAsync(
        ZipArchiveEntry entry,
        string expectedSha256,
        string? destinationPath,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream source = entry.Open();
            await using FileStream? destination = destinationPath is null
                ? null
                : new(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    256 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (destination is not null)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }
                hash.AppendData(buffer.AsSpan(0, read));
                total = checked(total + read);
            }
            if (destination is not null)
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != entry.Length)
                throw new InvalidDataException("Pure MIDI content pack extracted length is inconsistent.");
            string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Pure MIDI content-pack SHA-256 does not match manifest.json.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string ResolveTrustedStagingPath(string packagePath)
    {
        string path = Path.GetFullPath(Path.Combine(
            _root,
            packagePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The Pure MIDI content-pack path escapes the save-owned staging directory.");
        return path;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
