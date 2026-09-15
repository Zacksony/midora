using Midora.Compiler;
using Midora.Domain;
using Midora.Playback;

namespace Midora.Application.Tests;

public sealed class ProjectMetadataAndDisplayEditCommandsTests
{
    [Fact]
    public void UserMetadataUpdateIsAtomicPreservesSystemFieldsAndUndoRestoresExactly()
    {
        MidoraProject project = CreateProject(out _);
        ProjectMetadataSnapshot initial = project.Metadata.Snapshot();
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long fingerprint = compilation.LastAttempt.Fingerprint;

        document.Execute(ProjectDomainEditCommands.UpdateProjectMetadata(
            "  Project Name  ",
            " v1 beta ",
            "Author Team",
            "Original Work",
            "Copyright line"));

        Assert.Equal("  Project Name  ", project.Metadata.ProjectName);
        Assert.Equal(" v1 beta ", project.Metadata.ProjectVersion);
        Assert.Equal("Author Team", project.Metadata.AuthorOrTeam);
        Assert.Equal("Original Work", project.Metadata.OriginalWork);
        Assert.Equal("Copyright line", project.Metadata.Copyright);
        Assert.Equal(initial.CreatedAtUtc, project.Metadata.CreatedAtUtc);
        Assert.Equal(initial.ModifiedAtUtc, project.Metadata.ModifiedAtUtc);
        Assert.Equal(fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
        Assert.True(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();

        ProjectMetadataSnapshot restored = project.Metadata.Snapshot();
        Assert.Equal(initial.ProjectName, restored.ProjectName);
        Assert.Equal(initial.ProjectVersion, restored.ProjectVersion);
        Assert.Equal(initial.AuthorOrTeam, restored.AuthorOrTeam);
        Assert.Equal(initial.OriginalWork, restored.OriginalWork);
        Assert.Equal(initial.Copyright, restored.Copyright);
        Assert.Equal(initial.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(initial.ModifiedAtUtc, restored.ModifiedAtUtc);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void MetadataValidationUsesPersistenceScalarAndControlContracts()
    {
        MidoraProject project = CreateProject(out _);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        string tooLongShortText = string.Concat(Enumerable.Repeat("😀", 257));
        string tooLongMetadataText = new('a', 4_097);

        Assert.Throws<ArgumentException>(() => document.Execute(
            Metadata(projectName: tooLongShortText)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            Metadata(authorOrTeam: tooLongMetadataText)));
        Assert.Throws<ArgumentException>(() => document.Execute(
            Metadata(projectVersion: "invalid\nversion")));
        Assert.Throws<ArgumentException>(() => document.Execute(
            Metadata(copyright: "invalid\0copyright")));
        Assert.Throws<ArgumentException>(() => document.Execute(
            Metadata(originalWork: "\ud800")));
        Assert.Throws<ArgumentNullException>(() => document.Execute(
            ProjectDomainEditCommands.UpdateProjectMetadata(
                "", "", null!, "", "")));

        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LogicalTrackColorOverrideCanBeSetClearedAndUndoneWithoutTrackRecompile()
    {
        MidoraProject project = CreateProject(out LogicalTrack track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        long fingerprint = compilation.LastAttempt.Fingerprint;
        MidoraColor color = new(1, 2, 3);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalTrackColorOverride(
            track.Id,
            color));

        Assert.Equal(color, track.ColorOverride);
        Assert.Equal(fingerprint, compilation.LastAttempt.Fingerprint);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalTrackColorOverride(
            track.Id,
            colorOverride: null));

        Assert.Null(track.ColorOverride);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
        AssertCurrentCompilationMatchesFull(compilation);

        document.Undo();
        Assert.Equal(color, track.ColorOverride);
        document.Undo();
        Assert.Null(track.ColorOverride);
        Assert.False(document.IsModified);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void PureMidiTrackColorCanBeSetClearedAndUndoneWithoutTrackRecompile()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI Track",
            MidiChannelRootId = root.Id
        };
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;
        MidoraColor color = new(4, 5, 6);

        document.Execute(ProjectDomainEditCommands.UpdatePureMidiTrackColor(track.Id, color));

        Assert.Equal(color, track.Color);
        Assert.Same(before, compilation.LastAttempt);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
        ProjectEditExecution noOp = document.Execute(
            ProjectDomainEditCommands.UpdatePureMidiTrackColor(track.Id, color));
        Assert.False(noOp.Changed);

        document.Execute(ProjectDomainEditCommands.UpdatePureMidiTrackColor(track.Id, color: null));

        Assert.Null(track.Color);
        Assert.Same(before, compilation.LastAttempt);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
        document.Undo();
        Assert.Equal(color, track.Color);
        document.Undo();
        Assert.Null(track.Color);
        document.Redo();
        Assert.Equal(color, track.Color);
        AssertCurrentCompilationMatchesFull(compilation);
    }

    [Fact]
    public void LogicalTrackPropertiesCommitNameAndOverrideAsOneUndo()
    {
        MidoraProject project = CreateProject(out LogicalTrack track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        MidoraColor color = new(7, 8, 9);

        document.Execute(ProjectDomainEditCommands.UpdateLogicalTrackProperties(
            track.Id,
            "Renamed",
            color));

        Assert.Equal("Renamed", track.Name);
        Assert.Equal(color, track.ColorOverride);
        Assert.Single(document.History);
        document.Undo();
        Assert.Equal("Track", track.Name);
        Assert.Null(track.ColorOverride);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void PureMidiTrackPropertiesColorOnlyCommitDoesNotRecompile()
    {
        MidoraProject project = new(480);
        MidiChannelRoot root = new(project)
        {
            Name = "Root",
            RoutingMode = MidiChannelRootRoutingMode.Auto,
            ChannelMode = MidiChannelMode.Melodic
        };
        PureMidiTrack track = new(project)
        {
            Name = "MIDI Track",
            MidiChannelRootId = root.Id
        };
        project.MidiChannelRoots.Add(root);
        project.PureMidiTracks.Add(track);
        project.ArrangementTracks.Add(new(ArrangementTrackKind.PureMidiTrack, track.Id));
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;
        MidoraColor color = new(10, 11, 12);

        document.Execute(ProjectDomainEditCommands.UpdatePureMidiTrackProperties(
            track.Id,
            track.Name,
            color));

        Assert.Equal(color, track.Color);
        Assert.Same(before, compilation.LastAttempt);
        Assert.Equal(0, compilation.LastCompilationTelemetry.RecompiledTrackCount);
        Assert.Single(document.History);
        document.Undo();
        Assert.Null(track.Color);
        Assert.Same(before, compilation.LastAttempt);
    }

    [Fact]
    public void IdenticalMetadataAndTrackColorAreNoOperations()
    {
        MidoraProject project = CreateProject(out LogicalTrack track);
        using ProjectCompilationSession compilation = new(project);
        ProjectDocumentSession document = new(compilation, ProjectDocumentOrigin.Persisted);
        CanonicalCompiledResult before = compilation.LastAttempt;

        ProjectEditExecution metadata = document.Execute(Metadata());
        ProjectEditExecution color = document.Execute(
            ProjectDomainEditCommands.UpdateLogicalTrackColorOverride(
                track.Id,
                colorOverride: null));

        Assert.False(metadata.Changed);
        Assert.False(color.Changed);
        Assert.Same(before, compilation.LastAttempt);
        Assert.False(document.CanUndo);
        Assert.False(document.IsModified);
    }

    private static IProjectEditCommand Metadata(
        string projectName = "",
        string projectVersion = "",
        string authorOrTeam = "",
        string originalWork = "",
        string copyright = "") =>
        ProjectDomainEditCommands.UpdateProjectMetadata(
            projectName,
            projectVersion,
            authorOrTeam,
            originalWork,
            copyright);

    private static MidoraProject CreateProject(out LogicalTrack track)
    {
        MidoraProject project = new(480, new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero));
        track = new LogicalTrack(project) { Name = "Track" };
        project.Tracks.Add(track);
        return project;
    }

    private static void AssertCurrentCompilationMatchesFull(ProjectCompilationSession compilation)
    {
        using MidoraCompiler compiler = new();
        CanonicalCompiledResult expected = compiler.CompileFull(compilation.Project);
        CanonicalCompiledResult actual = compilation.LastAttempt;
        Assert.Equal(expected.IsConsumable, actual.IsConsumable);
        Assert.Equal(expected.IsPartial, actual.IsPartial);
        Assert.Equal(expected.FailureStage, actual.FailureStage);
        Assert.Equal(expected.StartTick, actual.StartTick);
        Assert.Equal(expected.EndTick, actual.EndTick);
        Assert.Equal(expected.Fingerprint, actual.Fingerprint);
        Assert.Equal(expected.Statistics, actual.Statistics);
        Assert.Equal(expected.Events.ToArray(), actual.Events.ToArray());
        Assert.Equal(expected.Allocations.ToArray(), actual.Allocations.ToArray());
        Assert.Equal(expected.Diagnostics, actual.Diagnostics);
    }
}
