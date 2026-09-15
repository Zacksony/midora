using System.Text;
using Midora.Audio;
using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class StreamingMidiProjectImportTests
{
    [Fact]
    public void ImportFileUsesPagedContentAndPreservesChannelSemantics()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-streaming-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "source.mid");
        MidiProjectImportResult? result = null;
        try
        {
            byte[] bytes = StandardMidiFile.EncodeType1(
                192,
                [
                    new StandardMidiFileTrack(
                        384,
                        [StandardMidiFileEvent.Meta(0, StandardMidiFile.SetTempoMetaType, [0x07, 0xa1, 0x20])]),
                    new StandardMidiFileTrack(
                        384,
                        [
                            StandardMidiFileEvent.Text(0, StandardMidiFile.TrackNameMetaType, "Piano"),
                            StandardMidiFileEvent.Meta(0, StandardMidiFile.MidiPortMetaType, [0]),
                            StandardMidiFileEvent.ChannelVoice(0, MidiMessage.ProgramChange(0, 4)),
                            StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                            StandardMidiFileEvent.ChannelVoice(96, MidiMessage.NoteOff(0, 60, 23)),
                            StandardMidiFileEvent.Meta(100, 0x01, [1, 2, 3])
                        ])
                ]);
            File.WriteAllBytes(path, bytes);

            RecordingProgress progress = new();
            result = MidiProjectImportService.ImportFile(
                path,
                "Song",
                progress: progress);

            PureMidiTrack track = Assert.Single(result.Project.PureMidiTracks);
            MidiSegment segment = Assert.Single(track.Segments);
            Assert.True(segment.UsesPagedContent);
            DirectMidiNote note = Assert.Single(segment.Notes);
            Assert.Equal(60, note.Key);
            Assert.Equal(23, note.NoteOffVelocity);
            Assert.Equal(4, Assert.Single(segment.ChannelEvents).Data1);
            Assert.Equal([1, 2, 3], Assert.Single(segment.OpaqueEvents).Payload);
            Assert.NotNull(result.Metrics);
            Assert.Equal(1, result.Metrics.ImportedNoteCount);
            Assert.True(result.Metrics.ContentPageCount >= 3);
            Assert.True(result.Metrics.ContentPackBytes > 0);
            Assert.Equal(MidiProjectImportPhase.ScanningSource, progress.Values[0].Phase);
            Assert.Equal(0, progress.Values[0].Fraction);
            Assert.Contains(progress.Values, value =>
                value.Phase == MidiProjectImportPhase.ScanningSource
                && value.ProcessedEventCount == value.TotalEventCount
                && value.Fraction == 0.15);
            Assert.Contains(progress.Values, value =>
                value.Phase == MidiProjectImportPhase.ImportingEvents
                && value.ProcessedEventCount == value.TotalEventCount
                && value.TotalEventCount == result.Metrics.ScannedEventCount
                && value.Fraction == 0.95);
            Assert.True(progress.Values
                .Select(value => value.Fraction)
                .SequenceEqual(progress.Values
                    .Select(value => value.Fraction)
                    .Order()));
            Assert.Equal(
                MidiProjectImportPhase.FinalizingProject,
                progress.Values[^1].Phase);
            Assert.Equal(0.98, progress.Values[^1].Fraction);
        }
        finally
        {
            result?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportFileNormalizesDuplicateConductorStatesBeforeValidation()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-streaming-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "duplicates.mid");
        MidiProjectImportResult? result = null;
        try
        {
            File.WriteAllBytes(path, StandardMidiFile.EncodeType1(
                480,
                [
                    new StandardMidiFileTrack(
                        120,
                        [
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.SetTempoMetaType,
                                [0x07, 0xa1, 0x20]),
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.TimeSignatureMetaType,
                                [4, 2, 24, 8]),
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.KeySignatureMetaType,
                                [0, 0])
                        ]),
                    new StandardMidiFileTrack(
                        120,
                        [
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.TimeSignatureMetaType,
                                [3, 2, 24, 8]),
                            StandardMidiFileEvent.Meta(
                                0,
                                StandardMidiFile.KeySignatureMetaType,
                                [1, 1]),
                            StandardMidiFileEvent.ChannelVoice(0, MidiMessage.NoteOn(0, 60, 100)),
                            StandardMidiFileEvent.ChannelVoice(120, MidiMessage.NoteOff(0, 60, 0))
                        ])
                ]));

            result = MidiProjectImportService.ImportFile(path, "Duplicates");

            TimeSignatureChange timeSignature = Assert.Single(
                result.Project.Conductor.TimeSignatures);
            Assert.Equal((3, 4), (timeSignature.Numerator, timeSignature.Denominator));
            KeySignatureChange keySignature = Assert.Single(
                result.Project.Conductor.KeySignatures);
            Assert.Equal((1, true), (keySignature.SharpsFlats, keySignature.IsMinor));
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-DUPLICATE-TIME-SIGNATURE");
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-DUPLICATE-KEY-SIGNATURE");
        }
        finally
        {
            result?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportFileAcceptsWindows31JTrackNamesAndMarkers()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-streaming-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "windows-31j.mid");
        MidiProjectImportResult? result = null;
        try
        {
            File.WriteAllBytes(path, StandardMidiFile.EncodeType1(
                480,
                [new StandardMidiFileTrack(
                    120,
                    [
                        StandardMidiFileEvent.Meta(
                            0,
                            StandardMidiFile.TrackNameMetaType,
                            EncodeWindows31J("メロディ")),
                        StandardMidiFileEvent.Meta(
                            0,
                            StandardMidiFile.MarkerMetaType,
                            EncodeWindows31J("イントロ")),
                        StandardMidiFileEvent.ChannelVoice(
                            0,
                            MidiMessage.NoteOn(0, 60, 100)),
                        StandardMidiFileEvent.Meta(
                            60,
                            StandardMidiFile.MarkerMetaType,
                            [0x81]),
                        StandardMidiFileEvent.ChannelVoice(
                            120,
                            MidiMessage.NoteOff(0, 60, 0))
                    ])]));

            result = MidiProjectImportService.ImportFile(path, "Japanese Text Compatibility");

            Assert.Equal("メロディ", Assert.Single(result.Project.PureMidiTracks).Name);
            ProjectMarker marker = Assert.Single(result.Project.Conductor.Markers);
            Assert.Equal((0L, "イントロ"), (marker.Tick, marker.Name));
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-WINDOWS-31J-TRACK-NAME");
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-WINDOWS-31J-MARKER");
            Assert.Contains(result.Diagnostics, value =>
                value.Code == "MIDORA-MIDI-IMPORT-INVALID-MARKER"
                && value.Severity == DiagnosticSeverity.Warning
                && value.Tick == 60);
        }
        finally
        {
            result?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportFileAssignsChannelModeSystemExclusiveToTargetChannel()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-streaming-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "channel-mode.mid");
        MidiProjectImportResult? result = null;
        try
        {
            File.WriteAllBytes(path, StandardMidiFile.EncodeType1(
                480,
                [new StandardMidiFileTrack(
                    120,
                    [
                        StandardMidiFileEvent.ChannelVoice(0, MidiMessage.ProgramChange(0, 4)),
                        StandardMidiFileEvent.SystemExclusive(
                            24,
                            [0x43, 0x10, 0x4c, 0x08, 0x01, 0x07, 0x01, 0xf7])
                    ])]));

            result = MidiProjectImportService.ImportFile(path, "Channel Mode");

            PureMidiTrack owner = Assert.Single(result.Project.PureMidiTracks, track =>
                track.Segments.SelectMany(segment => segment.OpaqueEvents).Any());
            MidiChannelRoot root = result.Project.MidiChannelRoots.Single(value =>
                value.Id == owner.MidiChannelRootId);
            Assert.Equal(2, result.Project.PureMidiTracks.Count);
            Assert.Equal((byte)1, root.FixedZeroBasedChannel);
            using MidoraCompiler compiler = new();
            CanonicalCompiledResult compiled = compiler.CompileFull(result.Project);
            Assert.True(compiled.HasPagedEvents);
            MidiRenderPlan plan = MidiRenderPlanAdapter.CreateRealtime(compiled, 48_000);
            IMidiRenderEventPageProvider provider = Assert.IsAssignableFrom<
                IMidiRenderEventPageProvider>(plan.EventPageProvider);
            ScheduledPortMidiMessage mode = Assert.Single(
                provider.Query(0, plan.TotalFrameCount),
                value => value.Scheduled.IsChannelModeSystemExclusive);
            Assert.Equal((byte)1, mode.Scheduled.Message.ChannelNumber);
            Assert.DoesNotContain(
                plan.UnitFragments.ToArray().SelectMany(value => value.Events.ToArray()),
                value => value.IsChannelModeSystemExclusive);
        }
        finally
        {
            result?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingProgress : IProgress<MidiProjectImportProgress>
    {
        public List<MidiProjectImportProgress> Values { get; } = [];

        public void Report(MidiProjectImportProgress value) => Values.Add(value);
    }

    private static byte[] EncodeWindows31J(string value) =>
        (CodePagesEncodingProvider.Instance.GetEncoding(932)
            ?? throw new InvalidOperationException("Windows-31J encoding is unavailable."))
        .GetBytes(value);
}
