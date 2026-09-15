using Midora.Domain;

namespace Midora.Application;

public sealed record LogicalParameterEnumItemDefinitionEdit(
    MidoraId? ExistingItemId,
    string Name,
    int Value = 0);

public sealed record LogicalParameterDefinitionEdit(
    LogicalParameterType Type,
    double Minimum,
    double Maximum,
    double DisplayMinimum,
    double DisplayMaximum,
    double DefaultValue,
    bool UsesExplicitEnumValues,
    IReadOnlyList<LogicalParameterEnumItemDefinitionEdit> EnumItems);

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand MigrateLogicalParameterDefinition(
        MidoraId eventInstrumentId,
        MidoraId parameterId,
        LogicalParameterDefinitionEdit edit,
        LogicalParameterLaneRebindMode migrationMode,
        bool enumSemanticWarningAcknowledged,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.EnumItems);
        // The input DTO owns its existing strings. Freeze only its reference
        // directory here, and reject an oversized validation plan before any
        // array/dictionary is constructed. The same conservative allowance is
        // charged to the shared operation budget during preparation below.
        const long metadataBytesPerEnumItem = 2048;
        long maximumWorking = BulkEditPreparationContext.Current?.Resources.Budget.MaximumWorkingBytes
            ?? new PagedEditResourceBudget().MaximumWorkingBytes;
        if ((long)edit.EnumItems.Count * metadataBytesPerEnumItem > maximumWorking)
            throw new InvalidOperationException("The Logical Parameter enum definition exceeds the edit working-memory budget.");
        LogicalParameterEnumItemDefinitionEdit[] frozenItems = edit.EnumItems.ToArray();
        return Command("Migrate logical parameter definition", project =>
        {
            using var preparation = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
            if (!Enum.IsDefined(migrationMode))
            {
                throw new ArgumentOutOfRangeException(nameof(migrationMode));
            }
            EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
            LogicalParameterDefinition parameter = FindLogicalParameter(instrument, parameterId);
            using var validationBudget = preparation.Resources.ReserveWorking(
                checked(((long)parameter.EnumItems.Count + frozenItems.Length) * metadataBytesPerEnumItem));
            string targetName = name is null
                ? parameter.Name
                : NormalizeUniqueLogicalParameterName(instrument, parameterId, name);
            ValidatedLogicalParameterDefinitionEdit target =
                ValidateLogicalParameterDefinitionEdit(parameter, edit, frozenItems);
            if (target.Type == LogicalParameterType.Enum
                && !enumSemanticWarningAcknowledged)
            {
                throw new InvalidOperationException(
                    "Migrating a Logical Parameter to an Enum definition requires acknowledging that numeric compatibility does not preserve semantic meaning.");
            }

            if (parameter.EnumItems.Count + (long)target.Items.Count + project.Tracks.SelectMany(track => track.Segments).SelectMany(segment => segment.ParameterLanes)
                .Where(lane => lane.ParameterId == parameterId).Sum(lane => 1L + lane.Points.Count) >= BoundedNoteThreshold)
                return PrepareBoundedLogicalParameterDefinitionMigration(project, instrument, parameter, target, migrationMode, targetName);

            LaneMigration[] laneMigrations = project.Tracks
                .SelectMany(track => track.Segments.SelectMany(segment =>
                    segment.ParameterLanes
                        .Where(lane => lane.ParameterId == parameterId)
                        .Select(lane => new LaneMigration(
                            track.Id,
                            lane,
                            lane.Points.ToArray(),
                            lane.Points
                                .Select(point => ConvertPoint(
                                    project,
                                    point,
                                    target.Target,
                                    migrationMode))
                                .Where(point => point is not null)
                                .Cast<CurvePoint>()
                                .ToArray()))))
                .ToArray();
            LogicalParameterDefinitionSnapshot old = CaptureDefinition(parameter);
            string oldName = parameter.Name;
            LogicalParameterEnumItemSnapshot[] oldItems = parameter.EnumItems
                .Select(item => new LogicalParameterEnumItemSnapshot(item, item.Name, item.Value))
                .ToArray();
            List<LogicalParameterEnumItem>? replacementItems = null;
            ProjectChangeSet changes = EventInstrumentChange(eventInstrumentId);
            changes.TrackIds.UnionWith(laneMigrations.Select(value => value.TrackId));

            return Prepared(
                !string.Equals(oldName, targetName, StringComparison.Ordinal)
                    || DefinitionMigrationHasChanges(parameter, target, laneMigrations),
                changes,
                owner =>
                {
                    replacementItems ??= BuildReplacementEnumItems(
                        owner,
                        parameter,
                        target.Items);
                    parameter.Name = targetName;
                    ApplyDefinition(parameter, target, replacementItems);
                    foreach (LaneMigration migration in laneMigrations)
                    {
                        SetLane(migration.Lane, parameterId, migration.ReplacementPoints);
                    }
                },
                _ =>
                {
                    parameter.Name = oldName;
                    RestoreDefinition(parameter, old, oldItems);
                    foreach (LaneMigration migration in laneMigrations)
                    {
                        SetLane(migration.Lane, parameterId, migration.OldPoints);
                    }
                });
        });
    }

    private static ValidatedLogicalParameterDefinitionEdit ValidateLogicalParameterDefinitionEdit(
        LogicalParameterDefinition parameter,
        LogicalParameterDefinitionEdit edit,
        IReadOnlyList<LogicalParameterEnumItemDefinitionEdit> items)
    {
        ValidateLogicalParameterDefinitionCreation(
            edit.Type,
            edit.Minimum,
            edit.Maximum,
            edit.DisplayMinimum,
            edit.DisplayMaximum,
            edit.DefaultValue);
        if (edit.Type != LogicalParameterType.Enum)
        {
            if (edit.UsesExplicitEnumValues || items.Count != 0)
            {
                throw new ArgumentException(
                    "Only an Enum Logical Parameter can contain Enum items or use explicit Enum values.",
                    nameof(edit));
            }
            return new(
                edit.Type,
                edit.Minimum,
                edit.Maximum,
                edit.DisplayMinimum,
                edit.DisplayMaximum,
                edit.DefaultValue,
                false,
                [],
                new(edit.Type, edit.Minimum, edit.Maximum, null));
        }
        if (items.Count == 0)
        {
            throw new ArgumentException(
                "An Enum Logical Parameter must contain at least one Enum item.",
                nameof(edit));
        }

        Dictionary<MidoraId, LogicalParameterEnumItem> existing = parameter.EnumItems
            .ToDictionary(value => value.Id);
        HashSet<MidoraId> usedIds = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> values = [];
        List<ValidatedEnumItemEdit> validated = [];
        for (int index = 0; index < items.Count; index++)
        {
            if ((index & 255) == 0) BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            LogicalParameterEnumItemDefinitionEdit item = items[index]
                ?? throw new ArgumentException("Enum item definitions cannot be null.", nameof(edit));
            string name = ProjectTextRules.NormalizeShortText(
                item.Name,
                allowEmpty: false,
                nameof(edit));
            if (!names.Add(name))
            {
                throw new ArgumentException(
                    "Enum item names must be unique ignoring case.",
                    nameof(edit));
            }
            LogicalParameterEnumItem? current = null;
            if (item.ExistingItemId.HasValue)
            {
                if (item.ExistingItemId.Value == default
                    || !existing.TryGetValue(item.ExistingItemId.Value, out current)
                    || !usedIds.Add(item.ExistingItemId.Value))
                {
                    throw new ArgumentException(
                        "An existing Enum item ID is invalid, duplicated, or belongs to another definition.",
                        nameof(edit));
                }
            }
            int value = edit.UsesExplicitEnumValues ? item.Value : index;
            if (value < edit.Minimum || value > edit.Maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(edit),
                    "Every Enum item value must be inside the legal range.");
            }
            if (!values.Add(value))
            {
                throw new ArgumentException(
                    "Enum item values must be unique.",
                    nameof(edit));
            }
            validated.Add(new(current, name, value));
        }
        if (edit.DefaultValue < int.MinValue
            || edit.DefaultValue > int.MaxValue
            || !values.Contains((int)edit.DefaultValue))
        {
            throw new ArgumentException(
                "An Enum default value must identify one of the target Enum items.",
                nameof(edit));
        }
        int[] enumValues = values.Order().ToArray();
        return new(
            edit.Type,
            edit.Minimum,
            edit.Maximum,
            edit.DisplayMinimum,
            edit.DisplayMaximum,
            edit.DefaultValue,
            edit.UsesExplicitEnumValues,
            validated.ToArray(),
            new(edit.Type, edit.Minimum, edit.Maximum, enumValues));
    }

    private static List<LogicalParameterEnumItem> BuildReplacementEnumItems(
        MidoraProject project,
        LogicalParameterDefinition parameter,
        IEnumerable<ValidatedEnumItemEdit> items)
    {
        List<LogicalParameterEnumItem> result = [];
        foreach (ValidatedEnumItemEdit item in items)
        {
            LogicalParameterEnumItem value = item.ExistingItem ?? new LogicalParameterEnumItem(project)
            {
                Name = item.Name,
                Value = item.Value
            };
            result.Add(value);
        }
        if (result.Select(value => value.Id).Distinct().Count() != result.Count
            || result.Any(value => parameter.EnumItems.All(existing => existing.Id != value.Id)
                && value.Id == default))
        {
            throw new InvalidOperationException(
                "The migrated Enum item identities are invalid or duplicated.");
        }
        return result;
    }

    private static void ApplyDefinition(
        LogicalParameterDefinition parameter,
        ValidatedLogicalParameterDefinitionEdit target,
        IReadOnlyCollection<LogicalParameterEnumItem> items)
    {
        parameter.Type = target.Type;
        parameter.Minimum = target.Minimum;
        parameter.Maximum = target.Maximum;
        parameter.DisplayMinimum = target.DisplayMinimum;
        parameter.DisplayMaximum = target.DisplayMaximum;
        parameter.DefaultValue = target.DefaultValue;
        parameter.UsesExplicitEnumValues = target.UsesExplicitEnumValues;
        parameter.EnumItems.Clear();
        int index = 0;
        foreach (LogicalParameterEnumItem item in items)
        {
            ValidatedEnumItemEdit value = target.Items[index++];
            item.Name = value.Name;
            item.Value = value.Value;
            parameter.EnumItems.Add(item);
        }
    }

    private static LogicalParameterDefinitionSnapshot CaptureDefinition(
        LogicalParameterDefinition parameter) =>
        new(
            parameter.Type,
            parameter.Minimum,
            parameter.Maximum,
            parameter.DisplayMinimum,
            parameter.DisplayMaximum,
            parameter.DefaultValue,
            parameter.UsesExplicitEnumValues);

    private static void RestoreDefinition(
        LogicalParameterDefinition parameter,
        LogicalParameterDefinitionSnapshot snapshot,
        IReadOnlyCollection<LogicalParameterEnumItemSnapshot> items)
    {
        parameter.Type = snapshot.Type;
        parameter.Minimum = snapshot.Minimum;
        parameter.Maximum = snapshot.Maximum;
        parameter.DisplayMinimum = snapshot.DisplayMinimum;
        parameter.DisplayMaximum = snapshot.DisplayMaximum;
        parameter.DefaultValue = snapshot.DefaultValue;
        parameter.UsesExplicitEnumValues = snapshot.UsesExplicitEnumValues;
        parameter.EnumItems.Clear();
        foreach (LogicalParameterEnumItemSnapshot item in items)
        {
            item.Item.Name = item.Name;
            item.Item.Value = item.Value;
            parameter.EnumItems.Add(item.Item);
        }
    }

    private static bool DefinitionMigrationHasChanges(
        LogicalParameterDefinition parameter,
        ValidatedLogicalParameterDefinitionEdit target,
        IEnumerable<LaneMigration> lanes)
    {
        if (parameter.Type != target.Type
            || parameter.Minimum != target.Minimum
            || parameter.Maximum != target.Maximum
            || parameter.DisplayMinimum != target.DisplayMinimum
            || parameter.DisplayMaximum != target.DisplayMaximum
            || parameter.DefaultValue != target.DefaultValue
            || parameter.UsesExplicitEnumValues != target.UsesExplicitEnumValues
            || parameter.EnumItems.Count != target.Items.Count)
        {
            return true;
        }
        for (int index = 0; index < target.Items.Count; index++)
        {
            LogicalParameterEnumItem current = parameter.EnumItems[index];
            ValidatedEnumItemEdit replacement = target.Items[index];
            if (!ReferenceEquals(current, replacement.ExistingItem)
                || !string.Equals(current.Name, replacement.Name, StringComparison.Ordinal)
                || current.Value != replacement.Value)
            {
                return true;
            }
        }
        return lanes.Any(value =>
            value.OldPoints.Length != value.ReplacementPoints.Length
            || value.OldPoints.Where((point, index) =>
                !ReferenceEquals(point, value.ReplacementPoints[index])).Any());
    }

    private sealed record ValidatedLogicalParameterDefinitionEdit(
        LogicalParameterType Type,
        double Minimum,
        double Maximum,
        double DisplayMinimum,
        double DisplayMaximum,
        double DefaultValue,
        bool UsesExplicitEnumValues,
        IReadOnlyList<ValidatedEnumItemEdit> Items,
        LogicalParameterTarget Target);

    private sealed record ValidatedEnumItemEdit(
        LogicalParameterEnumItem? ExistingItem,
        string Name,
        int Value);

    private sealed record LogicalParameterDefinitionSnapshot(
        LogicalParameterType Type,
        double Minimum,
        double Maximum,
        double DisplayMinimum,
        double DisplayMaximum,
        double DefaultValue,
        bool UsesExplicitEnumValues);

    private sealed record LogicalParameterEnumItemSnapshot(
        LogicalParameterEnumItem Item,
        string Name,
        int Value);

    private sealed record LaneMigration(
        MidoraId TrackId,
        LogicalParameterLane Lane,
        CurvePoint[] OldPoints,
        CurvePoint[] ReplacementPoints);
}
