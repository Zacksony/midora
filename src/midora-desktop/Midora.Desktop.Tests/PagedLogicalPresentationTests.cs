using Midora.Desktop.Presentation.Rendering;
using Midora.Desktop.Presentation.Interaction;
using Midora.Domain;
using System.Diagnostics;
using System.Windows.Media;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class PagedLogicalPresentationTests
{
    [Fact]
    public void ArrangementContentRebuildDoesNotResetUnchangedInstrumentBrowser()
    {
        using MidoraProject project = new(192);
        EventInstrument instrument = new(project) { Name = "Browser instrument" };
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = 480 };
        LogicalNote note = new(project)
        {
            StartTick = 0,
            LengthTicks = 120,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        track.Segments.Add(segment);
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        TimelineWorkspaceViewModel workspace = new(
            new WorkspaceKey(WorkspaceKind.Arrangement, null),
            "Arrangement",
            TimelineWorkspaceMode.Arrangement);
        workspace.Rebuild(project, revision: 1);
        int notifications = 0;
        workspace.EventInstrumentBrowser.CollectionChanged += (_, _) => notifications++;

        note.Note = 61;
        workspace.Rebuild(project, revision: 2);

        Assert.Equal(0, notifications);
        EventInstrumentBrowserRow row = Assert.Single(workspace.EventInstrumentBrowser);
        Assert.Equal(instrument.Id, row.Id);
        Assert.Equal(1, row.UsageCount);
        Assert.Equal(1, row.TrackCount);
    }

    [Theory]
    [InlineData("logical")]
    [InlineData("direct")]
    [InlineData("subvoice")]
    public void DensePianoLowLodRasterWorkIsBoundedByPixels(string sourceKind)
    {
        const int noteCount = 60_000;
        using MidoraProject project = new(192);
        TimelineRenderSnapshot snapshot;
        if (sourceKind == "logical")
        {
            Segment segment = new(project) { LengthTicks = noteCount * 2L + 2 };
            segment.Notes.AddRange(Enumerable.Range(0, noteCount).Select(index => new LogicalNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 2,
                Note = index % 128,
                Velocity = 1 + index % 127
            }));
            snapshot = new(
                1,
                "logical-low-lod",
                [],
                itemSource: new PagedLogicalNoteTimelineItemSource(
                    segment,
                    LogicalNoteTimelineProjection.Notes));
        }
        else if (sourceKind == "direct")
        {
            MidiSegment segment = new(project) { LengthTicks = noteCount * 2L + 2 };
            segment.Notes.AddRange(Enumerable.Range(0, noteCount).Select(index => new DirectMidiNote(project)
            {
                StartTick = index * 2L,
                LengthTicks = 2,
                Key = index % 128,
                NoteOnVelocity = 1 + index % 127
            }));
            snapshot = new(
                1,
                "direct-low-lod",
                [],
                itemSource: new PagedDirectMidiTimelineItemSource(
                    segment,
                    DirectMidiTimelineProjection.Notes));
        }
        else
        {
            SubVoice voice = new(project);
            voice.Events.AddRange(Enumerable.Range(0, noteCount).Select(index => new TemplateEvent(project)
            {
                Kind = TemplateEventKind.Note,
                Tick = index * 2L,
                LengthTicks = 2,
                Number = index % 128,
                Value = 1 + index % 127
            }));
            snapshot = new(
                1,
                "subvoice-low-lod",
                [],
                itemSource: new PagedTemplateNoteTimelineItemSource(
                    voice,
                    TemplateNoteTimelineProjection.Notes));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 0.001,
            devicePixelsPerLane: 2,
            tileX: 0,
            tileY: 0,
            Colors.SlateGray,
            Colors.OrangeRed);
        stopwatch.Stop();

        Assert.InRange(raster.CandidateCount, 1, TimelinePianoTileRasterizer.RasterSize * 4);
        Assert.Contains(raster.Pixels, static value => value != 0);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"{sourceKind} low-LOD raster took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Theory]
    [InlineData("logical")]
    [InlineData("direct")]
    [InlineData("subvoice")]
    public void PagedPianoSourcesKeepAdjacentNoteStartsVisibleAtLowLod(string sourceKind)
    {
        using MidoraProject project = new(192);
        ITimelineRenderItemSource source;
        if (sourceKind == "logical")
        {
            Segment segment = new(project) { LengthTicks = 128 };
            segment.Notes.AddRange(
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
            source = new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes);
        }
        else if (sourceKind == "direct")
        {
            MidiSegment segment = new(project) { LengthTicks = 128 };
            segment.Notes.AddRange(
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
            source = new PagedDirectMidiTimelineItemSource(
                segment,
                DirectMidiTimelineProjection.Notes);
        }
        else
        {
            SubVoice voice = new(project);
            voice.Events.AddRange(
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
            source = new PagedTemplateNoteTimelineItemSource(
                voice,
                TemplateNoteTimelineProjection.Notes);
        }
        TimelineRenderSnapshot snapshot = new(
            1,
            $"{sourceKind}-adjacent-low-lod",
            [],
            itemSource: source);
        Color darkFill = Color.FromRgb(68, 75, 80);
        Color brightOutline = Color.FromRgb(163, 178, 190);

        TimelineRasterBuffer raster = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 0.125,
            devicePixelsPerLane: 4,
            tileX: 0,
            tileY: 1,
            darkFill,
            Colors.OrangeRed,
            normalOutlineColor: brightOutline);

        Color fill = PixelColor(raster, 4, 15);
        Color leftEnd = PixelColor(raster, 5, 15);
        Color rightStart = PixelColor(raster, 6, 15);
        Color rightFill = PixelColor(raster, 7, 15);
        Assert.Equal(leftEnd, rightStart);
        Assert.Equal(fill, rightFill);
        Assert.True(leftEnd.R > fill.R);
        Assert.True(leftEnd.G > fill.G);
        Assert.True(leftEnd.B > fill.B);

        static Color PixelColor(TimelineRasterBuffer buffer, int x, int y)
        {
            int offset = (y * buffer.Width + x) * 4;
            return Color.FromArgb(
                buffer.Pixels[offset + 3],
                buffer.Pixels[offset + 2],
                buffer.Pixels[offset + 1],
                buffer.Pixels[offset]);
        }
    }

    [Fact]
    public void ImportedPureMidiPianoSummaryPreservesDisplayLaneStartsAtATileSeam()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-imported-piano-raster-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "notes.mpk");
        try
        {
            using MidoraProject project = new(192);
            MidiSegment segment = new(project) { LengthTicks = 4_096 };
            using PureMidiContentPackWriter writer = new(path);
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 2_000, 96, 100, 100, 0, 0, 1));
            writer.AddNote(segment.Id, new(
                project.AllocateStableId(), 2_048, 48, 60, 100, 0, 2, 3));
            using PureMidiContentPack pack = writer.Complete();
            segment.AttachPagedContent(pack.GetSegmentSource(segment.Id));
            ITimelineRasterAggregateSource source =
                Assert.IsAssignableFrom<ITimelineRasterAggregateSource>(
                    new PagedDirectMidiTimelineItemSource(
                        segment,
                        DirectMidiTimelineProjection.Notes));
            var projection = new TimelineRasterColumnProjection(
                2_040,
                4_104,
                2_040,
                0,
                0.125,
                TimelinePianoTileRasterizer.RasterSize);
            TimelineRasterColumnSummary[] columns =
                new TimelineRasterColumnSummary[TimelinePianoTileRasterizer.RasterSize];

            Assert.True(source.TryAccumulateRasterColumns(
                TimelineRasterAggregateKind.PianoNotes,
                projection,
                0,
                128,
                columns,
                out _));

            const ulong continuedDisplayLaneMask = 1UL << 27; // key 100 -> lane 27
            const ulong seamStartDisplayLaneMask = 1UL << 3; // key 60 -> lane 67
            Assert.NotEqual(0UL, columns[0].LaneMaskLow & continuedDisplayLaneMask);
            Assert.Equal(0UL, columns[0].StartLaneMaskLow & continuedDisplayLaneMask);
            Assert.NotEqual(0UL, columns[1].StartLaneMaskHigh & seamStartDisplayLaneMask);
            Assert.NotEqual(0UL, columns[6].EndLaneMaskHigh & seamStartDisplayLaneMask);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LowZoomSelectionUsesTheSameRoundedPixelColumnsAsTheBaseNoteLayer()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 1_000 };
        LogicalNote note = new(project)
        {
            StartTick = 4,
            LengthTicks = 8,
            Note = 60,
            Velocity = 100
        };
        segment.Notes.Add(note);
        TimelineRenderSnapshot snapshot = new(
            1,
            "low-zoom-selection-alignment",
            [],
            itemSource: new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes));
        TimelineSelectionSnapshot selection = new(
            1,
            [note.Id],
            primary: null,
            [new TimelineRenderItem(
                Id: note.Id,
                Kind: TimelineItemKind.LogicalNote,
                StartTick: note.StartTick,
                EndTick: note.StartTick + note.LengthTicks,
                Lane: 127 - note.Note,
                Value: note.Velocity / 127d,
                ZIndex: 0,
                State: TimelineItemState.Selected)]);

        TimelineRasterBuffer baseLayer = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 0.125,
            devicePixelsPerLane: 4,
            tileX: 0,
            tileY: 0,
            Colors.SlateGray,
            Colors.OrangeRed);
        TimelineRasterBuffer selectionLayer = TimelinePianoTileRasterizer.Rasterize(
            snapshot,
            devicePixelsPerTick: 0.125,
            devicePixelsPerLane: 4,
            tileX: 0,
            tileY: 0,
            Colors.DarkRed,
            Colors.OrangeRed,
            selection,
            selectionOnly: true,
            outlineColor: Colors.Red);

        Assert.Equal(
            OccupiedColumns(baseLayer, firstRow: 240, lastRowExclusive: 246),
            OccupiedColumns(selectionLayer, firstRow: 240, lastRowExclusive: 246));

        static int[] OccupiedColumns(
            TimelineRasterBuffer buffer,
            int firstRow,
            int lastRowExclusive)
        {
            List<int> result = [];
            for (int x = 0; x < buffer.Width; x++)
            {
                bool occupied = false;
                for (int y = firstRow; y < lastRowExclusive; y++)
                {
                    int alpha = buffer.Pixels[(y * buffer.Width + x) * 4 + 3];
                    if (alpha == 0) continue;
                    occupied = true;
                    break;
                }
                if (occupied) result.Add(x);
            }
            return result.ToArray();
        }
    }

    [Fact]
    public void InMemoryPagedNoteSourcesCanPrepareVisibleFingerprintsWithoutAnAsyncRoundTrip()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 10_000 };
        segment.Notes.Add(new LogicalNote(project)
        {
            StartTick = 100,
            LengthTicks = 120,
            Note = 60,
            Velocity = 100
        });
        SubVoice voice = new(project);
        voice.Events.Add(new TemplateEvent(project)
        {
            Kind = TemplateEventKind.Note,
            Tick = 100,
            LengthTicks = 120,
            Number = 60,
            Value = 100
        });

        TimelineRenderSnapshot logical = new(
            1,
            "logical-non-blocking-fingerprint",
            [],
            itemSource: new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes));
        TimelineRenderSnapshot subVoice = new(
            1,
            "subvoice-non-blocking-fingerprint",
            [],
            itemSource: new PagedTemplateNoteTimelineItemSource(
                voice,
                TemplateNoteTimelineProjection.Notes));

        Assert.True(logical.CanComputeTileFingerprintSynchronously);
        Assert.True(subVoice.CanComputeTileFingerprintSynchronously);
    }

    [Fact]
    public void LogicalSegmentRebuildDoesNotMaterializeAFullParameterLane()
    {
        const int pointCount = 100_000;
        using MidoraProject project = new(192);
        EventInstrument instrument = new(project) { Name = "Parameter instrument" };
        LogicalParameterDefinition definition = new(project)
        {
            Name = "Dense parameter",
            Type = LogicalParameterType.Integer,
            Minimum = 0,
            Maximum = 127,
            DisplayMinimum = 0,
            DisplayMaximum = 127
        };
        instrument.LogicalParameters.Add(definition);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track" };
        Segment segment = new(project) { LengthTicks = pointCount + 1L };
        LogicalParameterLane lane = new(project) { ParameterId = definition.Id };
        lane.Points.AddRange(Enumerable.Range(0, pointCount).Select(index =>
            new CurvePoint(project, index, index % 128, CurveInterpolation.Step)));
        segment.ParameterLanes.Add(lane);
        track.Segments.Add(segment);
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        TimelineWorkspaceViewModel workspace = new(
            WorkspaceKey.ForObject(WorkspaceKind.SegmentEditor, segment.Id),
            "Segment",
            TimelineWorkspaceMode.Segment);

        Stopwatch stopwatch = Stopwatch.StartNew();
        workspace.Rebuild(project, revision: 1);
        stopwatch.Stop();

        Assert.NotNull(workspace.ParameterSnapshot);
        Assert.Equal(pointCount, workspace.ParameterSnapshot!.TotalItemCount);
        Assert.Empty(workspace.ParameterSnapshot.Items);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"Dense parameter rebuild took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public void LogicalPianoSourceQueriesOnlyVisibleNotePages()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project)
        {
            LengthTicks = 100_000,
            ContentOffsetTick = 0
        };
        segment.Notes.AddRange(Enumerable.Range(0, 20_000).Select(index => new LogicalNote(project)
        {
            StartTick = index * 4L,
            LengthTicks = 2,
            Note = index % 128,
            Velocity = 100
        }));
        TimelineRenderSnapshot snapshot = new(
            1,
            "logical-paged-query",
            [],
            itemSource: new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes));
        List<TimelineRenderItem> values = [];

        snapshot.QueryInto(10_000, 10_100, 0, 128, values);

        Assert.All(values, value =>
        {
            Assert.True(value.StartTick < 10_100);
            Assert.True(value.EndTick > 10_000);
        });
        Assert.InRange(values.Count, 20, 30);
    }

    [Fact]
    public void LogicalArrangementPreviewFingerprintIsLocalToTheTile()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project)
        {
            LengthTicks = 20_000,
            ContentOffsetTick = 0
        };
        LogicalNote near = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Note = 60,
            Velocity = 100
        };
        LogicalNote far = new(project)
        {
            StartTick = 12_000,
            LengthTicks = 100,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([near, far]);
        const double width = 2_000;
        LogicalSegmentPreviewSource first = Preview(segment);
        ulong tile0 = first.GetTileContentFingerprint(false, width, 0);

        far.Note = 72;
        ulong afterFarEdit = Preview(segment).GetTileContentFingerprint(false, width, 0);
        near.Note = 61;
        ulong afterNearEdit = Preview(segment).GetTileContentFingerprint(false, width, 0);

        Assert.Equal(tile0, afterFarEdit);
        Assert.NotEqual(tile0, afterNearEdit);

        static LogicalSegmentPreviewSource Preview(Segment value) => new(
            value,
            value.Notes.CreateQuerySnapshot(),
            []);
    }

    [Fact]
    public void PianoSelectionFingerprintIgnoresSelectionsOutsideTheTile()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 20_000 };
        LogicalNote near = new(project)
        {
            StartTick = 100,
            LengthTicks = 100,
            Note = 60,
            Velocity = 100
        };
        LogicalNote far = new(project)
        {
            StartTick = 12_000,
            LengthTicks = 100,
            Note = 64,
            Velocity = 100
        };
        segment.Notes.AddRange([near, far]);
        TimelineRenderSnapshot snapshot = new(
            1,
            "logical-local-selection",
            [],
            itemSource: new PagedLogicalNoteTimelineItemSource(
                segment,
                LogicalNoteTimelineProjection.Notes));
        TimelineSelectionSnapshot none = new(1, [], null, []);
        TimelineSelectionSnapshot farOnly = new(
            2,
            [far.Id],
            far.Id,
            [new TimelineRenderItem(
                far.Id,
                TimelineItemKind.LogicalNote,
                far.StartTick,
                far.StartTick + far.LengthTicks,
                127 - far.Note,
                far.Velocity,
                1,
                TimelineItemState.Selected)]);

        ulong first = TimelinePianoTileRasterizer.ComputeSelectionFingerprint(
            snapshot,
            none,
            devicePixelsPerTick: 0.1,
            devicePixelsPerLane: 10,
            tileX: 0,
            tileY: 2);
        ulong second = TimelinePianoTileRasterizer.ComputeSelectionFingerprint(
            snapshot,
            farOnly,
            devicePixelsPerTick: 0.1,
            devicePixelsPerLane: 10,
            tileX: 0,
            tileY: 2);

        Assert.Equal(first, second);
    }

    [Fact]
    public void SubVoiceNoteAndVelocitySourcesShareOneImmutablePagedSnapshot()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        voice.Events.AddRange(Enumerable.Range(0, 10_000).Select(index => new TemplateEvent(project)
        {
            Kind = TemplateEventKind.Note,
            Tick = index * 2L,
            LengthTicks = 2,
            Number = index % 128,
            Value = 100
        }));
        TemplateEventQuerySnapshot values = voice.Events.CreateQuerySnapshot();
        TimelineRenderSnapshot notes = new(
            1,
            "subvoice-notes",
            [],
            itemSource: new PagedTemplateNoteTimelineItemSource(
                voice,
                TemplateNoteTimelineProjection.Notes,
                snapshot: values));
        TimelineRenderSnapshot velocities = new(
            1,
            "subvoice-velocities",
            [],
            itemSource: new PagedTemplateNoteTimelineItemSource(
                voice,
                TemplateNoteTimelineProjection.Velocities,
                snapshot: values));
        List<TimelineRenderItem> noteItems = [];
        List<TimelineRenderItem> velocityItems = [];

        notes.QueryInto(5_000, 5_100, 0, 128, noteItems);
        velocities.QueryInto(5_000, 5_100, 0, 1, velocityItems);

        Assert.Equal(50, noteItems.Count);
        Assert.Equal(50, velocityItems.Count);
        Assert.All(noteItems, value => Assert.Equal(TimelineItemKind.TemplateNote, value.Kind));
        Assert.All(velocityItems, value => Assert.Equal(TimelineItemKind.Velocity, value.Kind));
    }

    [Fact]
    public void SubVoiceEventLaneQueriesOnlyTheActiveTargetAndVisibleRange()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        voice.Events.AddRange(Enumerable.Range(0, 60_000).Select(index => new TemplateEvent(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = index * 2L,
            Number = index % 2 == 0 ? 1 : 11,
            Value = index % 128
        }));
        TemplateEventQuerySnapshot values = voice.Events.CreateQuerySnapshot();
        TimelineRenderSnapshot snapshot = new(
            1,
            "subvoice-event-lane-range",
            [],
            ["CC 1"],
            itemSource: new PagedTemplateEventLaneTimelineItemSource(
                voice,
                values,
                MidiValueTarget.ControlChange(1)));
        List<TimelineRenderItem> items = [];

        snapshot.QueryInto(20_000, 20_200, 0, 1, items);

        Assert.Equal(50, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(TimelineItemKind.LogicalParameterPoint, item.Kind);
            Assert.InRange(item.StartTick, 20_000, 20_199);
            Assert.Equal(0, item.StartTick % 4);
        });
        Assert.Empty(snapshot.Items);
        Assert.True(snapshot.TotalItemCount >= 60_000);
    }

    [Fact]
    public void SubVoiceEventLaneRangeFingerprintIgnoresOtherTargets()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        TemplateEvent active = new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = 100,
            Number = 1,
            Value = 40
        };
        TemplateEvent unrelated = new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = 120,
            Number = 11,
            Value = 80
        };
        voice.Events.AddRange([active, unrelated]);
        ulong before = Source().GetRangeFingerprint(0, 200, 0, 1);

        unrelated.Value = 90;
        ulong afterUnrelatedEdit = Source().GetRangeFingerprint(0, 200, 0, 1);
        active.Value = 41;
        ulong afterActiveEdit = Source().GetRangeFingerprint(0, 200, 0, 1);

        Assert.Equal(before, afterUnrelatedEdit);
        Assert.NotEqual(before, afterActiveEdit);

        PagedTemplateEventLaneTimelineItemSource Source() => new(
            voice,
            voice.Events.CreateQuerySnapshot(),
            MidiValueTarget.ControlChange(1));
    }

    [Fact]
    public void DirectMidiEventLaneLargeSourceKeepsRangeQueriesBounded()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 200_000 };
        segment.ChannelEvents.AddRange(Enumerable.Range(0, 60_000).Select(index =>
            new DirectMidiChannelEvent(project)
            {
                Tick = index * 2L,
                Kind = DirectMidiChannelEventKind.ControlChange,
                Data1 = index % 2 == 0 ? 1 : 11,
                Data2 = index % 128
            }));
        TimelineRenderSnapshot snapshot = new(
            1,
            "direct-event-lane-range",
            [],
            ["CC 1"],
            itemSource: new PagedDirectMidiTimelineItemSource(
                segment,
                DirectMidiTimelineProjection.ChannelEvents,
                new DirectMidiEventLaneTarget(DirectMidiChannelEventKind.ControlChange, 1)));
        List<TimelineRenderItem> items = [];

        snapshot.QueryInto(20_000, 20_200, 0, 1, items);

        Assert.Equal(50, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal(TimelineItemKind.DirectMidiEvent, item.Kind);
            Assert.InRange(item.StartTick, 20_000, 20_199);
            Assert.Equal(0, item.StartTick % 4);
        });
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void SubVoiceEventDiscoveryKeysTrackExactTargetPresence()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        TemplateEvent cc1 = new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = 10,
            Number = 1,
            Value = 64
        };
        TemplateEvent cc11 = new(project)
        {
            Kind = TemplateEventKind.ControlChange,
            Tick = 20,
            Number = 11,
            Value = 96
        };
        voice.Events.AddRange([cc1, cc11]);

        MidiValueTarget[] before = voice.Events.CreateQuerySnapshot().DiscoveryKeys
            .Select(key => TemplateEventMidiTargets.TryDecodeDiscoveryKey(
                    key,
                    out MidiValueTarget target)
                ? target
                : throw new InvalidOperationException("Invalid event discovery key."))
            .OrderBy(static value => value.Kind)
            .ThenBy(static value => value.Number)
            .ToArray();
        Assert.Equal(
            [MidiValueTarget.ControlChange(1), MidiValueTarget.ControlChange(11)],
            before);

        Assert.True(voice.Events.Remove(cc11));
        MidiValueTarget[] after = voice.Events.CreateQuerySnapshot().DiscoveryKeys
            .Select(key => TemplateEventMidiTargets.TryDecodeDiscoveryKey(
                    key,
                    out MidiValueTarget target)
                ? target
                : throw new InvalidOperationException("Invalid event discovery key."))
            .ToArray();

        Assert.Equal([MidiValueTarget.ControlChange(1)], after);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("subvoice")]
    public void DenseTargetSpecificEventRasterWorkIsBoundedByPixels(string sourceKind)
    {
        const int eventCount = 60_000;
        using MidoraProject project = new(192);
        TimelineRenderSnapshot snapshot;
        if (sourceKind == "direct")
        {
            MidiSegment segment = new(project) { LengthTicks = eventCount * 2L + 2 };
            segment.ChannelEvents.AddRange(Enumerable.Range(0, eventCount).Select(index =>
                new DirectMidiChannelEvent(project)
                {
                    Tick = index * 2L,
                    Kind = DirectMidiChannelEventKind.ControlChange,
                    Data1 = index % 2 == 0 ? 1 : 11,
                    Data2 = index % 128
                }));
            DirectMidiChannelEventQuerySnapshot values =
                segment.ChannelEvents.CreateQuerySnapshot();
            TimelineEventTargetIndex<DirectMidiEventLaneTarget> index =
                TimelineEventTargetIndex<DirectMidiEventLaneTarget>.Build(
                    values.QueryValues(0, long.MaxValue).Select(static value => (
                        new DirectMidiEventLaneTarget(value.Kind, value.Data1),
                        new TimelineEventTargetPoint(
                            value.Id,
                            value.Tick,
                            value.Data2 / 127d))),
                    Comparer<DirectMidiEventLaneTarget>.Create(static (left, right) =>
                    {
                        int kind = left.Kind.CompareTo(right.Kind);
                        return kind != 0 ? kind : left.Data1.CompareTo(right.Data1);
                    }));
            Assert.True(index.TryGetLane(
                new(DirectMidiChannelEventKind.ControlChange, 1),
                out TimelineEventTargetLaneIndex? lane));
            snapshot = new(
                1,
                "direct-target-aggregate",
                [],
                itemSource: new PagedDirectMidiTimelineItemSource(
                    segment,
                    DirectMidiTimelineProjection.ChannelEvents,
                    new(DirectMidiChannelEventKind.ControlChange, 1),
                    channelEventSnapshot: values,
                    eventIndex: lane));
        }
        else
        {
            SubVoice voice = new(project);
            voice.Events.AddRange(Enumerable.Range(0, eventCount).Select(index =>
                new TemplateEvent(project)
                {
                    Kind = TemplateEventKind.ControlChange,
                    Tick = index * 2L,
                    Number = index % 2 == 0 ? 1 : 11,
                    Value = index % 128
                }));
            TemplateEventQuerySnapshot values = voice.Events.CreateQuerySnapshot();
            TimelineEventTargetIndex<MidiValueTarget> index =
                TimelineEventTargetIndex<MidiValueTarget>.Build(
                    values.QueryEvents(0, long.MaxValue).Select(static value => (
                        MidiValueTarget.ControlChange(value.Number),
                        new TimelineEventTargetPoint(
                            value.Id,
                            value.Tick,
                            value.Value / 127d))),
                    Comparer<MidiValueTarget>.Create(static (left, right) =>
                    {
                        int kind = left.Kind.CompareTo(right.Kind);
                        return kind != 0 ? kind : left.Number.CompareTo(right.Number);
                    }));
            Assert.True(index.TryGetLane(
                MidiValueTarget.ControlChange(1),
                out TimelineEventTargetLaneIndex? lane));
            snapshot = new(
                1,
                "subvoice-target-aggregate",
                [],
                itemSource: new PagedTemplateEventLaneTimelineItemSource(
                    voice,
                    values,
                    MidiValueTarget.ControlChange(1),
                    targetIndex: lane));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        TimelineRasterBuffer raster = TimelineEventPointTileRasterizer.Rasterize(
            snapshot,
            selection: null,
            devicePixelsPerTick: 0.001,
            devicePixelsPerValue: 256,
            tileX: 0,
            tileY: 0,
            dpiScaleX: 1,
            dpiScaleY: 1,
            normalColor: Colors.SlateGray,
            primaryColor: Colors.White,
            borderColor: Colors.Black);
        stopwatch.Stop();

        Assert.InRange(
            raster.CandidateCount,
            1,
            TimelineEventPointTileRasterizer.GetRasterSize(1) * 4);
        Assert.Contains(raster.Pixels, static value => value != 0);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"{sourceKind} target-specific raster took "
            + $"{stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }
}
