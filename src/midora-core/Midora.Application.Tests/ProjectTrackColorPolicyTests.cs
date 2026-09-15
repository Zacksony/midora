using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectTrackColorPolicyTests
{
    [Fact]
    public void NewPureMidiTracksUseTheGlobalArrangementPaletteAndWrap()
    {
        MidoraProject project = new(480);
        LogicalTrack logical = new(project) { Name = "Logical" };
        project.Tracks.Add(logical);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, logical.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);

        for (int index = 0; index < 9; index++)
        {
            document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
                $"MIDI {index + 1}"));
        }

        Assert.Equal(
            Enumerable.Range(0, 9)
                .Select(index => (MidoraColor?)ProjectTrackColorPolicy.PureMidiTrackPalette[
                    index % ProjectTrackColorPolicy.PureMidiTrackPalette.Count]),
            project.PureMidiTracksInArrangementOrder().Select(track => track.Color));
    }

    [Fact]
    public void ExistingAutoRootCreationUsesItsResolvedArrangementPosition()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("First"));
        PureMidiTrack first = Assert.Single(project.PureMidiTracks);
        LogicalTrack logical = new(project) { Name = "Logical" };
        project.Tracks.Add(logical);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.LogicalTrack, logical.Id));

        document.Execute(ProjectDomainEditCommands.CreatePureMidiTrack(
            first.MidiChannelRootId,
            "Second",
            insertionIndex: project.ArrangementTracks.Count));

        Assert.Equal(
            [first.Id, project.PureMidiTracks.Single(track => track.Id != first.Id).Id, logical.Id],
            project.TracksInArrangementOrder().Select(track => track.TrackId));
        Assert.Equal(ProjectTrackColorPolicy.PureMidiTrackPalette[1],
            project.PureMidiTracks.Single(track => track.Id != first.Id).Color);
    }

    [Fact]
    public void DuplicateInheritsItsSourceColorAndTheNextNewTrackContinuesByGlobalPosition()
    {
        MidoraProject project = new(480);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        for (int index = 0; index < 9; index++)
        {
            document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot(
                $"MIDI {index + 1}"));
        }
        PureMidiTrack source = project.PureMidiTracks[0];

        document.Execute(ProjectDomainEditCommands.DuplicatePureMidiTrack(source.Id));
        PureMidiTrack duplicate = project.PureMidiTracks[^1];
        document.Execute(ProjectDomainEditCommands.CreatePureMidiTrackWithNewRoot("After Duplicate"));
        PureMidiTrack next = project.PureMidiTracks[^1];

        Assert.Equal(source.Color, duplicate.Color);
        Assert.Equal(ProjectTrackColorPolicy.PureMidiTrackPalette[2], next.Color);
    }

    [Fact]
    public void MaterializedAndStreamingMidiImportsUseTheSameDeterministicPalette()
    {
        byte[] bytes = BuildTenTrackMidi();
        MidiProjectImportResult? materialized = null;
        MidiProjectImportResult? streamed = null;
        string directory = Path.Combine(
            Path.GetTempPath(),
            "midora-track-color-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "colors.mid");
        try
        {
            File.WriteAllBytes(path, bytes);
            materialized = MidiProjectImportService.Import(bytes, "Colors");
            streamed = MidiProjectImportService.ImportFile(path, "Colors");
            MidoraColor?[] expected = Enumerable.Range(0, 10)
                .Select(index => (MidoraColor?)ProjectTrackColorPolicy.PureMidiTrackPalette[
                    index % ProjectTrackColorPolicy.PureMidiTrackPalette.Count])
                .ToArray();

            Assert.Equal(
                expected,
                materialized.Project.PureMidiTracksInArrangementOrder()
                    .Select(track => track.Color)
                    .ToArray());
            Assert.Equal(
                expected,
                streamed.Project.PureMidiTracksInArrangementOrder()
                    .Select(track => track.Color)
                    .ToArray());
        }
        finally
        {
            materialized?.Project.Dispose();
            streamed?.Project.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] BuildTenTrackMidi()
    {
        List<StandardMidiFileTrack> tracks =
        [
            new(
                120,
                [
                    StandardMidiFileEvent.Meta(
                        0,
                        StandardMidiFile.SetTempoMetaType,
                        [0x07, 0xa1, 0x20])
                ])
        ];
        for (int index = 0; index < 10; index++)
        {
            byte channel = checked((byte)(index % 16));
            tracks.Add(new(
                120,
                [
                    StandardMidiFileEvent.Text(
                        0,
                        StandardMidiFile.TrackNameMetaType,
                        $"Track {index + 1}"),
                    StandardMidiFileEvent.ChannelVoice(
                        0,
                        MidiMessage.NoteOn(channel, 60, 100)),
                    StandardMidiFileEvent.ChannelVoice(
                        120,
                        MidiMessage.NoteOff(channel, 60, 0))
                ]));
        }
        return StandardMidiFile.EncodeType1(480, tracks);
    }
}
