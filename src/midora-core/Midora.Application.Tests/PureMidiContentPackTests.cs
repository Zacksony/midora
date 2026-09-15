using System.Buffers.Binary;
using System.Collections.Immutable;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PureMidiContentPackTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void EncoderSchedulerRemainsOpenUntilAllPendingResultsAreObserved(
        bool disposeWithoutComplete, bool faultEncoder, bool cancel)
    {
        string directory = System.IO.Path.Combine(
            AppContext.BaseDirectory, ".tmp", "encoder-lifecycle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "lifecycle.mpk");
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim releaseEncoder = new(false);
        using MidoraProject project = new(192);
        MidiSegment segment = new(project);
        int waitCount = 0;
        bool completedBeforeWait = false;
        PureMidiContentPackWriter writer = new(
            path, cancellation.Token, encoderConcurrency: 1,
            encodeBufferBudget: 64L * 1024 * 1024,
            encodePageTestHook: () =>
            {
                if (!releaseEncoder.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("The test did not enter the pending-page drain.");
                if (faultEncoder) throw new IOException("Injected encoder fault.");
            },
            beforePageWaitTestHook: schedulingCompleted =>
            {
                waitCount++;
                completedBeforeWait |= schedulingCompleted;
                // Force task completion to race with the ensuing synchronous
                // wait, without depending on machine timing to check ordering.
                releaseEncoder.Set();
            });
        try
        {
            int notes = disposeWithoutComplete
                ? PureMidiContentPackWriter.MaximumEndpointPageRecordCount + 1
                : 1;
            for (int index = 0; index < notes; index++)
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(), index, 12, index % 128,
                    100, 0, index * 2L, index * 2L + 1));
            }
            if (cancel) cancellation.Cancel();
            if (disposeWithoutComplete)
            {
                if (faultEncoder)
                {
                    InvalidDataException failure = Assert.Throws<InvalidDataException>(writer.Dispose);
                    Assert.IsType<IOException>(failure.InnerException);
                }
                else writer.Dispose();
                Assert.False(File.Exists(path));
            }
            else
            {
                using PureMidiContentPack pack = writer.Complete();
                Assert.Equal(1, pack.GetSegmentSource(segment.Id).NoteCount);
                writer.Dispose();
                Assert.True(File.Exists(path));
            }
            Assert.True(waitCount > 0);
            Assert.False(completedBeforeWait);
        }
        finally
        {
            releaseEncoder.Set();
            writer.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NoteOrdinalExclusionsRemainExactAcrossEndpointPageRuns()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "ordinal-exclusions.mpk");
        try
        {
            using MidoraProject project = new(192);
            MidiSegment segment = new(project);
            const int sourcePageSize = PureMidiContentPackWriter.MaximumPageRecordCount;
            const int endpointPageSize = PureMidiContentPackWriter.MaximumEndpointPageRecordCount;
            DirectMidiNoteValue[] notes = new DirectMidiNoteValue[sourcePageSize + 1];
            using (PureMidiContentPackWriter writer = new(path))
            {
                for (int ordinal = 0; ordinal < notes.Length; ordinal++)
                {
                    // Keep the excluded source page and the remaining Note in
                    // disjoint overview columns, while reversing tick order inside
                    // each endpoint page. Endpoint serialization sorts each page,
                    // so local endpoint indices no longer equal source ordinals even
                    // though each page still owns one contiguous ordinal run.
                    int withinEndpointPage = ordinal % endpointPageSize;
                    long startTick = ordinal < sourcePageSize
                        ? 150_000L + endpointPageSize - withinEndpointPage
                        : 10_000L;
                    DirectMidiNoteValue note = new(
                        project.AllocateStableId(),
                        startTick,
                        24,
                        ordinal % 128,
                        100,
                        0,
                        ordinal * 2L,
                        ordinal * 2L + 1);
                    notes[ordinal] = note;
                    writer.AddNote(segment.Id, note);
                }

                using PureMidiContentPack pack = writer.Complete();
                IPureMidiSegmentContentSource source = pack.GetSegmentSource(segment.Id);
                IPureMidiContentOverviewSource overview =
                    Assert.IsAssignableFrom<IPureMidiContentOverviewSource>(source);
                IPureMidiNoteExclusionAwareSource exclusionAware =
                    Assert.IsAssignableFrom<IPureMidiNoteExclusionAwareSource>(source);
                ImmutableDictionary<MidoraId, int> excluded = notes[..sourcePageSize]
                    .ToImmutableDictionary(static note => note.Id, static _ => 0);
                Dictionary<MidoraId, int> ordinals = notes[..sourcePageSize]
                    .Select((note, ordinal) => (note.Id, ordinal))
                    .ToDictionary(static value => value.Id, static value => value.ordinal);
                PureMidiSourceExclusionSet<int> exclusions = new(
                    ImmutableHashSet<MidoraId>.Empty,
                    excluded,
                    ordinals);

                byte[] columns = new byte[20];
                Assert.True(overview.TryAccumulateNoteStartColumns(
                    200_000,
                    columns,
                    exclusions));
                Assert.Equal(0, pack.PageCacheMissCount);
                Assert.Contains((byte)1, columns[..3]);
                Assert.DoesNotContain((byte)1, columns[14..]);

                DirectMidiNoteValue[] remaining = exclusionAware.QueryNotesExcluding(
                        0,
                        200_000,
                        0,
                        127,
                        exclusions)
                    .ToArray();
                Assert.Single(remaining);
                Assert.Equal(
                    notes[sourcePageSize..].Select(static note => note.Id).Order(),
                    remaining.Select(static note => note.Id).Order());
                Assert.Equal(1, pack.PageCacheMissCount);

                // An unresolved exclusion ordinal must disable page-level skipping,
                // then fall back to exact stable-ID filtering rather than leaking it.
                Dictionary<MidoraId, int> incompleteOrdinals = new(ordinals);
                incompleteOrdinals.Remove(notes[123].Id);
                PureMidiSourceExclusionSet<int> incomplete = new(
                    ImmutableHashSet<MidoraId>.Empty,
                    excluded,
                    incompleteOrdinals);
                Assert.True(incomplete.HasUnknownOrdinals);
                Assert.False(incomplete.ContainsAllOrdinals(0, sourcePageSize));
                Assert.Equal(
                    notes[sourcePageSize..].Select(static note => note.Id).Order(),
                    exclusionAware.QueryNotesExcluding(
                            0,
                            200_000,
                            0,
                            127,
                            incomplete)
                        .Select(static note => note.Id)
                        .Order());
                Assert.Equal(2, pack.PageCacheMissCount);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BackgroundEncoderFaultIsObservedAndDeletesTheUnpublishedPack()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "faulted.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            PureMidiContentPackWriter writer = new(
                path,
                CancellationToken.None,
                encoderConcurrency: 1,
                encodeBufferBudget: 64L * 1024 * 1024,
                encodePageTestHook: static () =>
                    throw new IOException("Injected background encoder failure."));
            for (int index = 0; index < 16_385; index++)
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(),
                    index,
                    12,
                    index % 128,
                    100,
                    0,
                    index * 2L,
                    index * 2L + 1));
            }

            InvalidDataException failure = Assert.Throws<InvalidDataException>(writer.Dispose);
            Assert.IsType<IOException>(failure.InnerException);
            writer.Dispose();
        }
        finally
        {
            Assert.False(File.Exists(path));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BoundedParallelEncodingIsByteDeterministicAcrossConcurrencyLevels()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string serialPath = System.IO.Path.Combine(directory, "serial.mpk");
        string parallelPath = System.IO.Path.Combine(directory, "parallel.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            DirectMidiNoteValue[] notes = Enumerable.Range(0, 100_000)
                .Select(index => new DirectMidiNoteValue(
                    project.AllocateStableId(),
                    index * 3L,
                    48 + index % 97,
                    index % 128,
                    1 + index % 127,
                    index % 128,
                    index * 2L,
                    index * 2L + 1))
                .ToArray();

            Write(serialPath, encoderConcurrency: 1);
            Write(parallelPath, encoderConcurrency: 4);

            Assert.Equal(File.ReadAllBytes(serialPath), File.ReadAllBytes(parallelPath));

            void Write(string path, int encoderConcurrency)
            {
                using PureMidiContentPackWriter writer = new(
                    path,
                    CancellationToken.None,
                    encoderConcurrency,
                    64L * 1024 * 1024);
                foreach (DirectMidiNoteValue note in notes) writer.AddNote(segment.Id, note);
                using PureMidiContentPack completed = writer.Complete();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CancelledParallelEncodingDeletesTheUnpublishedPack()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "cancelled.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            using CancellationTokenSource cancellation = new();
            using PureMidiContentPackWriter writer = new(path, cancellation.Token);
            for (int index = 0; index < 70_000; index++)
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(),
                    index,
                    12,
                    index % 128,
                    100,
                    0,
                    index * 2L,
                    index * 2L + 1));
            }
            cancellation.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() => writer.Complete());
        }
        finally
        {
            Assert.False(File.Exists(path));
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlaybackEndpointPagesAreGloballyOrderedAcrossLocalPageRuns()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "endpoints.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            const int noteCount = 20_000;
            using (PureMidiContentPackWriter writer = new(path))
            {
                for (int index = noteCount - 1; index >= 0; index--)
                {
                    writer.AddNote(segment.Id, new(
                        project.AllocateStableId(),
                        index,
                        100 + index % 7,
                        index % 128,
                        100,
                        0,
                        index * 2L,
                        index * 2L + 1));
                }
                writer.AddChannelEvent(segment.Id, new(
                    project.AllocateStableId(),
                    20,
                    DirectMidiChannelEventKind.ControlChange,
                    11,
                    80,
                    2));
                writer.AddChannelEvent(segment.Id, new(
                    project.AllocateStableId(),
                    10,
                    DirectMidiChannelEventKind.ProgramChange,
                    4,
                    0,
                    1));
                using PureMidiContentPack pack = writer.Complete();
                // One 20,000-record source Note page, two pages per Note endpoint
                // index, and one source/endpoint page for Channel events.
                Assert.Equal(7, pack.PageCount);
                IPureMidiPlaybackEndpointSource endpoints =
                    Assert.IsAssignableFrom<IPureMidiPlaybackEndpointSource>(
                        pack.GetSegmentSource(segment.Id));

                DirectMidiNoteValue[] active = endpoints
                    .QueryActiveNotes(15_050)
                    .ToArray();
                Assert.NotEmpty(active);
                Assert.All(
                    active,
                    value => Assert.True(
                        value.StartTick < 15_050
                        && value.StartTick + value.LengthTicks > 15_050));
                Assert.Equal(1, pack.PageCacheMissCount);

                DirectMidiNoteValue[] starts = endpoints
                    .QueryNoteStarts(15_000, 15_100)
                    .ToArray();
                Assert.Equal(100, starts.Length);
                Assert.Equal(
                    Enumerable.Range(15_000, 100).Select(value => (long)value),
                    starts.Select(value => value.StartTick));

                DirectMidiNoteValue[] ends = endpoints
                    .QueryNoteEnds(15_100, 15_220)
                    .ToArray();
                Assert.NotEmpty(ends);
                Assert.True(ends
                    .Select(value => value.StartTick + value.LengthTicks)
                    .SequenceEqual(ends
                        .Select(value => value.StartTick + value.LengthTicks)
                        .Order()));
                Assert.Equal(
                    [10L, 20L],
                    endpoints.QueryOrderedChannelEvents(0, 30)
                        .Select(value => value.Tick)
                        .ToArray());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreviousDevelopmentPackVersionIsRejected()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "old-version.mpk");
        try
        {
            using (PureMidiContentPackWriter writer = new(path))
            {
                using PureMidiContentPack completed = writer.Complete();
            }
            byte[] bytes = File.ReadAllBytes(path);
            bytes[6] = (byte)'2';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 2);
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => PureMidiContentPack.Open(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RoundTrip_QueryAndCopyOnWriteRemainBoundedByPages()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "track.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project)
            {
                ProjectStartTick = 0,
                LengthTicks = 2_000,
                ContentOffsetTick = 0
            };
            MidiChannelRoot root = new(project)
            {
                Name = "Root",
                RoutingMode = MidiChannelRootRoutingMode.Auto,
                ChannelMode = MidiChannelMode.Melodic
            };
            PureMidiTrack track = new(project)
            {
                Name = "Track",
                MidiChannelRootId = root.Id
            };
            track.Segments.Add(segment);
            project.MidiChannelRoots.Add(root);
            project.PureMidiTracks.Add(track);
            project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
            MidoraId firstNoteId = project.AllocateStableId();
            MidoraId secondNoteId = project.AllocateStableId();
            MidoraId channelEventId = project.AllocateStableId();
            MidoraId opaqueEventId = project.AllocateStableId();
            using (PureMidiContentPackWriter writer = new(path))
            {
                writer.AddNote(segment.Id, new(
                    firstNoteId, 10, 100, 60, 100, 12, 1, 2));
                writer.AddNote(segment.Id, new(
                    secondNoteId, 1_000, 100, 72, 90, 0, 3, 4));
                writer.AddChannelEvent(segment.Id, new(
                    channelEventId, 20, DirectMidiChannelEventKind.ControlChange, 11, 127, 5));
                writer.AddOpaqueEvent(segment.Id, new(
                    opaqueEventId, 30, OpaqueMidiEventKind.Meta, 6, new byte[] { 1, 2, 3 }, 6));
                using PureMidiContentPack pack = writer.Complete();
                IPureMidiSegmentContentSource contentSource = pack.GetSegmentSource(segment.Id);
                Assert.Equal(
                    1_100,
                    Assert.IsAssignableFrom<IPureMidiContentBoundsSource>(contentSource)
                        .MaximumNoteEndTick);
                IPureMidiContentOverviewSource overviewSource =
                    Assert.IsAssignableFrom<IPureMidiContentOverviewSource>(contentSource);
                long missesBeforeOverview = pack.PageCacheMissCount;
                Assert.Equal(
                    [(10L, 1_000L, 2)],
                    overviewSource.GetNoteRangeSummaries()
                        .Select(value => (value.MinimumTick, value.MaximumTick, value.RecordCount)));
                Assert.Equal(
                    [(20L, 20L, 1)],
                    overviewSource.GetChannelEventRangeSummaries()
                        .Select(value => (value.MinimumTick, value.MaximumTick, value.RecordCount)));
                Assert.Equal(
                    [(30L, 30L, 1)],
                    overviewSource.GetOpaqueEventRangeSummaries()
                        .Select(value => (value.MinimumTick, value.MaximumTick, value.RecordCount)));
                Assert.Equal(missesBeforeOverview, pack.PageCacheMissCount);
                IPureMidiContentRangeFingerprintSource rangeFingerprints =
                    Assert.IsAssignableFrom<IPureMidiContentRangeFingerprintSource>(contentSource);
                long missesBeforeFingerprints = pack.PageCacheMissCount;
                _ = rangeFingerprints.GetNoteRangeFingerprint(0, 200, 0, 127);
                _ = rangeFingerprints.GetChannelEventRangeFingerprint(0, 200);
                _ = rangeFingerprints.GetOpaqueEventRangeFingerprint(0, 200);
                Assert.Equal(missesBeforeFingerprints, pack.PageCacheMissCount);
                segment.AttachPagedContent(contentSource);

                Assert.Equal(2, segment.Notes.Count);
                Assert.Equal(firstNoteId, segment.Notes[0].Id);
                Assert.Equal(secondNoteId, Assert.Single(segment.Notes.Query(900, 1_200)).Id);
                Assert.Equal(channelEventId, Assert.Single(segment.ChannelEvents.Query(0, 100)).Id);
                Assert.Equal([1, 2, 3], Assert.Single(segment.OpaqueEvents.Query(0, 100)).Payload);

                DirectMidiNote changed = segment.Notes[0];
                changed.Key = 65;
                Assert.Equal(65, segment.Notes[0].Key);
                Assert.Equal(65, Assert.Single(segment.Notes.Query(0, 200)).Key);

                Assert.InRange(pack.DecodedCacheByteCount, 1, pack.DecodedCacheByteLimit);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExactOverviewColumnsNeverInferOccupancyAcrossPageRanges()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "overview.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            MidoraId firstNoteId = project.AllocateStableId();
            using PureMidiContentPackWriter writer = new(path);
            writer.AddNote(segment.Id, new(
                firstNoteId, 100, 20, 60, 100, 0, 0, 1));
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 800, 20, 64, 100, 0, 2, 3));
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 200,
                DirectMidiChannelEventKind.ControlChange, 1, 64, 4));
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 300,
                DirectMidiChannelEventKind.NoteOn, 67, 100, 5));
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 400,
                DirectMidiChannelEventKind.NoteOff, 67, 0, 6));
            writer.AddChannelEvent(segment.Id, new(
                project.AllocateStableId(), 700,
                DirectMidiChannelEventKind.ProgramChange, 4, 0, 7));
            writer.AddOpaqueEvent(segment.Id, new(
                project.AllocateStableId(), 500,
                OpaqueMidiEventKind.Meta, 6, new byte[] { 1 }, 8));
            writer.AddOpaqueEvent(segment.Id, new(
                project.AllocateStableId(), 900,
                OpaqueMidiEventKind.SystemExclusive, 0, new byte[] { 0x7d }, 9));

            using PureMidiContentPack pack = writer.Complete();
            IPureMidiContentOverviewSource source =
                Assert.IsAssignableFrom<IPureMidiContentOverviewSource>(
                    pack.GetSegmentSource(segment.Id));
            byte[] notes = new byte[10];
            byte[] events = new byte[10];

            Assert.True(source.TryAccumulateNoteStartColumns(
                1_000,
                notes,
                excludedIds: null));
            Assert.True(source.TryAccumulateChannelEventColumns(
                1_000,
                notes,
                events,
                excludedIds: null));
            Assert.True(source.TryAccumulateOpaqueEventColumns(
                1_000,
                events,
                excludedIds: null));

            Assert.Equal([1, 3, 8], Occupied(notes));
            Assert.Equal([2, 5, 7, 9], Occupied(events));

            Array.Clear(notes);
            Assert.True(source.TryAccumulateNoteStartColumns(
                1_000,
                notes,
                new HashSet<MidoraId> { firstNoteId }));
            Assert.Equal([8], Occupied(notes));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        static int[] Occupied(byte[] columns) => columns
            .Select((value, index) => (value, index))
            .Where(static item => item.value != 0)
            .Select(static item => item.index)
            .ToArray();
    }

    [Fact]
    public void PagedNoteRasterColumnsPreserveDistinctBoundaries()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "raster-starts.mpk");
        try
        {
            using MidoraProject project = new(192);
            MidiSegment segment = new(project) { LengthTicks = 128 };
            using PureMidiContentPackWriter writer = new(path);
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 8, 32, 60, 100, 0, 0, 1));
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 40, 32, 60, 100, 0, 2, 3));
            using PureMidiContentPack pack = writer.Complete();
            IPureMidiContentOverviewSource source =
                Assert.IsAssignableFrom<IPureMidiContentOverviewSource>(
                    pack.GetSegmentSource(segment.Id));
            var projection = new TimelineRasterColumnProjection(
                0,
                128,
                0,
                0,
                0.125,
                16);
            TimelineRasterColumnSummary[] columns = new TimelineRasterColumnSummary[16];

            Assert.True(source.TryAccumulateNoteRasterColumns(
                projection,
                60,
                60,
                columns,
                excludedIds: null,
                out int sourceWorkCount));

            const ulong noteMask = 1UL << 60;
            Assert.Equal(noteMask, columns[1].StartLaneMaskLow & noteMask);
            Assert.Equal(noteMask, columns[5].StartLaneMaskLow & noteMask);
            Assert.Equal(noteMask, columns[4].EndLaneMaskLow & noteMask);
            Assert.Equal(noteMask, columns[8].EndLaneMaskLow & noteMask);
            Assert.InRange(sourceWorkCount, 1, 2);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExactOverviewKeepsSingleDeviceColumnPagesOnTheDirectoryFastPath()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "overview-fast-path.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            using PureMidiContentPackWriter writer = new(path);
            for (int index = 0; index < 20_000; index++)
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(),
                    100 + index % 2,
                    1,
                    index % 128,
                    100,
                    0,
                    index * 2L,
                    index * 2L + 1));
            }

            using PureMidiContentPack pack = writer.Complete();
            IPureMidiContentOverviewSource source =
                Assert.IsAssignableFrom<IPureMidiContentOverviewSource>(
                    pack.GetSegmentSource(segment.Id));
            byte[] notes = new byte[10];

            Assert.True(source.TryAccumulateNoteStartColumns(
                1_000,
                notes,
                excludedIds: null));

            Assert.Equal(1, notes[1]);
            Assert.Equal(0, pack.PageCacheMissCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CorruptedDecodedPageIsRejectedWhenRead()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "track.mpk");
        try
        {
            MidoraProject project = new(192);
            MidiSegment segment = new(project);
            using (PureMidiContentPackWriter writer = new(path))
            {
                writer.AddNote(segment.Id, new(
                    project.AllocateStableId(), 10, 20, 60, 100, 0, 1, 2));
                using PureMidiContentPack completed = writer.Complete();
            }
            byte[] bytes = File.ReadAllBytes(path);
            bytes[48] ^= 0x40;
            File.WriteAllBytes(path, bytes);
            using PureMidiContentPack pack = PureMidiContentPack.Open(path);
            IPureMidiSegmentContentSource source = pack.GetSegmentSource(segment.Id);
            Assert.Throws<InvalidDataException>(() => source.GetNote(0));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SharedDecodedCacheAppliesOneProjectWideByteLimitAcrossPacks()
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "midora-paged-content-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using PureMidiContentPackDecodedCache cache = new(
                PureMidiContentPackWriter.MaximumDecodedPageByteCount);
            MidoraProject project = new(192);
            MidiSegment firstSegment = new(project);
            MidiSegment secondSegment = new(project);
            byte[] payload = new byte[3 * 1024 * 1024];
            using PureMidiContentPack first = WritePack("first", firstSegment);
            using PureMidiContentPack second = WritePack("second", secondSegment);

            _ = first.GetSegmentSource(firstSegment.Id).GetOpaqueEvent(0);
            Assert.InRange(cache.ByteCount, payload.Length, cache.ByteLimit);
            _ = second.GetSegmentSource(secondSegment.Id).GetOpaqueEvent(0);

            Assert.InRange(cache.ByteCount, payload.Length, cache.ByteLimit);
            Assert.Equal(2, first.PageCacheMissCount + second.PageCacheMissCount);
            Assert.Equal(cache.ByteCount, first.DecodedCacheByteCount);
            Assert.Equal(cache.ByteCount, second.DecodedCacheByteCount);

            PureMidiContentPack WritePack(string name, MidiSegment segment)
            {
                string path = System.IO.Path.Combine(directory, name + ".mpk");
                using PureMidiContentPackWriter writer = new(path);
                writer.AddOpaqueEvent(segment.Id, new(
                    project.AllocateStableId(),
                    0,
                    OpaqueMidiEventKind.Meta,
                    1,
                    payload,
                    0));
                return writer.Complete(cache);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
