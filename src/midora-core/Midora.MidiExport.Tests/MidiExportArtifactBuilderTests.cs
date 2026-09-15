using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using Midora.OutputPlanning;
using System.Reflection;

namespace Midora.MidiExport.Tests;

public sealed class MidiExportArtifactBuilderTests
{
    [Fact]
    public void BuildsExactlyTheFrozenArtifactsForAllThreeModes()
    {
        (MidoraProject project, LogicalTrack track) = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult compiled = compiler.CompileFull(project, new CompilationRequest
        {
            Purpose = CompilationPurpose.MidiExport,
            StartTick = 0,
            EndTick = 192
        });
        string output = Path.Combine(Path.GetTempPath(), $"midora-artifact-plan-{Guid.NewGuid():N}");
        MidiExportLogicalTrackLayout layout = new(
            track.Id,
            new Dictionary<MidiExportChannelUnit, string>
            {
                [new(0, 0)] = "Port 1 / Channel 1"
            });

        MidiExportFrozenOutputPlan wholePlan = MidiExportOutputPlanner.PlanWholeProject(
            output,
            "Project",
            null,
            includeReadme: false);
        MidiExportArtifactBuildResult whole = MidiExportArtifactBuilder.BuildWholeProject(
            wholePlan,
            new()
            {
                CompiledResult = compiled,
                ConductorTrackName = "Conductor",
                LogicalTracks = [layout]
            });

        MidiExportFrozenOutputPlan trackPlan = MidiExportOutputPlanner.PlanLogicalTracks(
            output,
            [new LogicalTrackOutputName(track.Id.ToString(), 1, track.Name)],
            1,
            includeReadme: false);
        MidiExportArtifactBuildResult perTrack = MidiExportArtifactBuilder.BuildLogicalTracks(
            trackPlan,
            [new(track.Id.ToString(), new()
            {
                CompiledResult = compiled,
                ConductorTrackName = "Conductor",
                LogicalTrack = layout
            })]);

        MidiExportFrozenOutputPlan portPlan = MidiExportOutputPlanner.PlanPorts(
            output,
            [1],
            includeReadme: false);
        MidiExportArtifactBuildResult perPort = MidiExportArtifactBuilder.BuildPorts(
            portPlan,
            [new(new()
            {
                CompiledResult = compiled,
                ConductorTrackName = "Conductor",
                LogicalTracks = [layout],
                ZeroBasedOriginalPort = 0
            })]);

        AssertArtifactSet(whole, "whole-project-midi");
        AssertArtifactSet(perTrack, "logical-track:" + track.Id);
        AssertArtifactSet(perPort, "port:01");
    }

    [Fact]
    public void EncodingFailureProducesNoPartialArtifactSet()
    {
        (MidoraProject project, LogicalTrack track) = CreateProject();
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult playbackResult = compiler.CompileFull(project);
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            Path.Combine(Path.GetTempPath(), $"midora-artifact-fail-{Guid.NewGuid():N}"),
            "Project",
            null,
            includeReadme: false);

        MidiExportArtifactBuildResult result = MidiExportArtifactBuilder.BuildWholeProject(
            plan,
            new()
            {
                CompiledResult = playbackResult,
                ConductorTrackName = "Conductor",
                LogicalTracks = [new(track.Id, new Dictionary<MidiExportChannelUnit, string>
                {
                    [new(0, 0)] = "Port 1 / Channel 1"
                })]
            });

        Assert.False(result.Succeeded);
        Assert.Empty(result.Artifacts);
        Assert.Contains(result.Diagnostics, item =>
            item.Diagnostic.Code == "MIDORA-MIDI-EXPORT-CONTEXT");
    }

    [Fact]
    public void ReadmeArtifactFreezesItsSnapshotAndWritesLazilyWithCancellation()
    {
        (MidoraProject project, _) = CreateProject();
        using MidoraCompiler compiler = new();
        MidiExportCompilationResult compilation = new MidiExportCompilationCoordinator(compiler).Compile(new()
        {
            Project = project,
            Mode = MidiExportMode.WholeProject,
            Routing = MidiExportRoutingStrategy.Compact,
            EndTick = 192
        });
        MidiExportFrozenOutputPlan plan = MidiExportOutputPlanner.PlanWholeProject(
            Path.Combine(Path.GetTempPath(), "midora-readme-streaming-artifact"),
            "Project", null, includeReadme: true);
        MidiExportReadmeRequest readme = MidiExportReadmeFactory.Create(project, compilation, plan,
            MidiExportRangeSource.Manual, "0.1", "0.1", "0.1", DateTimeOffset.UnixEpoch);
        byte[] expected = MidiExportReadmeBuilder.Build(readme);
        MidiExportArtifactBuildResult artifacts = MidiExportArtifactBuilder.BuildWholeProject(plan,
            new()
            {
                CompiledResult = compilation.CompiledResult,
                ConductorTrackName = compilation.ConductorTrackName,
                LogicalTracks = compilation.Layouts
            }, readme);
        MidiExportPreparedArtifact artifact = Assert.Single(artifacts.Artifacts, value => value.SourceKey == "readme");
        foreach (MidiExportPreparedArtifact midi in artifacts.Artifacts.Where(value => value.SourceKey != "readme"))
        {
            using MemoryStream encoded = new();
            midi.WriteTo(encoded);
        }
        FieldInfo content = typeof(MidiExportPreparedArtifact).GetField("_content",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(content.GetValue(artifact));
        ((string[])readme.FileNames)[0] = "MUTATED.md";
        using MemoryStream output = new();
        artifact.WriteTo(output);
        Assert.Equal(expected, output.ToArray());
        Assert.Null(content.GetValue(artifact));
        using MemoryStream canceled = new();
        Assert.Throws<OperationCanceledException>(() => artifact.WriteTo(canceled, new CancellationToken(true)));
        Assert.Equal(0, canceled.Length);
    }

    private static void AssertArtifactSet(MidiExportArtifactBuildResult result, string sourceKey)
    {
        Assert.True(result.Succeeded);
        MidiExportPreparedArtifact artifact = Assert.Single(result.Artifacts);
        Assert.Equal(sourceKey, artifact.SourceKey);
        StandardMidiFile.ValidateType1(artifact.Content.Span);
    }

    private static (MidoraProject Project, LogicalTrack Track) CreateProject()
    {
        MidoraProject project = new(192);
        EventInstrument instrument = new(project)
        {
            Name = "Instrument",
            RootNote = 60,
            TemplateLengthTicks = 192,
            OverlapPolicy = OverlapPolicy.Warn
        };
        SubVoice voice = new(project) { Name = "Voice" };
        voice.Events.Add(TemplateEvent.Note(project, 0, 96, 60, 100));
        instrument.SubVoices.Add(voice);
        project.EventInstruments.Add(instrument);
        LogicalTrack track = new(project) { Name = "Track"};
        ProjectGraphConstruction.AddIndependentLogicalTrack(project, track, instrument.Id);
        Segment segment = new(project) { LengthTicks = 192 };
        segment.Notes.Add(new(project)
        {
            StartTick = 0,
            LengthTicks = 192,
            Note = 60,
            Velocity = 100
        });
        track.Segments.Add(segment);
        return (project, track);
    }
}
