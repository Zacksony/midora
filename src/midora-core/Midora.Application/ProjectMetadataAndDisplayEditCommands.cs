using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand UpdateProjectMetadata(
        string projectName,
        string projectVersion,
        string authorOrTeam,
        string originalWork,
        string copyright) =>
        Command("Change project metadata", project =>
        {
            ProjectUserMetadata replacement = new(
                ProjectTextRules.ValidateShortTextContent(
                    projectName,
                    nameof(projectName)),
                ProjectTextRules.ValidateShortTextContent(
                    projectVersion,
                    nameof(projectVersion)),
                ProjectTextRules.ValidateMetadataText(
                    authorOrTeam,
                    nameof(authorOrTeam)),
                ProjectTextRules.ValidateMetadataText(
                    originalWork,
                    nameof(originalWork)),
                ProjectTextRules.ValidateMetadataText(
                    copyright,
                    nameof(copyright)));
            ProjectUserMetadata old = SnapshotUserMetadata(project.Metadata);
            return Prepared(
                old != replacement,
                NoCompilationChange(),
                _ => RestoreUserMetadata(project.Metadata, replacement),
                _ => RestoreUserMetadata(project.Metadata, old));
        });

    public static IProjectEditCommand UpdateLogicalTrackColorOverride(
        MidoraId trackId,
        MidoraColor? colorOverride) =>
        Command("Change logical track color", project =>
        {
            LogicalTrack track = FindLogicalTrack(project, trackId);
            MidoraColor? oldColor = track.ColorOverride;
            return Prepared(
                oldColor != colorOverride,
                TrackPresentationChange(trackId),
                _ => track.ColorOverride = colorOverride,
                _ => track.ColorOverride = oldColor);
        });

    public static IProjectEditCommand UpdatePureMidiTrackColor(
        MidoraId trackId,
        MidoraColor? color) =>
        Command("Change Pure MIDI Track color", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            MidoraColor? oldColor = track.Color;
            return Prepared(
                oldColor != color,
                TrackPresentationChange(trackId),
                _ => track.Color = color,
                _ => track.Color = oldColor);
        });

    public static IProjectEditCommand UpdateLogicalTrackProperties(
        MidoraId trackId,
        string name,
        MidoraColor? colorOverride) =>
        Command("Change logical track properties", project =>
        {
            LogicalTrack track = FindLogicalTrack(project, trackId);
            string normalizedName = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: true,
                nameof(name));
            string oldName = track.Name;
            MidoraColor? oldColor = track.ColorOverride;
            bool nameChanged = !string.Equals(
                oldName,
                normalizedName,
                StringComparison.Ordinal);
            bool colorChanged = oldColor != colorOverride;
            ProjectChangeSet changes = nameChanged
                ? TrackChange(trackId)
                : colorChanged
                    ? TrackPresentationChange(trackId)
                    : NoCompilationChange();
            return Prepared(
                nameChanged || colorChanged,
                changes,
                _ =>
                {
                    track.Name = normalizedName;
                    track.ColorOverride = colorOverride;
                },
                _ =>
                {
                    track.Name = oldName;
                    track.ColorOverride = oldColor;
                });
        });

    public static IProjectEditCommand UpdatePureMidiTrackProperties(
        MidoraId trackId,
        string name,
        MidoraColor? color) =>
        Command("Change Pure MIDI Track properties", project =>
        {
            PureMidiTrack track = FindPureMidiTrack(project, trackId);
            string normalizedName = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: true,
                nameof(name));
            string oldName = track.Name;
            MidoraColor? oldColor = track.Color;
            bool nameChanged = !string.Equals(
                oldName,
                normalizedName,
                StringComparison.Ordinal);
            bool colorChanged = oldColor != color;
            ProjectChangeSet changes = nameChanged
                ? PureMidiTrackChange(trackId)
                : colorChanged
                    ? TrackPresentationChange(trackId)
                    : NoCompilationChange();
            return Prepared(
                nameChanged || colorChanged,
                changes,
                _ =>
                {
                    track.Name = normalizedName;
                    track.Color = color;
                },
                _ =>
                {
                    track.Name = oldName;
                    track.Color = oldColor;
                });
        });

    private static ProjectUserMetadata SnapshotUserMetadata(ProjectMetadata value) =>
        new(
            value.ProjectName,
            value.ProjectVersion,
            value.AuthorOrTeam,
            value.OriginalWork,
            value.Copyright);

    private static LogicalTrack FindLogicalTrack(
        MidoraProject project,
        MidoraId trackId) =>
        project.Tracks.SingleOrDefault(value => value.Id == trackId)
        ?? throw new ArgumentOutOfRangeException(nameof(trackId));

    private static void RestoreUserMetadata(
        ProjectMetadata metadata,
        ProjectUserMetadata value)
    {
        metadata.ProjectName = value.ProjectName;
        metadata.ProjectVersion = value.ProjectVersion;
        metadata.AuthorOrTeam = value.AuthorOrTeam;
        metadata.OriginalWork = value.OriginalWork;
        metadata.Copyright = value.Copyright;
    }

    private readonly record struct ProjectUserMetadata(
        string ProjectName,
        string ProjectVersion,
        string AuthorOrTeam,
        string OriginalWork,
        string Copyright);
}
