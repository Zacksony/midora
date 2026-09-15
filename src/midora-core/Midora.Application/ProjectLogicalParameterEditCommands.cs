using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand RenameLogicalParameter(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        string name) =>
        Command("Rename logical parameter", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            string normalized = NormalizeUniqueLogicalParameterName(
                instrument,
                parameterId,
                name);
            string oldName = parameter.Name;
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                EventInstrumentChange(eventInstrumentId),
                _ => parameter.Name = normalized,
                _ => parameter.Name = oldName);
        });

    public static IProjectEditCommand UpdateLogicalParameterDefaultValue(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        double defaultValue) =>
        Command("Change logical parameter default value", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            ValidateLogicalParameterValue(
                parameter,
                defaultValue,
                parameter.Minimum,
                parameter.Maximum,
                nameof(defaultValue));
            double oldValue = parameter.DefaultValue;
            return Prepared(
                oldValue != defaultValue,
                EventInstrumentChange(eventInstrumentId),
                _ => parameter.DefaultValue = defaultValue,
                _ => parameter.DefaultValue = oldValue);
        });

    public static IProjectEditCommand UpdateLogicalParameterDisplayRange(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        double displayMinimum,
        double displayMaximum) =>
        Command("Change logical parameter display range", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            ValidateFiniteOrderedRange(
                displayMinimum,
                displayMaximum,
                nameof(displayMinimum));
            DoubleRange old = new(parameter.DisplayMinimum, parameter.DisplayMaximum);
            DoubleRange replacement = new(displayMinimum, displayMaximum);
            return Prepared(
                old != replacement,
                NoCompilationChange(),
                _ => SetLogicalParameterDisplayRange(parameter, replacement),
                _ => SetLogicalParameterDisplayRange(parameter, old));
        });

    public static IProjectEditCommand UpdateLogicalParameterLegalRange(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        double minimum,
        double maximum) =>
        Command("Change logical parameter legal range", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            ValidateFiniteOrderedRange(minimum, maximum, nameof(minimum));
            if (!Enum.IsDefined(parameter.Type))
            {
                throw new InvalidOperationException(
                    "The Logical Parameter input type must be repaired before editing its legal range.");
            }
            if (parameter.Type == LogicalParameterType.Integer
                && (minimum != Math.Truncate(minimum)
                    || maximum != Math.Truncate(maximum)))
            {
                throw new ArgumentException(
                    "An Integer Logical Parameter requires integer legal range endpoints.",
                    nameof(minimum));
            }
            ValidateLogicalParameterValue(
                parameter,
                parameter.DefaultValue,
                minimum,
                maximum,
                nameof(minimum));
            if (parameter.Type == LogicalParameterType.Enum
                && GetLogicalParameterEnumValues(parameter)
                    .Any(value => value < minimum || value > maximum))
            {
                throw new InvalidOperationException(
                    "The legal range must contain every current Enum item value.");
            }
            if (project.Tracks
                .SelectMany(track => track.Segments)
                .SelectMany(segment => segment.ParameterLanes)
                .Where(lane => lane.ParameterId == parameterId)
                .SelectMany(lane => lane.Points)
                .Any(point => point.Value < minimum || point.Value > maximum))
            {
                throw new InvalidOperationException(
                    "The requested legal range would invalidate existing Logical Parameter Lane points; Q-NUI-009 must be decided before applying a migration.");
            }
            DoubleRange old = new(parameter.Minimum, parameter.Maximum);
            DoubleRange replacement = new(minimum, maximum);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetLogicalParameterLegalRange(parameter, replacement),
                _ => SetLogicalParameterLegalRange(parameter, old));
        });

    public static IProjectEditCommand RenameLogicalParameterEnumItem(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        MidoraId enumItemId,
        string name) =>
        Command("Rename logical parameter enum item", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            if (parameter.Type != LogicalParameterType.Enum)
            {
                throw new InvalidOperationException(
                    "Only an Enum Logical Parameter exposes Enum item names.");
            }
            LogicalParameterEnumItem item = parameter.EnumItems
                .SingleOrDefault(value => value.Id == enumItemId)
                ?? throw new ArgumentOutOfRangeException(nameof(enumItemId));
            string normalized = ProjectTextRules.NormalizeShortText(
                name,
                allowEmpty: false,
                nameof(name));
            if (parameter.EnumItems.Any(value =>
                value.Id != enumItemId
                && string.Equals(
                    value.Name.Trim(),
                    normalized,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "An Enum item with the same name already exists in this Logical Parameter.");
            }
            string oldName = item.Name;
            return Prepared(
                !string.Equals(oldName, normalized, StringComparison.Ordinal),
                EventInstrumentChange(eventInstrumentId),
                _ => item.Name = normalized,
                _ => item.Name = oldName);
        });

    public static IProjectEditCommand DeleteLogicalParameter(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        bool referencedDeletionConfirmed) =>
        Command("Delete logical parameter", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            bool isReferenced = project.Tracks
                    .SelectMany(track => track.Segments)
                    .SelectMany(segment => segment.ParameterLanes)
                    .Any(lane => lane.ParameterId == parameterId)
                || instrument.ParameterMappings.Any(value => value.ParameterId == parameterId)
                || EnumerateMappingSteps(instrument)
                    .Any(value => value.LogicalParameterId == parameterId);
            if (isReferenced && !referencedDeletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a referenced Logical Parameter requires explicit confirmation.");
            }
            int originalIndex = instrument.LogicalParameters.IndexOf(parameter);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(
                    instrument.LogicalParameters,
                    parameter,
                    "Logical Parameter"),
                _ => InsertAt(
                    instrument.LogicalParameters,
                    originalIndex,
                    parameter,
                    "Logical Parameter"));
        });

    public static IProjectEditCommand UpdateLogicalParameterMappingSource(
        MidoraId eventInstrumentId,
        MidoraId mappingId,
        MidoraId parameterId) =>
        Command("Change logical parameter mapping source", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterMapping mapping = FindLogicalParameterMapping(instrument, mappingId);
            _ = FindLogicalParameter(instrument, parameterId);
            MidoraId oldParameterId = mapping.ParameterId;
            return Prepared(
                oldParameterId != parameterId,
                EventInstrumentChange(eventInstrumentId),
                _ => mapping.ParameterId = parameterId,
                _ => mapping.ParameterId = oldParameterId);
        });

    public static IProjectEditCommand UpdateLogicalParameterMappingTarget(
        MidoraId eventInstrumentId,
        MidoraId mappingId,
        MidoraId subVoiceId,
        MidiValueTarget target) =>
        Command("Change logical parameter mapping target", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterMapping mapping = FindLogicalParameterMapping(instrument, mappingId);
            _ = FindSubVoice(instrument, subVoiceId);
            MidiStateValueRules.Validate(target, value: null);
            LogicalParameterMappingTarget old = new(
                mapping.SubVoiceId,
                mapping.Target,
                new(mapping.TargetSettings.Rounding, mapping.TargetSettings.Overflow));
            if (old.SubVoiceId == subVoiceId && old.Target == target)
            {
                return Prepared(
                    hasChanges: false,
                    EventInstrumentChange(eventInstrumentId),
                    _ => { },
                    _ => { });
            }
            LogicalParameterMapping[] peers = instrument.ParameterMappings
                .Where(value => !ReferenceEquals(value, mapping)
                    && value.SubVoiceId == subVoiceId
                    && value.Target == target)
                .ToArray();
            IntegerTargetSettingsValue replacementSettings = peers.Length == 0
                ? old.Settings
                : GetSharedTargetSettings(peers);
            LogicalParameterMappingTarget replacement = new(
                subVoiceId,
                target,
                replacementSettings);
            return Prepared(
                old != replacement,
                EventInstrumentChange(eventInstrumentId),
                _ => SetLogicalParameterMappingTarget(mapping, replacement),
                _ => SetLogicalParameterMappingTarget(mapping, old));
        });

    public static IProjectEditCommand UpdateLogicalParameterMappingRoute(
        MidoraId eventInstrumentId,
        MidoraId mappingId,
        MidoraId parameterId,
        MidoraId subVoiceId,
        MidiValueTarget target) =>
        Command("Change logical parameter mapping route", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterMapping mapping = FindLogicalParameterMapping(instrument, mappingId);
            _ = FindLogicalParameter(instrument, parameterId);
            _ = FindSubVoice(instrument, subVoiceId);
            MidiStateValueRules.Validate(target, value: null);

            MidoraId oldParameterId = mapping.ParameterId;
            LogicalParameterMappingTarget oldTarget = new(
                mapping.SubVoiceId,
                mapping.Target,
                new(mapping.TargetSettings.Rounding, mapping.TargetSettings.Overflow));
            LogicalParameterMapping[] peers = instrument.ParameterMappings
                .Where(value => !ReferenceEquals(value, mapping)
                    && value.SubVoiceId == subVoiceId
                    && value.Target == target)
                .ToArray();
            IntegerTargetSettingsValue replacementSettings = peers.Length == 0
                ? oldTarget.Settings
                : GetSharedTargetSettings(peers);
            LogicalParameterMappingTarget replacementTarget = new(
                subVoiceId,
                target,
                replacementSettings);

            return Prepared(
                oldParameterId != parameterId || oldTarget != replacementTarget,
                EventInstrumentChange(eventInstrumentId),
                _ =>
                {
                    mapping.ParameterId = parameterId;
                    SetLogicalParameterMappingTarget(mapping, replacementTarget);
                },
                _ =>
                {
                    mapping.ParameterId = oldParameterId;
                    SetLogicalParameterMappingTarget(mapping, oldTarget);
                });
        });

    public static IProjectEditCommand UpdateLogicalParameterMappingTargetSettings(
        MidoraId eventInstrumentId,
        MidoraId mappingId,
        MappingRounding rounding,
        MappingOverflow overflow) =>
        Command("Change logical parameter mapping target settings", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterMapping mapping = FindLogicalParameterMapping(instrument, mappingId);
            ValidateIntegerTargetSettings(rounding, overflow);
            IntegerTargetSettingsValue replacement = new(rounding, overflow);
            LogicalParameterMappingSettingsChange[] changes = instrument.ParameterMappings
                .Where(value => value.SubVoiceId == mapping.SubVoiceId
                    && value.Target == mapping.Target)
                .Select(value => new LogicalParameterMappingSettingsChange(
                    value,
                    new(value.TargetSettings.Rounding, value.TargetSettings.Overflow)))
                .ToArray();
            return Prepared(
                changes.Any(value => value.OldValue != replacement),
                EventInstrumentChange(eventInstrumentId),
                _ => SetLogicalParameterMappingSettings(changes, replacement),
                _ => RestoreLogicalParameterMappingSettings(changes));
        });

    public static IProjectEditCommand ReorderLogicalParameterMapping(
        MidoraId eventInstrumentId,
        MidoraId mappingId,
        int newIndex) =>
        Command("Reorder logical parameter mapping", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterMapping mapping = FindLogicalParameterMapping(instrument, mappingId);
            int oldIndex = instrument.ParameterMappings.IndexOf(mapping);
            ValidateExistingIndex(newIndex, instrument.ParameterMappings.Count, nameof(newIndex));
            return Prepared(
                oldIndex != newIndex,
                EventInstrumentChange(eventInstrumentId),
                _ => Move(instrument.ParameterMappings, mapping, newIndex),
                _ => Move(instrument.ParameterMappings, mapping, oldIndex));
        });

    public static IProjectEditCommand DeleteLogicalParameterMapping(
        MidoraId eventInstrumentId,
        MidoraId mappingId,
        bool deletionConfirmed) =>
        Command("Delete logical parameter mapping", project =>
        {
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterMapping mapping = FindLogicalParameterMapping(instrument, mappingId);
            if (!deletionConfirmed)
            {
                throw new InvalidOperationException(
                    "Deleting a Logical Parameter Mapping requires explicit confirmation.");
            }
            int originalIndex = instrument.ParameterMappings.IndexOf(mapping);
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(eventInstrumentId),
                _ => RemoveRequired(
                    instrument.ParameterMappings,
                    mapping,
                    "Logical Parameter Mapping"),
                _ => InsertAt(
                    instrument.ParameterMappings,
                    originalIndex,
                    mapping,
                    "Logical Parameter Mapping"));
        });

    private static LogicalParameterDefinition FindLogicalParameter(
        EventInstrument instrument,
        MidoraId parameterId) =>
        instrument.LogicalParameters.SingleOrDefault(value => value.Id == parameterId)
        ?? throw new ArgumentOutOfRangeException(nameof(parameterId));

    private static LogicalParameterMapping FindLogicalParameterMapping(
        EventInstrument instrument,
        MidoraId mappingId) =>
        instrument.ParameterMappings.SingleOrDefault(value => value.Id == mappingId)
        ?? throw new ArgumentOutOfRangeException(nameof(mappingId));

    private static string NormalizeUniqueLogicalParameterName(
        EventInstrument instrument,
        MidoraId parameterId,
        string name)
    {
        string normalized = ProjectTextRules.NormalizeShortText(
            name,
            allowEmpty: false,
            nameof(name));
        if (instrument.LogicalParameters.Any(value =>
            value.Id != parameterId
            && string.Equals(
                value.Name.Trim(),
                normalized,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "A Logical Parameter with the same name already exists in this Event Instrument.");
        }
        return normalized;
    }

    private static void ValidateLogicalParameterValue(
        LogicalParameterDefinition parameter,
        double value,
        double minimum,
        double maximum,
        string parameterName)
    {
        if (!Enum.IsDefined(parameter.Type))
        {
            throw new InvalidOperationException(
                "The Logical Parameter input type is invalid.");
        }
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
        if (parameter.Type == LogicalParameterType.Integer
            && value != Math.Truncate(value))
        {
            throw new ArgumentException(
                "An Integer Logical Parameter requires an integer value.",
                parameterName);
        }
        if (parameter.Type == LogicalParameterType.Enum
            && (value < int.MinValue || value > int.MaxValue
                || value != Math.Truncate(value)
                || !GetLogicalParameterEnumValues(parameter).Contains((int)value)))
        {
            throw new ArgumentException(
                "An Enum Logical Parameter requires a defined Enum item value.",
                parameterName);
        }
    }

    private static void ValidateFiniteOrderedRange(
        double minimum,
        double maximum,
        string parameterName)
    {
        if (!double.IsFinite(minimum)
            || !double.IsFinite(maximum)
            || maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static int[] GetLogicalParameterEnumValues(
        LogicalParameterDefinition parameter) =>
        parameter.EnumItems
            .Select((item, index) => parameter.UsesExplicitEnumValues ? item.Value : index)
            .ToArray();

    private static void SetLogicalParameterDisplayRange(
        LogicalParameterDefinition parameter,
        DoubleRange range)
    {
        parameter.DisplayMinimum = range.Minimum;
        parameter.DisplayMaximum = range.Maximum;
    }

    private static void SetLogicalParameterLegalRange(
        LogicalParameterDefinition parameter,
        DoubleRange range)
    {
        parameter.Minimum = range.Minimum;
        parameter.Maximum = range.Maximum;
    }

    private static IntegerTargetSettingsValue GetSharedTargetSettings(
        IReadOnlyList<LogicalParameterMapping> mappings)
    {
        IntegerTargetSettingsValue result = new(
            mappings[0].TargetSettings.Rounding,
            mappings[0].TargetSettings.Overflow);
        if (mappings.Skip(1).Any(value =>
            value.TargetSettings.Rounding != result.Rounding
            || value.TargetSettings.Overflow != result.Overflow))
        {
            throw new InvalidOperationException(
                "Existing Logical Parameter Mappings for the target do not share one target setting.");
        }
        return result;
    }

    private static void SetLogicalParameterMappingTarget(
        LogicalParameterMapping mapping,
        LogicalParameterMappingTarget value)
    {
        mapping.SubVoiceId = value.SubVoiceId;
        mapping.Target = value.Target;
        SetTargetSettings(mapping.TargetSettings, value.Settings);
    }

    private static void SetLogicalParameterMappingSettings(
        IEnumerable<LogicalParameterMappingSettingsChange> changes,
        IntegerTargetSettingsValue value)
    {
        foreach (LogicalParameterMappingSettingsChange change in changes)
        {
            SetTargetSettings(change.Mapping.TargetSettings, value);
        }
    }

    private static void RestoreLogicalParameterMappingSettings(
        IEnumerable<LogicalParameterMappingSettingsChange> changes)
    {
        foreach (LogicalParameterMappingSettingsChange change in changes)
        {
            SetTargetSettings(change.Mapping.TargetSettings, change.OldValue);
        }
    }

    private readonly record struct DoubleRange(double Minimum, double Maximum);

    private readonly record struct LogicalParameterMappingTarget(
        MidoraId SubVoiceId,
        MidiValueTarget Target,
        IntegerTargetSettingsValue Settings);

    private readonly record struct LogicalParameterMappingSettingsChange(
        LogicalParameterMapping Mapping,
        IntegerTargetSettingsValue OldValue);
}
