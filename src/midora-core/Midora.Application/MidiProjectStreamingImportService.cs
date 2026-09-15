using System.Diagnostics;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Persistence;

namespace Midora.Application;

public static partial class MidiProjectImportService
{
    private const double ScanningSourceProgressEnd = 0.15;
    private const double ImportingEventsProgressEnd = 0.95;

    public static MidiProjectImportResult ImportFile(
        string path,
        string projectName,
        IReadOnlyDictionary<byte, byte>? zeroBasedPortMapping = null,
        CancellationToken cancellationToken = default,
        IProgress<MidiProjectImportProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(projectName);
        string fullPath = Path.GetFullPath(path);
        using FileStream importSource = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            256 * 1024,
            FileOptions.SequentialScan);
        progress?.Report(new(
            MidiProjectImportPhase.ScanningSource,
            0,
            0,
            0,
            importSource.Length,
            0));
        Stopwatch firstPassTimer = Stopwatch.StartNew();
        StreamingFirstPassVisitor firstPass = new(progress);
        StandardMidiFileStreamResult scan = StandardMidiFile.ScanType0Or1(
            importSource,
            firstPass,
            cancellationToken);
        firstPassTimer.Stop();
        progress?.Report(new(
            MidiProjectImportPhase.ScanningSource,
            scan.EventCount,
            scan.EventCount,
            scan.Header.FileByteCount,
            scan.Header.FileByteCount,
            ScanningSourceProgressEnd));
        StreamingTrackPlan[] tracks = NormalizeStreamingMetadata(firstPass.Tracks);
        List<MidiProjectImportDiagnostic> diagnostics = [];
        AddStreamingTrackNameDiagnostic(tracks, diagnostics);
        StreamingBucket[] buckets = BuildStreamingBuckets(scan.Header.Format, tracks);
        byte[] sourcePorts = buckets
            .Select(value => value.Key.SourcePort)
            .Concat(tracks.SelectMany(value => value.OpaquePortFirstOrders.Keys))
            .Concat(tracks.SelectMany(value => value.AcceptedMetadata?.SourcePort is byte port
                ? new[] { port }
                : Array.Empty<byte>()))
            .Distinct()
            .Order()
            .ToArray();
        Dictionary<byte, byte> portMap = ValidatePortMap(sourcePorts, zeroBasedPortMapping);

        MidoraProject project = new(scan.Header.TicksPerQuarterNote);
        SessionContentDirectoryLease backingDirectory =
            SessionContentDirectoryLease.Create(project);
        string backingRoot = backingDirectory.DirectoryPath;
        PureMidiContentPackDecodedCache decodedCache = new();
        project.RegisterRuntimeResource(decodedCache);
        List<StreamingImportTarget> targets = [];
        try
        {
            project.Metadata.ProjectName = projectName.Trim();
            ImportStreamingConductor(project, tracks, diagnostics, cancellationToken);
            CreateStreamingProjectStructure(
                project,
                buckets,
                portMap,
                backingRoot,
                decodedCache,
                targets,
                diagnostics,
                cancellationToken);

            Stopwatch secondPassTimer = Stopwatch.StartNew();
            StreamingSecondPassVisitor secondPass = new(
                project,
                tracks,
                targets,
                diagnostics,
                cancellationToken,
                scan.EventCount,
                scan.Header.FileByteCount,
                progress);
            importSource.Position = 0;
            StandardMidiFileStreamResult secondScan = StandardMidiFile.ScanType0Or1(
                importSource,
                secondPass,
                cancellationToken);
            if (secondScan.Header != scan.Header
                || secondScan.EventCount != scan.EventCount
                || secondScan.PayloadByteCount != scan.PayloadByteCount
                || !secondScan.Tracks.SequenceEqual(scan.Tracks))
            {
                throw new InvalidDataException(
                    "The MIDI source changed between the two streaming import passes.");
            }
            secondPass.Complete();
            secondPassTimer.Stop();

            progress?.Report(new(
                MidiProjectImportPhase.ValidatingProject,
                scan.EventCount,
                scan.EventCount,
                scan.Header.FileByteCount,
                scan.Header.FileByteCount,
                0.96));

            using MidoraCompiler compiler = new();
            CanonicalCompiledResult validation = compiler.CompileFull(project, cancellationToken: cancellationToken);
            if (!validation.IsConsumable)
            {
                string detail = string.Join(
                    Environment.NewLine,
                    validation.Diagnostics
                        .Where(value => value.Severity == DiagnosticSeverity.Error)
                        .Select(value => $"{value.Code}: {value.Message}"));
                throw new InvalidDataException(
                    "The imported MIDI cannot form a valid Midora Project."
                    + (detail.Length == 0 ? string.Empty : Environment.NewLine + detail));
            }

            progress?.Report(new(
                MidiProjectImportPhase.FinalizingProject,
                scan.EventCount,
                scan.EventCount,
                scan.Header.FileByteCount,
                scan.Header.FileByteCount,
                0.98));

            MidiProjectImportMetrics metrics = new(
                scan.Header.FileByteCount,
                scan.EventCount,
                scan.PayloadByteCount,
                targets.Sum(value => value.ImportedNoteCount),
                targets.Sum(value => value.ImportedChannelEventCount + value.ImportedOpaqueEventCount),
                targets.Sum(value => value.ContentPack?.PageCount ?? 0),
                targets.Sum(value => value.ContentPack is null
                    ? 0L
                    : new FileInfo(value.ContentPack.Path).Length),
                firstPassTimer.Elapsed,
                secondPassTimer.Elapsed,
                GC.GetTotalMemory(forceFullCollection: false),
                Environment.WorkingSet);
            return new(project, diagnostics.ToArray(), metrics);
        }
        catch (Exception importFailure)
        {
            List<Exception>? cleanupFailures = null;
            foreach (StreamingImportTarget target in targets)
            {
                try
                {
                    target.DisposePendingWriter();
                }
                catch (Exception cleanupFailure)
                {
                    (cleanupFailures ??= []).Add(cleanupFailure);
                }
            }
            try
            {
                project.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                (cleanupFailures ??= []).Add(cleanupFailure);
            }
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, importFailure);
                throw new AggregateException(
                    "MIDI import failed and one or more unpublished content-pack resources also failed to clean up.",
                    cleanupFailures);
            }
            throw;
        }
    }

    private static StreamingTrackPlan[] NormalizeStreamingMetadata(StreamingTrackPlan[] tracks)
    {
        Dictionary<int, MidoraTrackMetadata> normalized = [];
        HashSet<long> invalidRootIds = [];
        foreach (StreamingTrackPlan track in tracks)
        {
            if (track.MetadataCandidates.Count != 1)
            {
                foreach (StreamingMetadataCandidate metadataCandidate in track.MetadataCandidates)
                    invalidRootIds.Add(metadataCandidate.Metadata.SourceRootId);
                continue;
            }
            StreamingMetadataCandidate candidate = track.MetadataCandidates[0];
            (byte Port, byte Channel)[] routes = track.ChannelRoutes.Keys
                .OrderBy(value => value.Port)
                .ThenBy(value => value.Channel)
                .ToArray();
            byte? sourcePort = candidate.Metadata.SourcePort;
            byte? sourceChannel = candidate.Metadata.SourceChannel;
            bool valid = routes.Length <= 1;
            if (sourcePort.HasValue != sourceChannel.HasValue)
            {
                valid = false;
            }
            else if (sourcePort.HasValue)
            {
                valid &= candidate.SourcePort == sourcePort.Value;
                valid &= routes.Length == 0 || routes[0] == (sourcePort.Value, sourceChannel!.Value);
            }
            else if (routes.Length == 1)
            {
                sourcePort = routes[0].Port;
                sourceChannel = routes[0].Channel;
                valid &= candidate.SourcePort == sourcePort.Value;
            }
            else
            {
                valid = false;
            }
            if (sourcePort.HasValue)
                valid &= track.OpaquePortFirstOrders.Keys.All(value => value == sourcePort.Value);
            if (!valid)
            {
                invalidRootIds.Add(candidate.Metadata.SourceRootId);
                continue;
            }
            normalized.Add(
                track.SourceTrackIndex,
                candidate.Metadata with { SourcePort = sourcePort, SourceChannel = sourceChannel });
        }

        foreach (IGrouping<long, MidoraTrackMetadata> group in normalized.Values.GroupBy(value => value.SourceRootId))
        {
            MidoraTrackMetadata first = group.First();
            bool consistent = group.All(value =>
                value.RootOrder == first.RootOrder
                && value.RoutingMode == first.RoutingMode
                && value.ChannelMode == first.ChannelMode
                && value.SourcePort == first.SourcePort
                && value.SourceChannel == first.SourceChannel
                && string.Equals(value.RootName, first.RootName, StringComparison.Ordinal));
            bool uniqueTrackIds = group.Select(value => value.SourceTrackId).Distinct().Count() == group.Count();
            bool uniqueTrackOrders = group.Select(value => value.TrackOrder).Distinct().Count() == group.Count();
            if (!consistent || !uniqueTrackIds || !uniqueTrackOrders) invalidRootIds.Add(group.Key);
        }
        foreach (IGrouping<(byte? Port, byte? Channel), MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => (value.SourcePort, value.SourceChannel))
            .Where(value => value.Select(item => item.SourceRootId).Distinct().Count() > 1))
            invalidRootIds.UnionWith(group.Select(value => value.SourceRootId));
        foreach (IGrouping<int, MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => value.RootOrder)
            .Where(value => value.Select(item => item.SourceRootId).Distinct().Count() > 1))
            invalidRootIds.UnionWith(group.Select(value => value.SourceRootId));
        foreach (IGrouping<long, MidoraTrackMetadata> group in normalized.Values
            .GroupBy(value => value.SourceTrackId)
            .Where(value => value.Select(item => item.SourceRootId).Distinct().Count() > 1))
            invalidRootIds.UnionWith(group.Select(value => value.SourceRootId));

        foreach (StreamingTrackPlan track in tracks)
        {
            MidoraTrackMetadata? metadata = normalized.GetValueOrDefault(track.SourceTrackIndex);
            if (metadata is not null && !invalidRootIds.Contains(metadata.SourceRootId))
            {
                track.AcceptedMetadata = metadata;
                track.AcceptedMetadataOrder = track.MetadataCandidates[0].Order;
            }
            else
            {
                foreach (StreamingMetadataCandidate candidate in track.MetadataCandidates)
                    track.RecordOpaquePort(candidate.SourcePort, candidate.Order);
            }
        }
        return tracks;
    }

    private static StreamingBucket[] BuildStreamingBuckets(
        ushort format,
        IReadOnlyList<StreamingTrackPlan> tracks)
    {
        Dictionary<StreamingBucketKey, StreamingBucket> buckets = [];
        foreach (StreamingTrackPlan track in tracks)
        {
            foreach (((byte port, byte channel), long firstOrder) in track.ChannelRoutes)
                buckets.Add(
                    new(track.SourceTrackIndex, port, channel),
                    new(new(track.SourceTrackIndex, port, channel), track, firstOrder));
        }
        foreach (StreamingTrackPlan track in tracks)
        {
            foreach ((byte port, long firstOrder) in track.OpaquePortFirstOrders)
            {
                StreamingBucket? owner = buckets.Values
                    .Where(value => value.Key.SourceTrackIndex == track.SourceTrackIndex
                        && value.Key.SourcePort == port)
                    .OrderBy(value => value.FirstOrder)
                    .ThenBy(value => value.Key.Channel)
                    .FirstOrDefault();
                if (owner is null)
                {
                    byte channel = track.AcceptedMetadata is
                    { SourcePort: byte metadataPort, SourceChannel: byte metadataChannel }
                        && metadataPort == port
                            ? metadataChannel
                            : buckets.Values
                                .Where(value => value.Key.SourcePort == port)
                                .Select(value => value.Key.Channel)
                                .DefaultIfEmpty((byte)0)
                                .Min();
                    StreamingBucketKey key = new(track.SourceTrackIndex, port, channel);
                    owner = new(key, track, firstOrder);
                    buckets.Add(key, owner);
                }
                owner.OwnedOpaquePorts.Add(port);
                owner.FirstOrder = Math.Min(owner.FirstOrder, firstOrder);
            }
        }
        foreach (StreamingTrackPlan track in tracks)
        {
            if (track.SourceTrackIndex == 0
                && format == 1
                && !buckets.Keys.Any(value => value.SourceTrackIndex == 0))
                continue;
            if (buckets.Keys.Any(value => value.SourceTrackIndex == track.SourceTrackIndex)) continue;
            byte port = track.AcceptedMetadata?.SourcePort ?? track.FinalSourcePort;
            byte channel = track.AcceptedMetadata?.SourceChannel
                ?? buckets.Values.Where(value => value.Key.SourcePort == port)
                    .Select(value => value.Key.Channel)
                    .DefaultIfEmpty((byte)0)
                    .Min();
            StreamingBucketKey key = new(track.SourceTrackIndex, port, channel);
            buckets.Add(key, new(key, track, long.MaxValue));
        }
        return buckets.Values
            .OrderBy(value => value.Key.SourceTrackIndex)
            .ThenBy(value => value.FirstOrder)
            .ThenBy(value => value.Key.SourcePort)
            .ThenBy(value => value.Key.Channel)
            .ToArray();
    }

    private static void CreateStreamingProjectStructure(
        MidoraProject project,
        IReadOnlyList<StreamingBucket> buckets,
        IReadOnlyDictionary<byte, byte> portMap,
        string backingRoot,
        PureMidiContentPackDecodedCache decodedCache,
        ICollection<StreamingImportTarget> targets,
        ICollection<MidiProjectImportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        Dictionary<(byte Port, byte Channel), MidiChannelRoot> roots = [];
        Dictionary<int, int> bucketCountBySourceTrack = buckets
            .GroupBy(value => value.Key.SourceTrackIndex)
            .ToDictionary(value => value.Key, value => value.Count());
        HashSet<int> fallbackSourceTracks = [];
        int fallbackCount = 0;
        int pureMidiTrackOrdinal = 0;
        foreach (StreamingBucket bucket in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte targetPort = portMap[bucket.Key.SourcePort];
            var route = (targetPort, bucket.Key.Channel);
            if (!roots.TryGetValue(route, out MidiChannelRoot? root))
            {
                root = new(project)
                {
                    Name = bucket.Track.AcceptedMetadata?.RootName
                        ?? $"Port {targetPort + 1} / Channel {bucket.Key.Channel + 1}",
                    RoutingMode = bucket.Track.AcceptedMetadata?.RoutingMode
                        ?? MidiChannelRootRoutingMode.Fixed,
                    FixedZeroBasedPort = targetPort,
                    FixedZeroBasedChannel = bucket.Key.Channel,
                    ChannelMode = bucket.Track.AcceptedMetadata?.ChannelMode
                        ?? (bucket.Key.Channel == 9 ? MidiChannelMode.Percussion : MidiChannelMode.Melodic)
                };
                roots.Add(route, root);
                project.MidiChannelRoots.Add(root);
            }
            bool fallback = string.IsNullOrWhiteSpace(bucket.Track.TrackName);
            string baseName = fallback
                ? $"MIDI Track {bucket.Key.SourceTrackIndex + 1}"
                : bucket.Track.TrackName;
            if (fallback)
            {
                fallbackCount++;
                fallbackSourceTracks.Add(bucket.Key.SourceTrackIndex);
            }
            string name = bucketCountBySourceTrack[bucket.Key.SourceTrackIndex] == 1
                ? baseName
                : $"{baseName} [Port {targetPort + 1}, Channel {bucket.Key.Channel + 1}]";
            PureMidiTrack track = new(project)
            {
                Name = name,
                MidiChannelRootId = root.Id,
                Color = ProjectTrackColorPolicy.ColorForPureMidiTrackOrdinal(
                    pureMidiTrackOrdinal)
            };
            pureMidiTrackOrdinal = checked(pureMidiTrackOrdinal + 1);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(
                ArrangementTrackKind.PureMidiTrack,
                track.Id));
            MidiSegment? segment = null;
            PureMidiContentPackWriter? writer = null;
            if (bucket.Track.EndTick > 0)
            {
                segment = new(project)
                {
                    ProjectStartTick = 0,
                    LengthTicks = bucket.Track.EndTick,
                    ContentOffsetTick = 0
                };
                track.Segments.Add(segment);
                writer = new(
                    Path.Combine(backingRoot, $"mt_{track.Id.Value}.mpk"),
                    cancellationToken);
            }
            StreamingImportTarget target = new(
                bucket,
                targetPort,
                root,
                track,
                segment,
                writer,
                decodedCache);
            bucket.Target = target;
            targets.Add(target);
        }
        if (fallbackCount != 0)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-FALLBACK-TRACK-NAME",
                DiagnosticSeverity.Info,
                $"Assigned deterministic fallback names to {fallbackCount} Pure MIDI Track(s) "
                + $"derived from {fallbackSourceTracks.Count} source MTrk(s) with no usable non-blank Track Name.",
                fallbackSourceTracks.Min()));
        }
    }

    private static void AddStreamingTrackNameDiagnostic(
        IReadOnlyList<StreamingTrackPlan> tracks,
        ICollection<MidiProjectImportDiagnostic> diagnostics)
    {
        StreamingTrackPlan[] decodedWindows31J = tracks
            .Where(value => value.Windows31JTrackNameCount != 0)
            .OrderBy(value => value.SourceTrackIndex)
            .ToArray();
        if (decodedWindows31J.Length != 0)
        {
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-WINDOWS-31J-TRACK-NAME",
                DiagnosticSeverity.Info,
                $"Decoded {decodedWindows31J.Sum(value => value.Windows31JTrackNameCount)} "
                + $"non-UTF-8 Track Name Meta event(s) from {decodedWindows31J.Length} "
                + "source MTrk(s) as Windows-31J (code page 932).",
                decodedWindows31J[0].SourceTrackIndex,
                decodedWindows31J[0].FirstWindows31JTrackNameByteOffset));
        }

        StreamingTrackPlan[] affected = tracks
            .Where(value => value.InvalidTrackNameCount != 0)
            .OrderBy(value => value.SourceTrackIndex)
            .ToArray();
        if (affected.Length == 0) return;
        diagnostics.Add(new(
            "MIDORA-MIDI-IMPORT-INVALID-TRACK-NAME",
            DiagnosticSeverity.Info,
            $"Discarded {affected.Sum(value => value.InvalidTrackNameCount)} Track Name Meta event(s) "
            + $"from {affected.Length} source MTrk(s) because the payload was not strict UTF-8. "
            + "The payload also could not be decoded as Windows-31J. "
            + "A remaining valid name was used when available; otherwise a deterministic fallback name was assigned.",
            affected[0].SourceTrackIndex,
            affected[0].FirstInvalidTrackNameByteOffset));
    }

    private static void ImportStreamingConductor(
        MidoraProject project,
        IReadOnlyList<StreamingTrackPlan> tracks,
        ICollection<MidiProjectImportDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        project.Conductor.Tempos.Clear();
        project.Conductor.TimeSignatures.Clear();
        project.Conductor.KeySignatures.Clear();
        project.Conductor.Markers.Clear();
        Dictionary<long, ImportedTempo> tempos = [];
        Dictionary<long, ImportedTimeSignature> timeSignatures = [];
        Dictionary<long, ImportedKeySignature> keySignatures = [];
        int identical = 0;
        int conflicting = 0;
        HashSet<long> identicalTicks = [];
        HashSet<long> conflictingTicks = [];
        ImportedTempo? firstIdentical = null;
        (ImportedTempo Previous, ImportedTempo Replacement)? firstConflict = null;
        int timeSignatureDuplicateCount = 0;
        int conflictingTimeSignatureDuplicateCount = 0;
        HashSet<long> timeSignatureDuplicateTicks = [];
        ImportedConductorDuplicate? firstTimeSignatureDuplicate = null;
        int keySignatureDuplicateCount = 0;
        int conflictingKeySignatureDuplicateCount = 0;
        HashSet<long> keySignatureDuplicateTicks = [];
        ImportedConductorDuplicate? firstKeySignatureDuplicate = null;
        int windows31JMarkerCount = 0;
        ImportedTextLocation? firstWindows31JMarker = null;
        int invalidMarkerCount = 0;
        ImportedTextLocation? firstInvalidMarker = null;
        foreach (StreamingTrackPlan track in tracks.OrderBy(value => value.SourceTrackIndex))
        {
            foreach (ParsedStandardMidiFileEvent value in track.ConductorEvents.OrderBy(value => value.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> data = value.Data.Span;
                switch (value.Type)
                {
                    case StandardMidiFile.SetTempoMetaType:
                        RequireStreamingLength(track, value, 3, "Set Tempo");
                        int microseconds = data[0] << 16 | data[1] << 8 | data[2];
                        if (microseconds == 0)
                            throw new InvalidDataException(
                                $"SMF MTrk {track.SourceTrackIndex} has a zero Set Tempo value at byte {value.SourceByteOffset}.");
                        ImportedTempo replacement = new(
                            value.Tick,
                            microseconds,
                            60_000_000m / microseconds,
                            track.SourceTrackIndex,
                            value.Order,
                            value.SourceByteOffset);
                        if (tempos.TryGetValue(value.Tick, out ImportedTempo previous))
                        {
                            if (previous.MicrosecondsPerQuarterNote == replacement.MicrosecondsPerQuarterNote)
                            {
                                identical++;
                                identicalTicks.Add(value.Tick);
                                firstIdentical ??= replacement;
                            }
                            else
                            {
                                conflicting++;
                                conflictingTicks.Add(value.Tick);
                                firstConflict ??= (previous, replacement);
                            }
                        }
                        tempos[value.Tick] = replacement;
                        break;
                    case StandardMidiFile.TimeSignatureMetaType:
                        RequireStreamingLength(track, value, 4, "Time Signature");
                        if (data[1] > 30)
                            throw new InvalidDataException(
                                $"SMF MTrk {track.SourceTrackIndex} has an unrepresentable Time Signature denominator at byte {value.SourceByteOffset}.");
                        ImportedTimeSignature importedTimeSignature = new(
                            value.Tick,
                            data[0],
                            checked(1 << data[1]),
                            track.SourceTrackIndex,
                            value.Order,
                            value.SourceByteOffset);
                        if (timeSignatures.TryGetValue(
                            value.Tick,
                            out ImportedTimeSignature previousTimeSignature))
                        {
                            timeSignatureDuplicateCount++;
                            timeSignatureDuplicateTicks.Add(value.Tick);
                            if (previousTimeSignature.Numerator != importedTimeSignature.Numerator
                                || previousTimeSignature.Denominator != importedTimeSignature.Denominator)
                            {
                                conflictingTimeSignatureDuplicateCount++;
                            }
                            firstTimeSignatureDuplicate ??= new(
                                value.Tick,
                                track.SourceTrackIndex,
                                value.SourceByteOffset);
                        }
                        timeSignatures[value.Tick] = importedTimeSignature;
                        break;
                    case StandardMidiFile.KeySignatureMetaType:
                        RequireStreamingLength(track, value, 2, "Key Signature");
                        ImportedKeySignature importedKeySignature = new(
                            value.Tick,
                            unchecked((sbyte)data[0]),
                            data[1] != 0,
                            track.SourceTrackIndex,
                            value.Order,
                            value.SourceByteOffset);
                        if (keySignatures.TryGetValue(
                            value.Tick,
                            out ImportedKeySignature previousKeySignature))
                        {
                            keySignatureDuplicateCount++;
                            keySignatureDuplicateTicks.Add(value.Tick);
                            if (previousKeySignature.SharpsFlats != importedKeySignature.SharpsFlats
                                || previousKeySignature.IsMinor != importedKeySignature.IsMinor)
                            {
                                conflictingKeySignatureDuplicateCount++;
                            }
                            firstKeySignatureDuplicate ??= new(
                                value.Tick,
                                track.SourceTrackIndex,
                                value.SourceByteOffset);
                        }
                        keySignatures[value.Tick] = importedKeySignature;
                        break;
                    case StandardMidiFile.MarkerMetaType:
                        ImportMarker(
                            project,
                            value,
                            track.SourceTrackIndex,
                            ref windows31JMarkerCount,
                            ref firstWindows31JMarker,
                            ref invalidMarkerCount,
                            ref firstInvalidMarker);
                        break;
                }
            }
        }
        foreach (ImportedTempo value in tempos.Values.OrderBy(value => value.Tick))
            project.Conductor.Tempos.Add(new(project, value.Tick, value.BeatsPerMinute));
        foreach (ImportedTimeSignature value in timeSignatures.Values.OrderBy(value => value.Tick))
            project.Conductor.TimeSignatures.Add(new(
                project,
                value.Tick,
                value.Numerator,
                value.Denominator));
        foreach (ImportedKeySignature value in keySignatures.Values.OrderBy(value => value.Tick))
            project.Conductor.KeySignatures.Add(new(
                project,
                value.Tick,
                value.SharpsFlats,
                value.IsMinor));
        AppendMarkerEncodingDiagnostics(
            diagnostics,
            windows31JMarkerCount,
            firstWindows31JMarker,
            invalidMarkerCount,
            firstInvalidMarker);
        if (!tempos.ContainsKey(0))
        {
            project.Conductor.Tempos.Insert(0, new(project, 0, 120m));
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-DEFAULT-TEMPO",
                DiagnosticSeverity.Info,
                "The source had no Set Tempo event at tick 0; Midora added an explicit 120 BPM Tempo state."));
        }
        if (!timeSignatures.ContainsKey(0))
        {
            project.Conductor.TimeSignatures.Insert(0, new(project, 0, 4, 4));
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-DEFAULT-TIME-SIGNATURE",
                DiagnosticSeverity.Info,
                "The source had no Time Signature event at tick 0; Midora added an explicit 4/4 Time Signature state."));
        }
        if (identical != 0)
        {
            ImportedTempo first = firstIdentical!.Value;
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-REDUNDANT-TEMPO",
                DiagnosticSeverity.Info,
                $"Removed {identical} redundant Set Tempo event(s) at {identicalTicks.Count} tick position(s); "
                + "the last event in source MTrk/event order was retained.",
                first.SourceTrackIndex,
                first.SourceByteOffset,
                first.Tick));
        }
        if (conflicting != 0)
        {
            (ImportedTempo previous, ImportedTempo replacement) = firstConflict!.Value;
            diagnostics.Add(new(
                "MIDORA-MIDI-IMPORT-CONFLICTING-TEMPO",
                DiagnosticSeverity.Warning,
                $"Resolved {conflicting} conflicting Set Tempo event(s) at {conflictingTicks.Count} tick position(s) "
                + "by retaining the last event in source MTrk/event order. "
                + $"First conflict at tick {replacement.Tick}: MTrk {previous.SourceTrackIndex} "
                + $"({FormatBpm(previous.BeatsPerMinute)} BPM) was replaced by MTrk {replacement.SourceTrackIndex} "
                + $"({FormatBpm(replacement.BeatsPerMinute)} BPM).",
                replacement.SourceTrackIndex,
                replacement.SourceByteOffset,
                replacement.Tick));
        }
        AppendConductorDuplicateDiagnostic(
            diagnostics,
            "TIME-SIGNATURE",
            "Time Signature",
            timeSignatureDuplicateCount,
            conflictingTimeSignatureDuplicateCount,
            timeSignatureDuplicateTicks.Count,
            firstTimeSignatureDuplicate);
        AppendConductorDuplicateDiagnostic(
            diagnostics,
            "KEY-SIGNATURE",
            "Key Signature",
            keySignatureDuplicateCount,
            conflictingKeySignatureDuplicateCount,
            keySignatureDuplicateTicks.Count,
            firstKeySignatureDuplicate);
    }

    private static void RequireStreamingLength(
        StreamingTrackPlan track,
        ParsedStandardMidiFileEvent value,
        int expected,
        string kind)
    {
        if (value.Data.Length != expected)
            throw new InvalidDataException(
                $"SMF MTrk {track.SourceTrackIndex} {kind} payload has length {value.Data.Length}, expected {expected}, at byte {value.SourceByteOffset}.");
    }

    private sealed class StreamingFirstPassVisitor(
        IProgress<MidiProjectImportProgress>? progress) : IStandardMidiFileStreamVisitor
    {
        private const long ReportInterval = 8_192;
        private readonly IProgress<MidiProjectImportProgress>? _progress = progress;
        private StreamingTrackPlan? _current;
        private byte _port;
        private long _processedEventCount;
        private long _totalSourceBytes;
        private long _lastReportedEventCount;

        public StreamingTrackPlan[] Tracks { get; private set; } = [];

        public void OnHeader(StandardMidiFileStreamHeader header)
        {
            _totalSourceBytes = header.FileByteCount;
            Tracks = Enumerable.Range(0, header.TrackCount)
                .Select(value => new StreamingTrackPlan(value))
                .ToArray();
        }

        public void OnTrackStart(int sourceTrackIndex, long chunkByteCount)
        {
            _current = Tracks[sourceTrackIndex];
            _port = 0;
        }

        public bool ShouldReadPayload(
            int sourceTrackIndex,
            StandardMidiFileEventKind kind,
            byte type,
            int payloadByteCount) => kind == StandardMidiFileEventKind.Meta
                && type is StandardMidiFile.TrackNameMetaType
                    or StandardMidiFile.MidiPortMetaType
                    or StandardMidiFile.SetTempoMetaType
                    or StandardMidiFile.TimeSignatureMetaType
                    or StandardMidiFile.KeySignatureMetaType
                    or StandardMidiFile.MarkerMetaType
                    or 0x7f
                || kind == StandardMidiFileEventKind.SystemExclusive && payloadByteCount <= 16;

        public void OnEvent(in StreamedStandardMidiFileEvent value)
        {
            _processedEventCount++;
            if (_progress is not null
                && _processedEventCount - _lastReportedEventCount >= ReportInterval)
            {
                _lastReportedEventCount = _processedEventCount;
                long processedBytes = Math.Clamp(
                    value.SourceByteOffset,
                    0,
                    _totalSourceBytes);
                double sourceFraction = _totalSourceBytes == 0
                    ? 0
                    : processedBytes / (double)_totalSourceBytes;
                _progress.Report(new(
                    MidiProjectImportPhase.ScanningSource,
                    _processedEventCount,
                    0,
                    processedBytes,
                    _totalSourceBytes,
                    sourceFraction * ScanningSourceProgressEnd));
            }
            StreamingTrackPlan track = _current
                ?? throw new InvalidOperationException("No streaming Track is active.");
            int sourceOffset = value.SourceByteOffset > int.MaxValue
                ? int.MaxValue
                : checked((int)value.SourceByteOffset);
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == StandardMidiFile.MidiPortMetaType)
            {
                if (!value.PayloadWasRead || value.DataLength != 1)
                    throw new InvalidDataException(
                        $"SMF MTrk {track.SourceTrackIndex} has an invalid MIDI Port Meta payload at byte {value.SourceByteOffset}.");
                _port = value.Data.Span[0];
                return;
            }
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == StandardMidiFile.TrackNameMetaType)
            {
                if (TryDecodeImportedText(
                        value.Data.Span,
                        out string decoded,
                        out ImportedTextEncoding encoding))
                {
                    if (encoding == ImportedTextEncoding.Windows31J)
                    {
                        track.Windows31JTrackNameCount++;
                        track.FirstWindows31JTrackNameByteOffset ??= sourceOffset;
                    }
                    string normalized = decoded.Trim();
                    if (normalized.Length != 0) track.TrackName = normalized;
                }
                else
                {
                    track.InvalidTrackNameCount++;
                    track.FirstInvalidTrackNameByteOffset ??= sourceOffset;
                }
                return;
            }
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == 0x7f
                && TryParseMidoraMetadata(value.Data.Span, out MidoraTrackMetadata? metadata))
            {
                track.MetadataCandidates.Add(new(metadata!, _port, value.Order));
                return;
            }
            ParsedStandardMidiFileEvent parsed = new(
                value.Tick,
                value.Order,
                value.Kind,
                value.Message,
                value.Type,
                value.Data,
                sourceOffset);
            if (IsConductor(parsed))
            {
                track.ConductorEvents.Add(parsed);
                return;
            }
            if (value.Kind == StandardMidiFileEventKind.ChannelVoice)
            {
                var route = (_port, value.Message.ChannelNumber);
                if (!track.ChannelRoutes.TryGetValue(route, out long firstOrder)
                    || value.Order < firstOrder)
                    track.ChannelRoutes[route] = value.Order;
            }
            else
            {
                if (value.Kind == StandardMidiFileEventKind.SystemExclusive
                    && value.Type == 0xf0
                    && MidiChannelModeSystemExclusive.TryParseF0Payload(
                        value.Data.Span,
                        out MidiChannelModeSystemExclusive channelMode))
                {
                    var route = (_port, channelMode.TargetChannel);
                    if (!track.ChannelRoutes.TryGetValue(route, out long firstOrder)
                        || value.Order < firstOrder)
                        track.ChannelRoutes[route] = value.Order;
                }
                track.RecordOpaquePort(_port, value.Order);
            }
        }

        public void OnTrackEnd(StandardMidiFileStreamTrackResult result)
        {
            StreamingTrackPlan track = _current
                ?? throw new InvalidOperationException("No streaming Track is active.");
            track.EndTick = result.EndTick;
            track.FinalSourcePort = _port;
            track.EventCount = result.EventCount;
            _current = null;
        }
    }

    private sealed class StreamingSecondPassVisitor : IStandardMidiFileStreamVisitor
    {
        private readonly MidoraProject _project;
        private readonly StreamingTrackPlan[] _tracks;
        private readonly Dictionary<StreamingBucketKey, StreamingImportTarget> _targets;
        private readonly Dictionary<(int Track, byte Port), StreamingImportTarget> _opaqueOwners;
        private readonly ICollection<MidiProjectImportDiagnostic> _diagnostics;
        private readonly CancellationToken _cancellationToken;
        private readonly long _totalEventCount;
        private readonly long _totalSourceBytes;
        private readonly IProgress<MidiProjectImportProgress>? _progress;
        private StreamingTrackPlan? _current;
        private byte _port;
        private long _processedEventCount;
        private long _lastReportedEventCount;

        public StreamingSecondPassVisitor(
            MidoraProject project,
            StreamingTrackPlan[] tracks,
            IEnumerable<StreamingImportTarget> targets,
            ICollection<MidiProjectImportDiagnostic> diagnostics,
            CancellationToken cancellationToken,
            long totalEventCount,
            long totalSourceBytes,
            IProgress<MidiProjectImportProgress>? progress)
        {
            _project = project;
            _tracks = tracks;
            _targets = targets.ToDictionary(value => value.Bucket.Key);
            _opaqueOwners = _targets.Values
                .SelectMany(target => target.Bucket.OwnedOpaquePorts.Select(port => (Port: port, Target: target)))
                .GroupBy(value => (targetTrack: value.Target.Bucket.Key.SourceTrackIndex, value.Port))
                .ToDictionary(
                    value => (value.Key.targetTrack, value.Key.Port),
                    value => value
                        .OrderBy(item => item.Target.Bucket.FirstOrder)
                        .ThenBy(item => item.Target.Bucket.Key.Channel)
                        .First().Target);
            _diagnostics = diagnostics;
            _cancellationToken = cancellationToken;
            _totalEventCount = totalEventCount;
            _totalSourceBytes = totalSourceBytes;
            _progress = progress;
        }

        public void OnHeader(StandardMidiFileStreamHeader header)
        {
        }

        public void OnTrackStart(int sourceTrackIndex, long chunkByteCount)
        {
            _current = _tracks[sourceTrackIndex];
            _port = 0;
        }

        public bool ShouldReadPayload(
            int sourceTrackIndex,
            StandardMidiFileEventKind kind,
            byte type,
            int payloadByteCount) => true;

        public void OnEvent(in StreamedStandardMidiFileEvent value)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _processedEventCount++;
            if (_progress is not null
                && (_processedEventCount - _lastReportedEventCount >= 8_192
                    || _processedEventCount == _totalEventCount))
            {
                _lastReportedEventCount = _processedEventCount;
                double eventFraction = _totalEventCount == 0
                    ? 1
                    : Math.Clamp(_processedEventCount / (double)_totalEventCount, 0, 1);
                _progress.Report(new(
                    MidiProjectImportPhase.ImportingEvents,
                    _processedEventCount,
                    _totalEventCount,
                    Math.Clamp(value.SourceByteOffset, 0, _totalSourceBytes),
                    _totalSourceBytes,
                    ScanningSourceProgressEnd
                        + eventFraction
                        * (ImportingEventsProgressEnd - ScanningSourceProgressEnd)));
            }
            StreamingTrackPlan track = _current
                ?? throw new InvalidOperationException("No streaming Track is active.");
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == StandardMidiFile.MidiPortMetaType)
            {
                if (value.DataLength != 1)
                    throw new InvalidDataException(
                        $"SMF MTrk {track.SourceTrackIndex} has an invalid MIDI Port Meta payload at byte {value.SourceByteOffset}.");
                _port = value.Data.Span[0];
                return;
            }
            if (value.Kind == StandardMidiFileEventKind.Meta
                && (value.Type == StandardMidiFile.TrackNameMetaType || IsStreamingConductorType(value.Type)))
                return;
            if (value.Kind == StandardMidiFileEventKind.Meta
                && value.Type == 0x7f
                && track.AcceptedMetadataOrder == value.Order)
                return;
            if (value.Kind == StandardMidiFileEventKind.ChannelVoice)
            {
                StreamingBucketKey key = new(
                    track.SourceTrackIndex,
                    _port,
                    value.Message.ChannelNumber);
                if (!_targets.TryGetValue(key, out StreamingImportTarget? target))
                    throw new InvalidDataException("The streaming MIDI import route plan changed between passes.");
                target.AcceptChannelEvent(_project, value);
                return;
            }
            if (ShouldStripChannel10Initialization(track, value)) return;
            StreamingImportTarget? owner = null;
            if (value.Kind == StandardMidiFileEventKind.SystemExclusive
                && value.Type == 0xf0
                && MidiChannelModeSystemExclusive.TryParseF0Payload(
                    value.Data.Span,
                    out MidiChannelModeSystemExclusive channelMode))
            {
                _targets.TryGetValue(
                    new StreamingBucketKey(track.SourceTrackIndex, _port, channelMode.TargetChannel),
                    out owner);
            }
            owner ??= _opaqueOwners.GetValueOrDefault((track.SourceTrackIndex, _port));
            if (owner is null)
                throw new InvalidDataException("The streaming MIDI opaque-event ownership plan changed between passes.");
            owner.AcceptOpaqueEvent(_project, value);
        }

        public void OnTrackEnd(StandardMidiFileStreamTrackResult result)
        {
            foreach (StreamingImportTarget target in _targets.Values
                .Where(value => value.Bucket.Key.SourceTrackIndex == result.SourceTrackIndex))
            {
                target.EndSourceTrack(_project, _diagnostics);
            }
            _current = null;
        }

        public void Complete()
        {
            foreach (StreamingImportTarget target in _targets.Values)
            {
                target.Publish(_project);
            }
        }

        private static bool IsStreamingConductorType(byte type) => type is
            StandardMidiFile.SetTempoMetaType
                or StandardMidiFile.TimeSignatureMetaType
                or StandardMidiFile.KeySignatureMetaType
                or StandardMidiFile.MarkerMetaType;

        private static bool ShouldStripChannel10Initialization(
            StreamingTrackPlan track,
            in StreamedStandardMidiFileEvent value) => track.AcceptedMetadata is
            {
                ChannelMode: MidiChannelMode.Melodic,
                SourceChannel: 9
            }
            && value.Tick == 0
            && value.Kind == StandardMidiFileEventKind.SystemExclusive
            && value.Type == 0xf0
            && (value.Data.Span.SequenceEqual(RolandGsChannel10NormalPart)
                || value.Data.Span.SequenceEqual(YamahaXgChannel10NormalPart));
    }

    private sealed class StreamingImportTarget
    {
        private readonly Queue<PendingNoteOn>?[] _activeNotes = new Queue<PendingNoteOn>?[128];
        private bool _sourceTrackEnded;
        private bool _published;
        private bool _hasUnpaired;
        private StreamedStandardMidiFileEvent? _firstUnpaired;

        public StreamingImportTarget(
            StreamingBucket bucket,
            byte targetPort,
            MidiChannelRoot root,
            PureMidiTrack track,
            MidiSegment? segment,
            PureMidiContentPackWriter? writer,
            PureMidiContentPackDecodedCache decodedCache)
        {
            Bucket = bucket;
            TargetPort = targetPort;
            Root = root;
            Track = track;
            Segment = segment;
            Writer = writer;
            DecodedCache = decodedCache;
        }

        public StreamingBucket Bucket { get; }
        public byte TargetPort { get; }
        public MidiChannelRoot Root { get; }
        public PureMidiTrack Track { get; }
        public MidiSegment? Segment { get; }
        public PureMidiContentPackWriter? Writer { get; private set; }
        private PureMidiContentPackDecodedCache DecodedCache { get; }
        public PureMidiContentPack? ContentPack { get; private set; }
        public long ImportedNoteCount { get; private set; }
        public long ImportedChannelEventCount { get; private set; }
        public long ImportedOpaqueEventCount { get; private set; }

        public void AcceptChannelEvent(
            MidoraProject project,
            in StreamedStandardMidiFileEvent value)
        {
            if (Writer is null || Segment is null) return;
            MidiMessage message = value.Message;
            if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0)
            {
                Queue<PendingNoteOn> queue = _activeNotes[message.Byte1] ??= new();
                queue.Enqueue(new(value.Tick, value.Order, message, value.SourceByteOffset));
                return;
            }
            if (message.MessageType == MidiMessageType.NoteOff
                || message.MessageType == MidiMessageType.NoteOn && message.Byte2 == 0)
            {
                Queue<PendingNoteOn>? queue = _activeNotes[message.Byte1];
                if (queue is null || queue.Count == 0)
                {
                    MarkUnpaired(value);
                    WriteRaw(project, value.Tick, value.Order, message);
                    return;
                }
                PendingNoteOn start = queue.Dequeue();
                if (value.Tick <= start.Tick)
                {
                    MarkUnpaired(value);
                    WriteRaw(project, start.Tick, start.Order, start.Message);
                    WriteRaw(project, value.Tick, value.Order, message);
                    return;
                }
                Writer.AddNote(Segment.Id, new(
                    project.AllocateStableId(),
                    start.Tick,
                    value.Tick - start.Tick,
                    start.Message.Byte1,
                    start.Message.Byte2,
                    message.MessageType == MidiMessageType.NoteOff ? message.Byte2 : 0,
                    start.Order,
                    value.Order));
                ImportedNoteCount++;
                return;
            }
            WriteRaw(project, value.Tick, value.Order, message);
        }

        public void AcceptOpaqueEvent(
            MidoraProject project,
            in StreamedStandardMidiFileEvent value)
        {
            if (Writer is null || Segment is null) return;
            if (value.DataLength > PureMidiContentPackWriter.MaximumDecodedPageByteCount - 33)
                throw new InvalidDataException(
                    $"SMF MTrk {value.SourceTrackIndex} contains one opaque event whose {value.DataLength}-byte payload exceeds the bounded Pure MIDI page size.");
            Writer.AddOpaqueEvent(Segment.Id, new(
                project.AllocateStableId(),
                value.Tick,
                value.Kind == StandardMidiFileEventKind.Meta
                    ? OpaqueMidiEventKind.Meta
                    : value.Type == 0xf7
                        ? OpaqueMidiEventKind.SystemExclusiveContinuation
                        : OpaqueMidiEventKind.SystemExclusive,
                value.Kind == StandardMidiFileEventKind.Meta ? value.Type : (byte)0,
                value.Data,
                value.Order));
            ImportedOpaqueEventCount++;
        }

        public void EndSourceTrack(
            MidoraProject project,
            ICollection<MidiProjectImportDiagnostic> diagnostics)
        {
            if (_sourceTrackEnded) return;
            _sourceTrackEnded = true;
            List<PendingNoteOn> unmatched = _activeNotes
                .Where(value => value is not null && value.Count != 0)
                .SelectMany(value => value!)
                .OrderBy(value => value.Order)
                .ToList();
            foreach (PendingNoteOn value in unmatched)
            {
                _hasUnpaired = true;
                _firstUnpaired ??= new(
                    Bucket.Key.SourceTrackIndex,
                    value.Tick,
                    value.Order,
                    StandardMidiFileEventKind.ChannelVoice,
                    value.Message,
                    0,
                    default,
                    0,
                    value.SourceByteOffset,
                    false);
                WriteRaw(project, value.Tick, value.Order, value.Message);
            }
            if (_hasUnpaired)
            {
                StreamedStandardMidiFileEvent location = _firstUnpaired
                    ?? throw new InvalidOperationException("An unmatched Note diagnostic has no source event.");
                diagnostics.Add(new(
                    "MIDORA-MIDI-IMPORT-UNPAIRED-NOTE",
                    DiagnosticSeverity.Warning,
                    $"The source Track contains unmatched Note messages at tick {location.Tick}, "
                    + $"Port {TargetPort + 1}, Channel {Bucket.Key.Channel + 1}; "
                    + "they were preserved as raw direct MIDI events.",
                    Bucket.Key.SourceTrackIndex,
                    location.SourceByteOffset > int.MaxValue
                        ? int.MaxValue
                        : checked((int)location.SourceByteOffset),
                    location.Tick,
                    TargetPort,
                    Bucket.Key.Channel));
            }
            // A target belongs to exactly one source MTrk. Publish it as soon as
            // that MTrk ends so later tracks cannot retain one partially filled
            // 4 MiB page builder and open writer per earlier Track/Channel bucket.
            Publish(project);
        }

        public void Publish(MidoraProject project)
        {
            if (_published || Writer is null || Segment is null) return;
            ContentPack = Writer.Complete(DecodedCache);
            Writer.Dispose();
            Writer = null;
            Segment.AttachPagedContent(ContentPack.GetSegmentSource(Segment.Id));
            project.RegisterRuntimeResource(ContentPack);
            _published = true;
        }

        public void DisposePendingWriter()
        {
            PureMidiContentPackWriter? writer = Writer;
            Writer = null;
            writer?.Dispose();
        }

        private void WriteRaw(
            MidoraProject project,
            long tick,
            long order,
            MidiMessage message)
        {
            if (Writer is null || Segment is null) return;
            DirectMidiChannelEventKind kind = message.MessageType switch
            {
                MidiMessageType.NoteOff => DirectMidiChannelEventKind.NoteOff,
                MidiMessageType.NoteOn when message.Byte2 == 0 => DirectMidiChannelEventKind.NoteOff,
                MidiMessageType.NoteOn => DirectMidiChannelEventKind.NoteOn,
                MidiMessageType.PolyphonicKeyPressure => DirectMidiChannelEventKind.PolyphonicKeyPressure,
                MidiMessageType.ControlChange => DirectMidiChannelEventKind.ControlChange,
                MidiMessageType.ProgramChange => DirectMidiChannelEventKind.ProgramChange,
                MidiMessageType.ChannelPressure => DirectMidiChannelEventKind.ChannelPressure,
                MidiMessageType.PitchWheelChange => DirectMidiChannelEventKind.PitchBend,
                _ => throw new InvalidDataException($"Unsupported MIDI channel event type {message.MessageType}.")
            };
            Writer.AddChannelEvent(Segment.Id, new(
                project.AllocateStableId(),
                tick,
                kind,
                message.Byte1,
                kind == DirectMidiChannelEventKind.NoteOff
                    && message.MessageType == MidiMessageType.NoteOn
                        ? 0
                        : message.Byte2,
                order));
            ImportedChannelEventCount++;
        }

        private void MarkUnpaired(in StreamedStandardMidiFileEvent value)
        {
            _hasUnpaired = true;
            _firstUnpaired ??= value;
        }
    }

    private sealed class StreamingTrackPlan(int sourceTrackIndex)
    {
        public int SourceTrackIndex { get; } = sourceTrackIndex;
        public long EndTick { get; set; }
        public long EventCount { get; set; }
        public string TrackName { get; set; } = string.Empty;
        public byte FinalSourcePort { get; set; }
        public Dictionary<(byte Port, byte Channel), long> ChannelRoutes { get; } = [];
        public Dictionary<byte, long> OpaquePortFirstOrders { get; } = [];
        public List<ParsedStandardMidiFileEvent> ConductorEvents { get; } = [];
        public List<StreamingMetadataCandidate> MetadataCandidates { get; } = [];
        public MidoraTrackMetadata? AcceptedMetadata { get; set; }
        public long? AcceptedMetadataOrder { get; set; }
        public int Windows31JTrackNameCount { get; set; }
        public int? FirstWindows31JTrackNameByteOffset { get; set; }
        public int InvalidTrackNameCount { get; set; }
        public int? FirstInvalidTrackNameByteOffset { get; set; }

        public void RecordOpaquePort(byte port, long order)
        {
            if (!OpaquePortFirstOrders.TryGetValue(port, out long first) || order < first)
                OpaquePortFirstOrders[port] = order;
        }
    }

    private readonly record struct StreamingMetadataCandidate(
        MidoraTrackMetadata Metadata,
        byte SourcePort,
        long Order);

    private readonly record struct StreamingBucketKey(
        int SourceTrackIndex,
        byte SourcePort,
        byte Channel);

    private sealed class StreamingBucket(
        StreamingBucketKey key,
        StreamingTrackPlan track,
        long firstOrder)
    {
        public StreamingBucketKey Key { get; } = key;
        public StreamingTrackPlan Track { get; } = track;
        public long FirstOrder { get; set; } = firstOrder;
        public HashSet<byte> OwnedOpaquePorts { get; } = [];
        public StreamingImportTarget? Target { get; set; }
    }

    private readonly record struct PendingNoteOn(
        long Tick,
        long Order,
        MidiMessage Message,
        long SourceByteOffset);
}
