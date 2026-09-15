using System.Buffers;
using System.Security.Cryptography;
using Midora.Domain;

namespace Midora.Persistence;

internal static class PureMidiContentPackPersistenceV1
{
    public static ManifestFileEntryJsonV1 Materialize(
        PureMidiTrack track,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        string packagePath = MidoraPackagePathsV1.PureMidiContentPack(track.Id);
        string filePath = Path.Combine(
            contentRoot,
            packagePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        PureMidiContentPack? reusable = FindReusablePack(track);
        string sha256;
        if (reusable is not null)
        {
            sha256 = CopyReusablePackAndHash(
                reusable,
                filePath,
                cancellationToken);
        }
        else
        {
            WriteMergedPack(track, filePath, cancellationToken);
            using FileStream hashInput = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                256 * 1024,
                FileOptions.SequentialScan);
            sha256 = Convert.ToHexStringLower(SHA256.HashData(hashInput));
        }
        return new()
        {
            Path = packagePath,
            Kind = "pure-midi-content-pack",
            SchemaVersion = PersistenceContractV1.SchemaVersion,
            Sha256 = sha256
        };
    }

    private static string CopyReusablePackAndHash(
        PureMidiContentPack reusable,
        string filePath,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            using FileStream source = new(
                reusable.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                256 * 1024,
                FileOptions.SequentialScan);
            using FileStream destination = new(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                256 * 1024,
                FileOptions.SequentialScan);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                destination.Write(buffer, 0, read);
                hash.AppendData(buffer.AsSpan(0, read));
            }
            destination.Flush(flushToDisk: true);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static PureMidiContentPack? FindReusablePack(PureMidiTrack track)
    {
        PureMidiContentPack? candidate = null;
        HashSet<MidoraId> currentSegmentIds = track.Segments.Select(value => value.Id).ToHashSet();
        foreach (MidiSegment segment in track.Segments)
        {
            PureMidiContentPack? owner = segment.TryGetPristineContentPack();
            if (owner is null)
            {
                if (segment.Notes.Count != 0
                    || segment.ChannelEvents.Count != 0
                    || segment.OpaqueEvents.Count != 0)
                {
                    return null;
                }
                continue;
            }
            if (candidate is not null && !ReferenceEquals(candidate, owner)) return null;
            candidate = owner;
        }
        if (candidate is null) return null;
        return candidate.SegmentIds.All(currentSegmentIds.Contains) ? candidate : null;
    }

    private static void WriteMergedPack(
        PureMidiTrack track,
        string filePath,
        CancellationToken cancellationToken)
    {
        using PureMidiContentPackWriter writer = new(filePath, cancellationToken);
        foreach (MidiSegment segment in track.Segments)
        {
            foreach (DirectMidiNoteValue value in segment.Notes.EnumerateValues(cancellationToken))
            {
                writer.AddNote(segment.Id, value);
            }
            foreach (DirectMidiChannelEventValue value in segment.ChannelEvents.EnumerateValues(cancellationToken))
            {
                writer.AddChannelEvent(segment.Id, value);
            }
            foreach (OpaqueMidiEventValue value in segment.OpaqueEvents.EnumerateValues(cancellationToken))
            {
                writer.AddOpaqueEvent(segment.Id, value);
            }
        }
        using PureMidiContentPack completed = writer.Complete();
    }
}
