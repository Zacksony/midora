using System.Collections.Immutable;
using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class PagedTimelineCollectionTests
{
    [Fact]
    public void SmallIdResolutionNeverEnumeratesTheLargeSourceExclusionSet()
    {
        DirectMidiNoteValue retained = new(
            new MidoraId(20_001),
            10,
            4,
            60,
            100,
            0,
            0,
            1);
        SingleNoteSource source = new(retained);
        CountingReadOnlySet exclusions = new(Enumerable.Range(1, 10_000)
            .Select(static value => new MidoraId(value)));
        DirectMidiNoteQuerySnapshot snapshot = new(
            source,
            clearSource: false,
            exclusions,
            ImmutableDictionary<MidoraId, DirectMidiNoteValue>.Empty,
            DirectMidiNoteOverlayIndex.Empty,
            new PureMidiSourceIdResolutionCache<DirectMidiNoteSourceMatch>(
                static match => match.Value.Id,
                static match => match.Index),
            count: 1,
            generation: 1);
        exclusions.ResetCounters();

        DirectMidiNoteValue[] result = snapshot.ResolveByIds(
            new HashSet<MidoraId> { new(5), retained.Id }).ToArray();

        Assert.Equal([retained.Id], result.Select(static value => value.Id));
        Assert.Equal(0, exclusions.EnumerationCount);
        Assert.Equal(2, exclusions.ContainsCount);
    }

    [Fact]
    public void SourceExclusionsUseAnExactSortedIdRangeIndex()
    {
        ImmutableDictionary<MidoraId, int>.Builder replacements =
            ImmutableDictionary.CreateBuilder<MidoraId, int>();
        for (int value = 20_000; value >= 10_000; value--)
            replacements.Add(new MidoraId(value), value);
        PureMidiSourceExclusionSet<int> exclusions = new(
            ImmutableHashSet.Create(new MidoraId(5), new MidoraId(30_000)),
            replacements.ToImmutable());

        Assert.False(exclusions.MayContain(new MidoraId(6), new MidoraId(9_999)));
        Assert.True(exclusions.MayContain(new MidoraId(9_999), new MidoraId(10_000)));
        Assert.True(exclusions.MayContain(new MidoraId(19_999), new MidoraId(20_001)));
        Assert.False(exclusions.MayContain(new MidoraId(20_001), new MidoraId(29_999)));
        Assert.True(exclusions.MayContain(new MidoraId(30_000), new MidoraId(30_000)));
    }

    [Fact]
    public void DirectMidiOverlaySpatialIndexKeepsKeyLocalFingerprintAndStartQueriesExact()
    {
        DirectMidiNoteValue[] values = Enumerable.Range(0, 8_192)
            .Select(index => new DirectMidiNoteValue(
                new MidoraId(index + 1),
                index % 256,
                4 + index % 13,
                index % 128,
                1 + index % 127,
                0,
                index * 2L,
                index * 2L + 1))
            .ToArray();
        DirectMidiNoteOverlayIndex index = DirectMidiNoteOverlayIndex.Create(values);
        const long startTick = 80;
        const long endTick = 144;
        const int minimumKey = 48;
        const int maximumKey = 63;
        DirectMidiNoteValue[] expected = values.Where(value =>
                value.StartTick < endTick
                && value.StartTick + value.LengthTicks > startTick
                && value.Key >= minimumKey
                && value.Key <= maximumKey)
            .ToArray();

        Assert.Equal(
            expected.Select(static value => value.Id).Order().ToArray(),
            index.Query(startTick, endTick, minimumKey, maximumKey)
                .Select(static value => value.Id).Order().ToArray());
        Assert.Equal(
            DirectMidiNoteOverlayIndex.Create(expected).GetRangeFingerprint(
                startTick,
                endTick,
                minimumKey,
                maximumKey),
            index.GetRangeFingerprint(startTick, endTick, minimumKey, maximumKey));

        DirectMidiNoteValue[] atStart = values.Where(static value =>
                value.StartTick == 100 && value.Key == 100)
            .ToArray();
        Assert.Equal(
            atStart.Select(static value => value.Id).Order().ToArray(),
            index.QueryStartKey(100, 100).Select(static value => value.Id).Order().ToArray());
        HashSet<DirectMidiNoteStartKey> keys = [new(100, 100), new(101, 101)];
        Assert.Equal(
            values.Where(value => keys.Contains(new(value.StartTick, value.Key)))
                .Select(static value => value.Id).Order().ToArray(),
            index.QueryStartKeys(keys).Select(static value => value.Id).Order().ToArray());
    }

    [Fact]
    public void SourceExclusionsIdentifyFullyReplacedOrdinalPages()
    {
        ImmutableDictionary<MidoraId, int> replacements = Enumerable.Range(0, 256)
            .ToImmutableDictionary(
                index => new MidoraId(10_000 + index),
                static index => index);
        Dictionary<MidoraId, int> ordinals = replacements.Keys
            .ToDictionary(id => id, id => checked((int)(id.Value - 10_000)));
        PureMidiSourceExclusionSet<int> exclusions = new(
            ImmutableHashSet<MidoraId>.Empty,
            replacements,
            ordinals);

        Assert.True(exclusions.ContainsAllOrdinals(0, 128));
        Assert.True(exclusions.ContainsAllOrdinals(128, 128));
        Assert.True(exclusions.MayContainOrdinalRange(96, 64));
        Assert.False(exclusions.MayContainOrdinalRange(256, 64));

        PureMidiSourceExclusionSet<int> partial = new(
            ImmutableHashSet<MidoraId>.Empty,
            replacements.Remove(new MidoraId(10_127)),
            ordinals);
        Assert.False(partial.ContainsAllOrdinals(0, 128));
        Assert.True(partial.MayContainOrdinalRange(0, 128));
    }

    [Fact]
    public void LogicalAndTemplateAtomicValueUpdatesPublishOneGeneration()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 1_000 };
        LogicalNote note = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        long noteGeneration = segment.Notes.Generation;

        note.SetValues(30, 40, 72, 80);

        Assert.Equal(noteGeneration + 1, segment.Notes.Generation);
        LogicalNoteSnapshotValue noteValue = Assert.Single(
            segment.Notes.CreateQuerySnapshot().EnumerateAll());
        Assert.Equal((30L, 40L, 72, 80),
            (noteValue.StartTick, noteValue.LengthTicks, noteValue.Note, noteValue.Velocity));
        note.SetValues(30, 40, 72, 80);
        Assert.Equal(noteGeneration + 1, segment.Notes.Generation);

        SubVoice voice = new(project);
        TemplateEvent template = TemplateEvent.Note(project, 10, 20, 60, 100);
        voice.Events.Add(template);
        long eventGeneration = voice.Events.Generation;

        template.SetValues(
            TemplateEventKind.Note,
            30,
            40,
            72,
            80,
            12,
            hasBankMsb: false,
            hasBankLsb: false,
            followPitchDelta: false);
        template.EnsureMappings();

        Assert.Equal(eventGeneration + 1, voice.Events.Generation);
        TemplateEventSnapshotValue eventValue = Assert.Single(
            voice.Events.CreateQuerySnapshot().EnumerateAll());
        Assert.Equal((30L, 40L, 72, 80, 12, false, false, false),
            (eventValue.Tick,
                eventValue.LengthTicks,
                eventValue.Number,
                eventValue.Value,
                eventValue.SecondaryValue,
                eventValue.HasBankMsb,
                eventValue.HasBankLsb,
                eventValue.FollowPitchDelta));
        template.SetValues(
            TemplateEventKind.Note,
            30,
            40,
            72,
            80,
            12,
            hasBankMsb: false,
            hasBankLsb: false,
            followPitchDelta: false);
        Assert.Equal(eventGeneration + 1, voice.Events.Generation);

        MidiSegment midiSegment = new(project) { LengthTicks = 1_000 };
        DirectMidiChannelEvent directEvent = new(project)
        {
            Tick = 10,
            Kind = DirectMidiChannelEventKind.ControlChange,
            Data1 = 1,
            Data2 = 64,
            Order = 2
        };
        midiSegment.ChannelEvents.Add(directEvent);
        long directGeneration = midiSegment.ChannelEvents.Generation;

        directEvent.SetValues(30, DirectMidiChannelEventKind.PitchBend, 2, 80, 4);

        Assert.Equal(directGeneration + 1, midiSegment.ChannelEvents.Generation);
        directEvent.SetValues(30, DirectMidiChannelEventKind.PitchBend, 2, 80, 4);
        Assert.Equal(directGeneration + 1, midiSegment.ChannelEvents.Generation);
    }

    [Fact]
    public void PureMidiQuerySnapshotsAreReusedWithinOneGeneration()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        DirectMidiNote note = new(project) { StartTick = 10, LengthTicks = 20 };
        DirectMidiChannelEvent channelEvent = new(project) { Tick = 10 };
        OpaqueMidiEvent opaqueEvent = new(project) { Tick = 10, Payload = [1, 2, 3] };
        segment.Notes.Add(note);
        segment.ChannelEvents.Add(channelEvent);
        segment.OpaqueEvents.Add(opaqueEvent);

        DirectMidiNoteQuerySnapshot noteBefore = segment.Notes.CreateQuerySnapshot();
        DirectMidiChannelEventQuerySnapshot channelBefore = segment.ChannelEvents.CreateQuerySnapshot();
        OpaqueMidiEventQuerySnapshot opaqueBefore = segment.OpaqueEvents.CreateQuerySnapshot();
        Assert.Same(noteBefore, segment.Notes.CreateQuerySnapshot());
        Assert.Same(channelBefore, segment.ChannelEvents.CreateQuerySnapshot());
        Assert.Same(opaqueBefore, segment.OpaqueEvents.CreateQuerySnapshot());

        note.StartTick = 20;
        channelEvent.Tick = 20;
        opaqueEvent.Tick = 20;
        DirectMidiNoteQuerySnapshot noteAfter = segment.Notes.CreateQuerySnapshot();
        DirectMidiChannelEventQuerySnapshot channelAfter = segment.ChannelEvents.CreateQuerySnapshot();
        OpaqueMidiEventQuerySnapshot opaqueAfter = segment.OpaqueEvents.CreateQuerySnapshot();

        Assert.NotSame(noteBefore, noteAfter);
        Assert.NotSame(channelBefore, channelAfter);
        Assert.NotSame(opaqueBefore, opaqueAfter);
        Assert.Same(noteAfter, segment.Notes.CreateQuerySnapshot());
        Assert.Same(channelAfter, segment.ChannelEvents.CreateQuerySnapshot());
        Assert.Same(opaqueAfter, segment.OpaqueEvents.CreateQuerySnapshot());
    }

    [Fact]
    public void LogicalSnapshotPublishesTwoValuesAfterOneMovesOntoTheOther()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 1_000 };
        LogicalNote incumbent = new(project) { StartTick = 100, LengthTicks = 40, Note = 64, Velocity = 90 };
        LogicalNote mover = new(project) { StartTick = 20, LengthTicks = 80, Note = 64, Velocity = 100 };
        segment.Notes.AddRange([incumbent, mover]);
        Assert.Single(segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64));

        mover.StartTick = 100;

        Assert.Equal(
            [incumbent.Id, mover.Id],
            segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64)
                .Select(static value => value.Id));

        mover.StartTick = 20;
        Assert.Single(segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64));
        mover.StartTick = 100;
        Assert.Equal(
            [incumbent.Id, mover.Id],
            segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64)
                .Select(static value => value.Id));

        Action restore = segment.Notes.RemoveRangeWithUndo([mover]);
        Assert.Single(segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64));
        restore();
        mover.StartTick = 20;
        Assert.Single(segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64));
        mover.StartTick = 100;
        Assert.Equal(
            [incumbent.Id, mover.Id],
            segment.Notes.CreateQuerySnapshot().QueryValues(100, 101, 64, 64)
                .Select(static value => value.Id));
    }

    [Fact]
    public void DirectMidiLocalFingerprintRestoresAfterColdRegionEditUndo()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 100_000 };
        DirectMidiNote near = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Key = 60,
            NoteOnVelocity = 100
        };
        segment.Notes.Add(near);
        ulong originalNear = segment.Notes.CreateQuerySnapshot()
            .GetRangeFingerprint(0, 1_000, 0, 127);

        DirectMidiNote far = new(project)
        {
            StartTick = 80_000,
            LengthTicks = 100,
            Key = 64,
            NoteOnVelocity = 100
        };
        segment.Notes.Add(far);
        DirectMidiNoteQuerySnapshot edited = segment.Notes.CreateQuerySnapshot();

        Assert.Equal(
            originalNear,
            edited.GetRangeFingerprint(0, 1_000, 0, 127));
        Assert.True(segment.Notes.Remove(far));
        Assert.Equal(
            originalNear,
            segment.Notes.CreateQuerySnapshot()
                .GetRangeFingerprint(0, 1_000, 0, 127));
    }

    [Fact]
    public void LogicalNotesUseCopyOnWritePageSnapshotsAndRangeQueries()
    {
        MidoraProject project = new(192);
        Segment segment = new(project)
        {
            LengthTicks = 20_000,
            ContentOffsetTick = 0
        };
        LogicalNote[] notes = Enumerable.Range(0, 8_500)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();

        segment.Notes.AddRange(notes);
        LogicalNoteQuerySnapshot before = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(3, segment.Notes.PageCount);

        LogicalNote changed = notes[4_200];
        changed.StartTick = 18_000;
        LogicalNoteQuerySnapshot after = segment.Notes.CreateQuerySnapshot();

        LogicalNoteSnapshotValue oldValue = Assert.Single(
            before.QueryValues(8_399, 8_403),
            value => value.Id == changed.Id);
        Assert.Equal(8_400, oldValue.StartTick);
        Assert.DoesNotContain(
            after.QueryValues(8_399, 8_403),
            value => value.Id == changed.Id);
        Assert.Contains(
            after.QueryValues(17_999, 18_003),
            value => value.Id == changed.Id && value.StartTick == 18_000);
    }

    [Fact]
    public void LogicalValueOverlayKeepsOldSnapshotAndRestoresFingerprintAndRaster()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 2_000 };
        LogicalNote note = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        LogicalNoteQuerySnapshot before = segment.Notes.CreateQuerySnapshot();
        ulong originalContent = before.ContentFingerprint;
        ulong originalOldRange = before.GetRangeFingerprint(0, 100, 60, 60);
        TimelineRasterColumnSummary[] beforeOldRaster = new TimelineRasterColumnSummary[8];
        before.AccumulateRasterColumns(
            RasterProjection(0, 100, beforeOldRaster.Length),
            60,
            60,
            beforeOldRaster);
        Assert.Contains(beforeOldRaster, static column => column.HasContent);

        note.SetValues(900, note.LengthTicks, note.Note, note.Velocity);
        LogicalNoteQuerySnapshot moved = segment.Notes.CreateQuerySnapshot();
        TimelineRasterColumnSummary[] movedOldRaster = new TimelineRasterColumnSummary[8];
        TimelineRasterColumnSummary[] movedNewRaster = new TimelineRasterColumnSummary[8];
        moved.AccumulateRasterColumns(
            RasterProjection(0, 100, movedOldRaster.Length),
            60,
            60,
            movedOldRaster);
        moved.AccumulateRasterColumns(
            RasterProjection(850, 1_000, movedNewRaster.Length),
            60,
            60,
            movedNewRaster);

        Assert.Single(before.QueryValues(0, 100, 60, 60));
        Assert.Empty(moved.QueryValues(0, 100, 60, 60));
        Assert.Single(moved.QueryValues(850, 1_000, 60, 60));
        Assert.DoesNotContain(movedOldRaster, static column => column.HasContent);
        Assert.Contains(movedNewRaster, static column => column.HasContent);
        Assert.NotEqual(originalContent, moved.ContentFingerprint);
        Assert.NotEqual(originalOldRange, moved.GetRangeFingerprint(0, 100, 60, 60));

        note.SetValues(10, note.LengthTicks, note.Note, note.Velocity);
        LogicalNoteQuerySnapshot restored = segment.Notes.CreateQuerySnapshot();
        TimelineRasterColumnSummary[] restoredOldRaster = new TimelineRasterColumnSummary[8];
        restored.AccumulateRasterColumns(
            RasterProjection(0, 100, restoredOldRaster.Length),
            60,
            60,
            restoredOldRaster);
        Assert.Equal(originalContent, restored.ContentFingerprint);
        Assert.Equal(originalOldRange, restored.GetRangeFingerprint(0, 100, 60, 60));
        Assert.Contains(restoredOldRaster, static column => column.HasContent);

        static TimelineRasterColumnProjection RasterProjection(
            long startTick,
            long endTick,
            int columns) => new(
                startTick,
                endTick,
                startTick,
                0,
                columns / (double)(endTick - startTick),
                columns);
    }

    [Fact]
    public void LowZoomRasterSummariesNeverInventTimeLaneCartesianProducts()
    {
        const int groupCount = 1_000;
        const int columnCount = 256;
        using MidoraProject project = new(192);
        Segment logicalSegment = new(project) { LengthTicks = 12_000 };
        logicalSegment.Notes.AddRange(CreateLogicalNotes(0, 0));
        logicalSegment.Notes.AddRange(CreateLogicalNotes(10_000, 127));
        SubVoice subVoice = new(project);
        subVoice.Events.AddRange(CreateTemplateNotes(0, 0));
        subVoice.Events.AddRange(CreateTemplateNotes(10_000, 127));
        MidiSegment midiSegment = new(project) { LengthTicks = 12_000 };
        midiSegment.Notes.AddRange(CreateDirectNotes(0, 0));
        midiSegment.Notes.AddRange(CreateDirectNotes(10_000, 127));
        var projection = new TimelineRasterColumnProjection(
            0,
            12_000,
            0,
            0,
            columnCount / 12_000d,
            columnCount);

        TimelineRasterColumnSummary[] logical = new TimelineRasterColumnSummary[columnCount];
        logicalSegment.Notes.CreateQuerySnapshot().AccumulateRasterColumns(
            projection,
            0,
            127,
            logical);
        TimelineRasterColumnSummary[] template = new TimelineRasterColumnSummary[columnCount];
        subVoice.Events.CreateQuerySnapshot().AccumulateNoteRasterColumns(
            projection,
            0,
            127,
            template);
        TimelineRasterColumnSummary[] direct = new TimelineRasterColumnSummary[columnCount];
        Assert.True(midiSegment.Notes.CreateQuerySnapshot().TryAccumulateRasterColumns(
            projection,
            0,
            127,
            direct,
            out _));

        AssertExactOccupancy(logical);
        AssertExactOccupancy(template);
        AssertExactOccupancy(direct);

        IEnumerable<LogicalNote> CreateLogicalNotes(long tickOffset, int note) =>
            Enumerable.Range(0, groupCount).Select(index => new LogicalNote(project)
            {
                StartTick = tickOffset + index * 2L,
                LengthTicks = 1,
                Note = note,
                Velocity = 100
            });

        IEnumerable<TemplateEvent> CreateTemplateNotes(long tickOffset, int note) =>
            Enumerable.Range(0, groupCount).Select(index => new TemplateEvent(project)
            {
                Kind = TemplateEventKind.Note,
                Tick = tickOffset + index * 2L,
                LengthTicks = 1,
                Number = note,
                Value = 100
            });

        IEnumerable<DirectMidiNote> CreateDirectNotes(long tickOffset, int note) =>
            Enumerable.Range(0, groupCount).Select(index => new DirectMidiNote(project)
            {
                StartTick = tickOffset + index * 2L,
                LengthTicks = 1,
                Key = note,
                NoteOnVelocity = 100
            });

        static void AssertExactOccupancy(TimelineRasterColumnSummary[] columns)
        {
            Assert.Contains(columns[..48], static column => column.HasContent);
            Assert.All(columns[..48], static column =>
                Assert.Equal(0UL, column.LaneMaskHigh));
            Assert.All(columns[48..208], static column => Assert.False(column.HasContent));
            Assert.Contains(columns[208..], static column => column.HasContent);
            Assert.All(columns[208..], static column =>
                Assert.Equal(0UL, column.LaneMaskLow));
        }
    }

    [Fact]
    public void LowZoomRasterSummariesPreserveNoteBoundariesAcrossAllPagedModels()
    {
        using MidoraProject project = new(192);
        Segment logicalSegment = new(project) { LengthTicks = 128 };
        logicalSegment.Notes.AddRange(
        [
            new LogicalNote(project)
            {
                StartTick = 8,
                LengthTicks = 32,
                Note = 60,
                Velocity = 100
            },
            new LogicalNote(project)
            {
                StartTick = 40,
                LengthTicks = 32,
                Note = 60,
                Velocity = 100
            }
        ]);
        SubVoice subVoice = new(project);
        subVoice.Events.AddRange(
        [
            new TemplateEvent(project)
            {
                Kind = TemplateEventKind.Note,
                Tick = 8,
                LengthTicks = 32,
                Number = 60,
                Value = 100
            },
            new TemplateEvent(project)
            {
                Kind = TemplateEventKind.Note,
                Tick = 40,
                LengthTicks = 32,
                Number = 60,
                Value = 100
            }
        ]);
        MidiSegment midiSegment = new(project) { LengthTicks = 128 };
        midiSegment.Notes.AddRange(
        [
            new DirectMidiNote(project)
            {
                StartTick = 8,
                LengthTicks = 32,
                Key = 60,
                NoteOnVelocity = 100
            },
            new DirectMidiNote(project)
            {
                StartTick = 40,
                LengthTicks = 32,
                Key = 60,
                NoteOnVelocity = 100
            }
        ]);
        var projection = new TimelineRasterColumnProjection(
            0,
            128,
            0,
            0,
            0.125,
            16);

        TimelineRasterColumnSummary[] logical = new TimelineRasterColumnSummary[16];
        logicalSegment.Notes.CreateQuerySnapshot().AccumulateRasterColumns(
            projection,
            60,
            60,
            logical);
        TimelineRasterColumnSummary[] template = new TimelineRasterColumnSummary[16];
        subVoice.Events.CreateQuerySnapshot().AccumulateNoteRasterColumns(
            projection,
            60,
            60,
            template);
        TimelineRasterColumnSummary[] direct = new TimelineRasterColumnSummary[16];
        Assert.True(midiSegment.Notes.CreateQuerySnapshot().TryAccumulateRasterColumns(
            projection,
            60,
            60,
            direct,
            out _));

        AssertStarts(logical);
        AssertStarts(template);
        AssertStarts(direct);

        static void AssertStarts(TimelineRasterColumnSummary[] columns)
        {
            const ulong noteMask = 1UL << 60;
            Assert.Equal(noteMask, columns[1].StartLaneMaskLow & noteMask);
            Assert.Equal(noteMask, columns[5].StartLaneMaskLow & noteMask);
            Assert.Equal(noteMask, columns[4].EndLaneMaskLow & noteMask);
            Assert.Equal(noteMask, columns[8].EndLaneMaskLow & noteMask);
            Assert.All(
                columns.Where((_, index) => index is not 1 and not 5),
                column => Assert.Equal(0UL, column.StartLaneMaskLow & noteMask));
        }
    }

    [Fact]
    public void LowZoomRasterSummaryDoesNotInventAStartAtAClippedTileEdge()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 256 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 0,
            LengthTicks = 256,
            Note = 60,
            Velocity = 100
        });
        var projection = new TimelineRasterColumnProjection(
            128,
            256,
            128,
            0,
            0.125,
            16);
        TimelineRasterColumnSummary[] columns = new TimelineRasterColumnSummary[16];

        segment.Notes.CreateQuerySnapshot().AccumulateRasterColumns(
            projection,
            60,
            60,
            columns);

        Assert.Contains(columns, static column => column.HasContent);
        Assert.All(columns, static column => Assert.Equal(0UL, column.StartLaneMaskLow));
    }

    [Fact]
    public void RasterColumnProjectionKeepsAdjacentTicksDistinctBeyondDoubleIntegerPrecision()
    {
        const long origin = 9_007_199_254_740_992;
        var projection = new TimelineRasterColumnProjection(
            origin,
            origin + 16,
            origin,
            0,
            1,
            16);

        for (int offset = 0; offset < 16; offset++)
        {
            Assert.True(projection.TryGetColumns(
                origin + offset,
                origin + offset + 1,
                out int first,
                out int lastExclusive));
            Assert.Equal(offset, first);
            Assert.Equal(offset + 1, lastExclusive);
        }
    }

    [Fact]
    public void LogicalValueOverlayRemovesGhostMaximumEndTick()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 2_000 };
        LogicalNote near = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        LogicalNote far = new(project)
        {
            StartTick = 1_000,
            LengthTicks = 100,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([near, far]);
        LogicalNoteQuerySnapshot before = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(1_100L, before.MaximumEndTick);

        far.SetValues(100, 5, far.Note, far.Velocity);
        LogicalNoteQuerySnapshot moved = segment.Notes.CreateQuerySnapshot();

        Assert.Equal(105L, moved.MaximumEndTick);
        Assert.Equal(1_100L, before.MaximumEndTick);
        Assert.Empty(moved.QueryValues(1_000, 1_100));
        Assert.Single(moved.QueryValues(100, 106), value => value.Id == far.Id);
    }

    [Fact]
    public void LogicalNoteBatchRemovalIsStableAndAdvancesGenerationOnce()
    {
        MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalNote[] notes = Enumerable.Range(0, 100_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        long generation = segment.Notes.Generation;
        LogicalNote[] removed = notes.Where((_, index) => (index & 1) == 0).ToArray();

        Assert.Equal(removed.Length, segment.Notes.RemoveRange(removed));

        Assert.Equal(generation + 1, segment.Notes.Generation);
        Assert.Equal(50_000, segment.Notes.Count);
        Assert.Equal(
            Enumerable.Range(0, 50_000).Select(index => index * 2L + 1),
            segment.Notes.Select(static value => value.StartTick));
    }

    [Fact]
    public void SubVoiceEventsUseCopyOnWritePageSnapshotsAndBatchRemoval()
    {
        MidoraProject project = new(192);
        SubVoice voice = new(project);
        TemplateEvent[] events = Enumerable.Range(0, 8_500)
            .Select(index => new TemplateEvent(project)
            {
                Kind = TemplateEventKind.Note,
                Tick = index * 2L,
                LengthTicks = 2,
                Number = index % 128,
                Value = 100
            })
            .ToArray();
        voice.Events.AddRange(events);
        TemplateEventQuerySnapshot before = voice.Events.CreateQuerySnapshot();
        Assert.Equal(3, voice.Events.PageCount);

        TemplateEvent changed = events[4_200];
        changed.Tick = 18_000;
        TemplateEventQuerySnapshot after = voice.Events.CreateQuerySnapshot();
        Assert.Contains(before.QueryNotes(8_399, 8_403), value => value.Id == changed.Id);
        Assert.DoesNotContain(after.QueryNotes(8_399, 8_403), value => value.Id == changed.Id);
        Assert.Contains(after.QueryNotes(17_999, 18_003), value => value.Id == changed.Id);

        long generation = voice.Events.Generation;
        TemplateEvent[] removed = events.Take(5_000).ToArray();
        Assert.Equal(removed.Length, voice.Events.RemoveRange(removed));
        Assert.Equal(generation + 1, voice.Events.Generation);
        Assert.Equal(3_500, voice.Events.Count);
    }

    [Fact]
    public void ExactCollisionBatchRemovalRestoresPagedOrderAndPageBoundaries()
    {
        MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalNote[] notes = Enumerable.Range(0, 9_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        LogicalNote[] removed = notes
            .Where((_, index) => index < 4_096 || index >= 4_096 && index % 3 == 0)
            .ToArray();
        int originalPages = segment.Notes.PageCount;

        Action restore = segment.Notes.RemoveRangeForExactCollision(removed);

        Assert.Equal(notes.Length - removed.Length, segment.Notes.Count);
        Assert.DoesNotContain(segment.Notes, removed.Contains);

        restore();

        Assert.Equal(originalPages, segment.Notes.PageCount);
        Assert.Equal(notes, segment.Notes);
        LogicalNoteQuerySnapshot snapshot = segment.Notes.CreateQuerySnapshot();
        Assert.Equal(notes.Length, snapshot.Count);
        Assert.Equal(notes.Select(static value => value.Id), snapshot.EnumerateAll().Select(static value => value.Id));
    }

    [Fact]
    public void LogicalNoteDuplicateAndUndoRemainExactAcrossManyPages()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project) { Name = "Instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 100_000 };
        LogicalNote[] notes = Enumerable.Range(0, 20_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        track.Segments.Add(segment);
        IPreparedProjectEdit source = ProjectDomainEditCommands.DuplicateLogicalNotes(
                segment.Id,
                notes.Select(static value => value.Id).ToArray(),
                segment.Id,
                newEarliestStartTick: 50_000)
            .Prepare(project);
        IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(project, source);

        edit.Apply(project);
        Assert.Equal(40_000, track.Segments.Single(value => value.Id == segment.Id).Notes.Count);
        Assert.Equal(20_000, segment.Notes.Count); // Frozen Undo root remains unchanged.

        edit.Undo(project);
        Assert.Same(segment, track.Segments.Single(value => value.Id == segment.Id));
        Assert.Equal(notes, track.Segments.Single(value => value.Id == segment.Id).Notes);
    }

    [Fact]
    public void TemplateNoteDuplicateAndUndoRemainExactAcrossManyPages()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            TemplateLengthTicks = 100_000
        };
        SubVoice voice = new(project) { Name = "Voice" };
        TemplateEvent[] notes = Enumerable.Range(0, 20_000)
            .Select(index => TemplateEvent.Note(
                project,
                index * 2L,
                lengthTicks: 1,
                note: index % 128,
                velocity: 100))
            .ToArray();
        voice.Events.AddRange(notes);
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        IPreparedProjectEdit source = ProjectDomainEditCommands.DuplicateTemplateNotes(
                instrument.Id,
                voice.Id,
                notes.Select(static value => value.Id).ToArray(),
                newEarliestTick: 50_000,
                pitchDelta: 0)
            .Prepare(project);
        IPreparedProjectEdit edit = ExactTimelineCollisionPolicy.Wrap(project, source);

        edit.Apply(project);
        Assert.Equal(40_000, instrument.SubVoices.Single(value => value.Id == voice.Id).Events.Count);
        Assert.Equal(20_000, voice.Events.Count); // Frozen Undo root remains unchanged.

        edit.Undo(project);
        Assert.Same(voice, instrument.SubVoices.Single(value => value.Id == voice.Id));
        Assert.Equal(notes, instrument.SubVoices.Single(value => value.Id == voice.Id).Events);
        Assert.Equal(100_000, instrument.TemplateLengthTicks);
    }

    [Fact]
    public void ConcurrentColdSnapshotPublicationReturnsOneImmutableRevision()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 100_000 };
        segment.Notes.AddRange(Enumerable.Range(0, 20_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray());

        ulong[] fingerprints = new ulong[2_000];
        Parallel.For(0, fingerprints.Length, index =>
        {
            LogicalNoteQuerySnapshot snapshot = segment.Notes.CreateQuerySnapshot();
            Assert.Equal(20_000, snapshot.Count);
            fingerprints[index] = snapshot.ContentFingerprint;
        });

        Assert.All(fingerprints, value => Assert.Equal(fingerprints[0], value));
    }

    [Fact]
    public void InsertRangeRejectsDuplicateStableIdsWithoutPartialMutation()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 1_000 };
        LogicalNote existing = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(existing);
        LogicalNote valid = new(project)
        {
            StartTick = 40,
            LengthTicks = 20,
            Note = 62,
            Velocity = 100
        };
        LogicalNote duplicate = new(project, existing.Id)
        {
            StartTick = 80,
            LengthTicks = 20,
            Note = 64,
            Velocity = 100
        };

        Assert.Throws<InvalidOperationException>(() =>
            segment.Notes.InsertRange(1, [valid, duplicate]));

        Assert.Single(segment.Notes);
        Assert.Same(existing, segment.Notes[0]);
        Assert.DoesNotContain(valid, segment.Notes);
    }

    [Fact]
    public void BulkAppendKeepsObjectIdentityAndPreviouslyPublishedSnapshotsImmutable()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 100_000 };
        LogicalNote existing = new(project)
        {
            StartTick = 1,
            LengthTicks = 2,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(existing);
        LogicalNoteQuerySnapshot before = segment.Notes.CreateQuerySnapshot();
        long generation = segment.Notes.Generation;
        LogicalNote[] appended = Enumerable.Range(0, 8_193)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index + 10,
                LengthTicks = 3,
                Note = index & 127,
                Velocity = 90
            })
            .ToArray();

        segment.Notes.AddRange(appended);

        Assert.Equal(generation + 1, segment.Notes.Generation);
        Assert.Equal(3, segment.Notes.PageCount);
        Assert.Same(existing, segment.Notes[0]);
        Assert.All(appended.Select((value, index) => (value, index)), pair =>
            Assert.Same(pair.value, segment.Notes[pair.index + 1]));
        Assert.Equal([existing.Id], before.EnumerateAll().Select(static value => value.Id));

        LogicalNoteQuerySnapshot published = segment.Notes.CreateQuerySnapshot();
        appended[4_200].Velocity = 47;
        Assert.Equal(90, Assert.Single(published.ResolveByIds([appended[4_200].Id])).Velocity);
        Assert.Equal(47, Assert.Single(segment.Notes.CreateQuerySnapshot()
            .ResolveByIds([appended[4_200].Id])).Velocity);
    }

    [Fact]
    public void BulkTemplateAppendKeepsIdentityMappingsAndOldSnapshotExact()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        TemplateEvent existing = TemplateEvent.Note(project, 1, 2, 60, 100);
        voice.Events.Add(existing);
        TemplateEventQuerySnapshot before = voice.Events.CreateQuerySnapshot();
        long generation = voice.Events.Generation;
        TemplateEvent[] appended = Enumerable.Range(0, 8_193)
            .Select(index => TemplateEvent.Note(project, index + 10, 3, index & 127, 90))
            .ToArray();

        voice.Events.AddRange(appended);

        Assert.Equal(generation + 1, voice.Events.Generation);
        Assert.Equal(3, voice.Events.PageCount);
        Assert.Same(existing, voice.Events[0]);
        Assert.All(appended.Select((value, index) => (value, index)), pair =>
            Assert.Same(pair.value, voice.Events[pair.index + 1]));
        Assert.Equal([existing.Id], before.EnumerateAll().Select(static value => value.Id));
        Assert.Contains(voice.EventMappings, mapping =>
            mapping.Target.EventKind == TemplateEventKind.Note
            && mapping.Target.Parameter == TemplateEventMappingParameter.Number);
        Assert.Contains(voice.EventMappings, mapping =>
            mapping.Target.EventKind == TemplateEventKind.Note
            && mapping.Target.Parameter == TemplateEventMappingParameter.Value);

        TemplateEventQuerySnapshot published = voice.Events.CreateQuerySnapshot();
        appended[4_200].Value = 47;
        Assert.Equal(90, Assert.Single(published.ResolveByIds([appended[4_200].Id])).Value);
        Assert.Equal(47, Assert.Single(voice.Events.CreateQuerySnapshot()
            .ResolveByIds([appended[4_200].Id])).Value);
    }

    [Fact]
    public void BulkTemplateAppendValidatesAllIdsBeforeAttachingAnyEvent()
    {
        using MidoraProject project = new(192);
        SubVoice destination = new(project);
        TemplateEvent existing = TemplateEvent.Note(project, 1, 2, 60, 100);
        destination.Events.Add(existing);
        int mappingsBefore = destination.EventMappings.Count;
        TemplateEvent valid = TemplateEvent.Note(project, 10, 2, 61, 100);
        TemplateEvent duplicate = new(project, existing.Id)
        {
            Kind = TemplateEventKind.Note,
            Tick = 20,
            LengthTicks = 2,
            Number = 62,
            Value = 100
        };

        Assert.Throws<InvalidOperationException>(() =>
            destination.Events.AddRange([valid, duplicate]));

        Assert.Equal([existing], destination.Events);
        Assert.Equal(mappingsBefore, destination.EventMappings.Count);
        SubVoice other = new(project);
        other.Events.Add(valid);
        Assert.Same(valid, Assert.Single(other.Events));
    }

    [Fact]
    public void ExactCollisionRestoreSurvivesInterveningPageSplitsAndRestoresFingerprint()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 100_000 };
        LogicalNote[] original = Enumerable.Range(0, 16_500)
            .Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 100
            })
            .ToArray();
        segment.Notes.AddRange(original);
        ulong originalFingerprint = segment.Notes.CreateQuerySnapshot().ContentFingerprint;
        LogicalNote[] collisions = original.Where((_, index) => index % 3 == 0).ToArray();

        Action restore = segment.Notes.RemoveRangeForExactCollision(collisions);
        LogicalNote[] transient = Enumerable.Range(0, 9_000)
            .Select(index => new LogicalNote(project)
            {
                StartTick = 60_000 + index,
                LengthTicks = 1,
                Note = index % 128,
                Velocity = 80
            })
            .ToArray();
        segment.Notes.InsertRange(17, transient);
        Assert.Equal(transient.Length, segment.Notes.RemoveRange(transient));

        restore();

        Assert.Equal(original, segment.Notes);
        Assert.Equal(
            originalFingerprint,
            segment.Notes.CreateQuerySnapshot().ContentFingerprint);
    }

    [Fact]
    public void DirectCollectionIndexerReplacementIsAtomicAndDetachesOldSink()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        DirectMidiNote first = new(project)
        {
            StartTick = 10,
            LengthTicks = 20,
            Key = 60,
            NoteOnVelocity = 100
        };
        DirectMidiNote second = new(project)
        {
            StartTick = 40,
            LengthTicks = 20,
            Key = 62,
            NoteOnVelocity = 100
        };
        segment.Notes.Add(first);
        segment.Notes.Add(second);
        DirectMidiNote duplicate = new(project, second.Id)
        {
            StartTick = 80,
            LengthTicks = 10,
            Key = 64,
            NoteOnVelocity = 100
        };

        Assert.Throws<InvalidOperationException>(() => segment.Notes[0] = duplicate);
        Assert.Equal([first, second], segment.Notes);

        DirectMidiNote replacement = new(project)
        {
            StartTick = 100,
            LengthTicks = 10,
            Key = 65,
            NoteOnVelocity = 100
        };
        segment.Notes[0] = replacement;
        long generation = segment.Notes.Generation;
        first.StartTick = 999;

        Assert.Equal(generation, segment.Notes.Generation);
        Assert.Equal([replacement, second], segment.Notes);
    }

    [Fact]
    public void OpaqueSnapshotOwnsPayloadBytes()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 1_000 };
        byte[] payload = [1, 2, 3];
        OpaqueMidiEvent value = new(project)
        {
            Tick = 12,
            Kind = OpaqueMidiEventKind.Meta,
            MetaType = 0x7f,
            Payload = payload
        };
        segment.OpaqueEvents.Add(value);
        OpaqueMidiEventQuerySnapshot before = segment.OpaqueEvents.CreateQuerySnapshot();

        payload[0] = 99;
        value.Payload = [7, 8, 9];
        OpaqueMidiEventQuerySnapshot after = segment.OpaqueEvents.CreateQuerySnapshot();

        Assert.Equal([1, 2, 3], Assert.Single(before.QueryValues(0, 100)).Payload.ToArray());
        Assert.Equal([7, 8, 9], Assert.Single(after.QueryValues(0, 100)).Payload.ToArray());
    }

    [Fact]
    public void PureMidiOverlayTargetedRemovalRestoresRandomFormalOrderAndOldSnapshots()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 100_000 };
        const int count = 12_000;
        DirectMidiNote[] notes = Enumerable.Range(0, count)
            .Select(index => new DirectMidiNote(project)
            {
                StartTick = index * 4L,
                LengthTicks = 2,
                Key = index & 0x7f,
                NoteOnVelocity = 100,
                NoteOffVelocity = 64,
                NoteOnOrder = index * 3L,
                NoteOffOrder = index * 3L + 1
            })
            .ToArray();
        DirectMidiChannelEvent[] channelEvents = Enumerable.Range(0, count)
            .Select(index => new DirectMidiChannelEvent(project)
            {
                Tick = index * 4L,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = index % 120,
                Data2 = index & 0x7f,
                Order = index * 3L + 2
            })
            .ToArray();
        OpaqueMidiEvent[] opaqueEvents = Enumerable.Range(0, count)
            .Select(index => new OpaqueMidiEvent(project)
            {
                Tick = index * 4L,
                Kind = OpaqueMidiEventKind.Meta,
                MetaType = 0x7f,
                Payload = [(byte)index],
                Order = index
            })
            .ToArray();
        segment.Notes.AddRange(notes);
        segment.ChannelEvents.AddRange(channelEvents);
        segment.OpaqueEvents.AddRange(opaqueEvents);
        DirectMidiNoteQuerySnapshot oldNotes = segment.Notes.CreateQuerySnapshot();
        DirectMidiChannelEventQuerySnapshot oldChannel = segment.ChannelEvents.CreateQuerySnapshot();
        OpaqueMidiEventQuerySnapshot oldOpaque = segment.OpaqueEvents.CreateQuerySnapshot();

        Random random = new(0x50414745);
        int[] selectedIndices = Enumerable.Range(count * 3 / 4, count / 4)
            .OrderBy(_ => random.Next())
            .Take(1_000)
            .Order()
            .ToArray();
        DirectMidiNote[] removedNotes = selectedIndices.Select(index => notes[index]).ToArray();
        DirectMidiChannelEvent[] removedChannel = selectedIndices.Select(index => channelEvents[index]).ToArray();
        OpaqueMidiEvent[] removedOpaque = selectedIndices.Select(index => opaqueEvents[index]).ToArray();

        Verify(
            notes,
            removedNotes,
            static value => value.Id,
            () => segment.Notes.Select(static value => value.Id).ToArray(),
            values => segment.Notes.RemoveRangeWithUndo(values));
        Verify(
            channelEvents,
            removedChannel,
            static value => value.Id,
            () => segment.ChannelEvents.Select(static value => value.Id).ToArray(),
            values => segment.ChannelEvents.RemoveRangeWithUndo(values));
        Verify(
            opaqueEvents,
            removedOpaque,
            static value => value.Id,
            () => segment.OpaqueEvents.Select(static value => value.Id).ToArray(),
            values => segment.OpaqueEvents.RemoveRangeWithUndo(values));

        Assert.Equal(count, oldNotes.QueryValues(0, 100_000).Count());
        Assert.Equal(count, oldChannel.QueryValues(0, 100_000).Count());
        Assert.Equal(count, oldOpaque.QueryValues(0, 100_000).Count());

        static void Verify<T>(
            T[] original,
            T[] removed,
            Func<T, MidoraId> getId,
            Func<MidoraId[]> currentIds,
            Func<IReadOnlyCollection<T>, Action> remove)
            where T : class
        {
            HashSet<MidoraId> removedIds = removed.Select(getId).ToHashSet();
            MidoraId[] expectedRemoved = original
                .Select(getId)
                .Where(id => !removedIds.Contains(id))
                .ToArray();
            Action restore = remove(removed);
            Assert.Equal(expectedRemoved, currentIds());
            restore();
            Assert.Equal(original.Select(getId), currentIds());

            Action restoreRedo = remove(removed);
            Assert.Equal(expectedRemoved, currentIds());
            restoreRedo();
            Assert.Equal(original.Select(getId), currentIds());
        }
    }

    private sealed class SingleNoteSource(DirectMidiNoteValue note)
        : IPureMidiSegmentContentSource
    {
        public int NoteCount => 1;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => 0;
        public string ContentFingerprint => "single-note";

        public DirectMidiNoteValue GetNote(int index) => index == 0
            ? note
            : throw new ArgumentOutOfRangeException(nameof(index));

        public DirectMidiChannelEventValue GetChannelEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public OpaqueMidiEventValue GetOpaqueEvent(int index) =>
            throw new ArgumentOutOfRangeException(nameof(index));

        public int FindNoteIndex(MidoraId id) => id == note.Id ? 0 : -1;
        public int FindChannelEventIndex(MidoraId id) => -1;
        public int FindOpaqueEventIndex(MidoraId id) => -1;

        public IEnumerable<DirectMidiNoteValue> QueryNotes(
            long startTick,
            long endTick,
            int minimumKey = 0,
            int maximumKey = 127) =>
            note.StartTick < endTick
            && note.StartTick + note.LengthTicks > startTick
            && note.Key >= minimumKey
            && note.Key <= maximumKey
                ? [note]
                : [];

        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(
            long startTick,
            long endTick) => [];

        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(
            long startTick,
            long endTick) => [];
    }

    private sealed class CountingReadOnlySet(IEnumerable<MidoraId> values)
        : IReadOnlySet<MidoraId>
    {
        private readonly HashSet<MidoraId> _values = [.. values];

        public int Count => _values.Count;
        public int ContainsCount { get; private set; }
        public int EnumerationCount { get; private set; }

        public bool Contains(MidoraId item)
        {
            ContainsCount++;
            return _values.Contains(item);
        }

        public void ResetCounters()
        {
            ContainsCount = 0;
            EnumerationCount = 0;
        }

        public IEnumerator<MidoraId> GetEnumerator()
        {
            EnumerationCount++;
            return _values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<MidoraId> other) =>
            _values.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<MidoraId> other) =>
            _values.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<MidoraId> other) =>
            _values.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<MidoraId> other) =>
            _values.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<MidoraId> other) =>
            _values.Overlaps(other);

        public bool SetEquals(IEnumerable<MidoraId> other) =>
            _values.SetEquals(other);
    }
}
