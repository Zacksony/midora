using System.Globalization;
using Midora.Application;
using Midora.Domain;

namespace Midora.Desktop;

internal static class ProjectSettingsProjection
{
    public static IProjectEditCommand CreateEditCommand(
        MidoraProject project,
        PropertyField field)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(field);
        string value = field.Value.Trim();

        if (field.Key.StartsWith("settings.project.", StringComparison.Ordinal))
        {
            ProjectMetadata metadata = project.Metadata;
            return ProjectDomainEditCommands.UpdateProjectMetadata(
                field.Key == "settings.project.name" ? field.Value : metadata.ProjectName,
                field.Key == "settings.project.version" ? field.Value : metadata.ProjectVersion,
                field.Key == "settings.project.author" ? field.Value : metadata.AuthorOrTeam,
                field.Key == "settings.project.originalWork" ? field.Value : metadata.OriginalWork,
                field.Key == "settings.project.copyright" ? field.Value : metadata.Copyright);
        }

        if (field.Key.StartsWith("settings.initial.", StringComparison.Ordinal))
        {
            return ProjectDomainEditCommands.UpdateProjectInitialStateValue(
                ParseStateTarget(field.Key["settings.initial.".Length..]),
                DisplayValue(ParseStateTarget(field.Key["settings.initial.".Length..]), value, field.Label));
        }

        if (field.Key.StartsWith("settings.reset.", StringComparison.Ordinal))
        {
            return ProjectDomainEditCommands.UpdateProjectResetDefaultValue(
                ParseStateTarget(field.Key["settings.reset.".Length..]),
                DisplayValue(ParseStateTarget(field.Key["settings.reset.".Length..]), value, field.Label));
        }

        throw new InvalidOperationException("This Project Settings field is read-only.");
    }

    private static int? DisplayValue(MidiValueTarget target, string value, string label) =>
        NullableInt(value, label) is { } display ? MidiEditingValueDomain.ClampInitialState(target, display) : null;

    private static int? NullableInt(string value, string label)
    {
        if (value.Length == 0) return null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result : throw new FormatException($"{label} must be blank or a base-10 integer.");
    }

    private static MidiValueTarget ParseStateTarget(string key)
    {
        if (TryNumberedTarget(key, "cc.", MidiValueKind.ControlChange, out MidiValueTarget target)
            || TryNumberedTarget(key, "rpn.", MidiValueKind.RegisteredParameter, out target)
            || TryNumberedTarget(key, "nrpn.", MidiValueKind.NonRegisteredParameter, out target))
        {
            return target;
        }
        return key switch
        {
            "bankMsb" => new(MidiValueKind.BankMsb),
            "bankLsb" => new(MidiValueKind.BankLsb),
            "program" => new(MidiValueKind.Program),
            "pitchBend" => new(MidiValueKind.PitchBend),
            "pitchRangeSemitones" => new(MidiValueKind.PitchBendRangeSemitones),
            "pitchRangeCents" => new(MidiValueKind.PitchBendRangeCents),
            _ => throw new InvalidOperationException("This MIDI State target is unsupported.")
        };
    }

    private static bool TryNumberedTarget(
        string key,
        string prefix,
        MidiValueKind kind,
        out MidiValueTarget target)
    {
        target = default;
        if (!key.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int number))
        {
            return false;
        }
        target = new(kind, number);
        return true;
    }

}
