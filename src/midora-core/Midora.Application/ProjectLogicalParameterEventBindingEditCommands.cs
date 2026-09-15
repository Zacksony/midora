using System.Globalization;
using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Application;

public enum LogicalParameterEventBindingOperation
{
    Override,
    Add,
    Multiply
}

public enum LogicalParameterEventBindingSubVoiceScope
{
    Selected,
    All
}

public enum LogicalParameterEventBindingConflictPolicy
{
    Append,
    Replace
}

public sealed record LogicalParameterEventBindingRequest
{
    public required string Name { get; init; }
    public required MidiValueTarget Target { get; init; }
    public LogicalParameterEventBindingOperation Operation { get; init; }
    public LogicalParameterEventBindingSubVoiceScope SubVoiceScope { get; init; }
    public IReadOnlyCollection<MidoraId> SubVoiceIds { get; init; } = [];
    public int SourceMinimum { get; init; }
    public int SourceMaximum { get; init; } = 127;
    public double? FactorMinimum { get; init; }
    public double? FactorMaximum { get; init; }
    public LogicalParameterEventBindingConflictPolicy ConflictPolicy { get; init; }
        = LogicalParameterEventBindingConflictPolicy.Append;
}

public static partial class ProjectDomainEditCommands
{
    public static IProjectEditCommand CreateLogicalParameterEventBinding(
        MidoraId eventInstrumentId,
        LogicalParameterEventBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MidoraId[] requestedSubVoiceIds = request.SubVoiceIds?.ToArray()
            ?? throw new ArgumentNullException(nameof(request.SubVoiceIds));
        FrozenLogicalParameterEventBindingRequest frozen = new(
            request.Name,
            request.Target,
            request.Operation,
            request.SubVoiceScope,
            requestedSubVoiceIds,
            request.SourceMinimum,
            request.SourceMaximum,
            request.FactorMinimum,
            request.FactorMaximum,
            request.ConflictPolicy);

        return Command("Add logical parameter event binding", project =>
            PrepareLogicalParameterEventBinding(project, eventInstrumentId, frozen));
    }

    private static IPreparedProjectEdit PrepareLogicalParameterEventBinding(
        MidoraProject project,
        MidoraId eventInstrumentId,
        FrozenLogicalParameterEventBindingRequest request)
    {
        ValidateLogicalParameterEventBindingEnums(request);
        EventInstrument instrument = FindEventInstrument(project, eventInstrumentId);
        string normalizedName = NormalizeUniqueLogicalParameterName(
            instrument,
            default,
            request.Name);
        MidiStateValueRules.Validate(request.Target, value: null);
        TemplateEventMappingTarget eventMappingTarget =
            TemplateEventMidiTargets.ToMappingTarget(request.Target);
        SubVoice[] targetVoices = ResolveLogicalParameterEventBindingVoices(
            instrument,
            request);
        double parameterDefault = ValidateAndGetLogicalParameterEventBindingDefault(request);
        ValidateLogicalParameterDefinitionCreation(
            LogicalParameterType.Integer,
            request.SourceMinimum,
            request.SourceMaximum,
            request.SourceMinimum,
            request.SourceMaximum,
            parameterDefault);

        Dictionary<MidoraId, SubVoiceEventMapping?> existingOwners = [];
        Dictionary<MidoraId, SubVoiceEventMapping[]> expectedOwnerSequences = [];
        foreach (SubVoice voice in targetVoices)
        {
            LogicalParameterMapping[] peers = instrument.ParameterMappings
                .Where(value => value.SubVoiceId == voice.Id && value.Target == request.Target)
                .ToArray();
            if (request.ConflictPolicy == LogicalParameterEventBindingConflictPolicy.Append
                && peers.Any(value => value.TargetSettings.Rounding != MappingRounding.Round
                    || value.TargetSettings.Overflow != MappingOverflow.Clamp))
            {
                throw new InvalidOperationException(
                    "Append requires every existing Mapping for the selected SubVoice target to use Round and Clamp. Select Replace or update the existing target settings first.");
            }

            SubVoiceEventMapping[] owners = voice.EventMappings
                .Where(value => value.Target == eventMappingTarget)
                .ToArray();
            if (owners.Length > 1)
            {
                throw new InvalidOperationException(
                    "The selected SubVoice contains duplicate event-lane owners for the requested target.");
            }
            existingOwners.Add(voice.Id, owners.SingleOrDefault());
            expectedOwnerSequences.Add(voice.Id, voice.EventMappings.ToArray());
        }

        LogicalParameterMapping[] beforeMappings = instrument.ParameterMappings.ToArray();
        MidoraProject preparedProject = project;
        string? multiplyFunctionName = request.Operation
            == LogicalParameterEventBindingOperation.Multiply
                ? CreateLogicalParameterMultiplyFunctionName(instrument)
                : null;
        LogicalParameterDefinition? createdParameter = null;
        CSharpMappingFunction? createdFunction = null;
        LogicalParameterMapping[]? createdMappings = null;
        CreatedEventLaneOwner[]? createdOwners = null;
        LogicalParameterMapping[]? afterMappings = null;
        bool isAttached = false;
        bool rollbackNoOpPending = false;

        return Prepared(
            hasChanges: true,
            EventInstrumentChange(eventInstrumentId),
            applyProject =>
            {
                // A direct caller may retry after observing an Apply failure without
                // invoking the reversible-edit rollback callback. A real rollback
                // consumes this marker synchronously before any later Apply.
                rollbackNoOpPending = false;
                long? allocationStart = null;
                try
                {
                    if (isAttached)
                    {
                        throw new InvalidOperationException(
                            "The Logical Parameter event binding is already attached.");
                    }
                    ValidateLogicalParameterEventBindingApplyPreconditions(
                        applyProject,
                        preparedProject,
                        instrument,
                        targetVoices,
                        existingOwners,
                        expectedOwnerSequences,
                        beforeMappings,
                        eventMappingTarget,
                        normalizedName,
                        multiplyFunctionName);
                    if (createdParameter is null)
                    {
                        long requiredStableIds = GetLogicalParameterEventBindingStableIdCount(
                            targetVoices.Length,
                            existingOwners.Values.Count(value => value is null),
                            request.Operation == LogicalParameterEventBindingOperation.Multiply);
                        EnsureStableIdCapacity(applyProject, requiredStableIds);
                        allocationStart = applyProject.NextStableId;

                        LogicalParameterDefinition candidateParameter = new(applyProject)
                        {
                            Name = normalizedName,
                            Type = LogicalParameterType.Integer,
                            Minimum = request.SourceMinimum,
                            Maximum = request.SourceMaximum,
                            DisplayMinimum = request.SourceMinimum,
                            DisplayMaximum = request.SourceMaximum,
                            DefaultValue = parameterDefault
                        };

                        CSharpMappingFunction? candidateFunction = null;
                        if (request.Operation == LogicalParameterEventBindingOperation.Multiply)
                        {
                            candidateFunction = new(applyProject)
                            {
                                Name = multiplyFunctionName!,
                                Body = CreateLogicalParameterMultiplyExpression(
                                    request,
                                    parameterDefault)
                            };
                            candidateFunction.DeclaredContextFields.Add(
                                nameof(MappingContextV2.LogicalParameterValue));
                        }

                        LogicalParameterMapping[] candidateMappings = targetVoices
                            .Select(voice => CreateLogicalParameterEventMapping(
                                applyProject,
                                candidateParameter,
                                candidateFunction,
                                voice,
                                request))
                            .ToArray();
                        CreatedEventLaneOwner[] candidateOwners = targetVoices
                            .Where(voice => existingOwners[voice.Id] is null)
                            .Select(voice => new CreatedEventLaneOwner(
                                voice,
                                new SubVoiceEventMapping(applyProject, eventMappingTarget),
                                voice.EventMappings.Count))
                            .ToArray();
                        LogicalParameterMapping[] candidateAfterMappings =
                            CreateLogicalParameterEventBindingMappingOrder(
                                beforeMappings,
                                candidateMappings,
                                request.ConflictPolicy);

                        // Publish the detached object graph to the prepared edit only
                        // after every constructor and deterministic ordering step has
                        // succeeded. Until this point the Project graph is untouched.
                        createdParameter = candidateParameter;
                        createdFunction = candidateFunction;
                        createdMappings = candidateMappings;
                        createdOwners = candidateOwners;
                        afterMappings = candidateAfterMappings;
                    }

                    ApplyLogicalParameterEventBinding(
                        instrument,
                        beforeMappings,
                        afterMappings!,
                        createdParameter,
                        createdFunction,
                        createdOwners!);
                    isAttached = true;
                }
                catch
                {
                    if (allocationStart.HasValue)
                    {
                        applyProject.RestoreNextStableId(allocationStart.Value);
                        createdParameter = null;
                        createdFunction = null;
                        createdMappings = null;
                        createdOwners = null;
                        afterMappings = null;
                    }
                    // ProjectCompilationSession always invokes rollback when edit
                    // throws. No Project object was left attached, so that rollback
                    // must be a safe no-op instead of attempting a normal Undo.
                    rollbackNoOpPending = true;
                    throw;
                }
            },
            _ =>
            {
                if (rollbackNoOpPending)
                {
                    rollbackNoOpPending = false;
                    return;
                }
                if (!isAttached)
                {
                    throw new InvalidOperationException(
                        "The Logical Parameter event binding is not attached before Undo.");
                }
                UndoLogicalParameterEventBinding(
                    instrument,
                    beforeMappings,
                    afterMappings
                        ?? throw new InvalidOperationException(
                            "The event binding has not been created before Undo."),
                    createdParameter
                        ?? throw new InvalidOperationException(
                            "The Logical Parameter has not been created before Undo."),
                    createdFunction,
                    createdOwners
                        ?? throw new InvalidOperationException(
                            "The event-lane owner set has not been created before Undo."));
                isAttached = false;
            });
    }

    private static void ValidateLogicalParameterEventBindingEnums(
        FrozenLogicalParameterEventBindingRequest request)
    {
        if (!Enum.IsDefined(request.Operation))
            throw new ArgumentOutOfRangeException(nameof(request.Operation));
        if (!Enum.IsDefined(request.SubVoiceScope))
            throw new ArgumentOutOfRangeException(nameof(request.SubVoiceScope));
        if (!Enum.IsDefined(request.ConflictPolicy))
            throw new ArgumentOutOfRangeException(nameof(request.ConflictPolicy));
    }

    private static SubVoice[] ResolveLogicalParameterEventBindingVoices(
        EventInstrument instrument,
        FrozenLogicalParameterEventBindingRequest request)
    {
        if (request.SubVoiceScope == LogicalParameterEventBindingSubVoiceScope.All)
        {
            if (request.SubVoiceIds.Length != 0)
            {
                throw new ArgumentException(
                    "All SubVoices scope must not also provide explicit SubVoice IDs.",
                    nameof(request));
            }
            if (instrument.SubVoices.Count == 0)
            {
                throw new InvalidOperationException(
                    "The Event Instrument has no SubVoices to bind.");
            }
            return instrument.SubVoices.ToArray();
        }

        if (request.SubVoiceIds.Length == 0)
        {
            throw new ArgumentException(
                "Selected SubVoices scope requires at least one SubVoice ID.",
                nameof(request));
        }
        HashSet<MidoraId> requestedIds = [];
        foreach (MidoraId id in request.SubVoiceIds)
        {
            if (id == default || !requestedIds.Add(id))
            {
                throw new ArgumentException(
                    "Selected SubVoice IDs must be non-empty and unique.",
                    nameof(request));
            }
        }
        SubVoice[] result = instrument.SubVoices
            .Where(value => requestedIds.Contains(value.Id))
            .ToArray();
        if (result.Length != requestedIds.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Every selected SubVoice ID must belong to the Event Instrument.");
        }
        return result;
    }

    private static double ValidateAndGetLogicalParameterEventBindingDefault(
        FrozenLogicalParameterEventBindingRequest request)
    {
        if (request.SourceMaximum < request.SourceMinimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Logical Parameter source range minimum must not exceed its maximum.");
        }

        switch (request.Operation)
        {
            case LogicalParameterEventBindingOperation.Override:
            {
                RequireNoFactorRange(request);
                int defaultValue = MidiValueTargetDefaults.GetDefaultValue(request.Target);
                if (defaultValue < request.SourceMinimum || defaultValue > request.SourceMaximum)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        "Override source range must contain the formal MIDI target default value.");
                }
                return defaultValue;
            }
            case LogicalParameterEventBindingOperation.Add:
                RequireNoFactorRange(request);
                if (request.SourceMinimum > 0 || request.SourceMaximum < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(request),
                        "Add source range must contain the neutral offset 0.");
                }
                return 0;
            case LogicalParameterEventBindingOperation.Multiply:
                return GetNeutralMultiplySourceDefault(request);
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Operation));
        }
    }

    private static void RequireNoFactorRange(FrozenLogicalParameterEventBindingRequest request)
    {
        if (request.FactorMinimum.HasValue || request.FactorMaximum.HasValue)
        {
            throw new ArgumentException(
                "A factor range is only valid for a Multiply event binding.",
                nameof(request));
        }
    }

    private static double GetNeutralMultiplySourceDefault(
        FrozenLogicalParameterEventBindingRequest request)
    {
        if (!request.FactorMinimum.HasValue || !request.FactorMaximum.HasValue)
        {
            throw new ArgumentException(
                "Multiply requires both Factor Minimum and Factor Maximum.",
                nameof(request));
        }
        double factorMinimum = request.FactorMinimum.Value;
        double factorMaximum = request.FactorMaximum.Value;
        if (!double.IsFinite(factorMinimum)
            || !double.IsFinite(factorMaximum)
            || factorMaximum <= factorMinimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Multiply factor range must contain two finite, strictly increasing values.");
        }
        if (factorMinimum > 1 || factorMaximum < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Multiply factor range must contain the neutral factor 1.");
        }
        if (request.SourceMaximum == request.SourceMinimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Multiply source range must be non-degenerate.");
        }

        double sourceDefault = request.SourceMinimum
            + ((1 - factorMinimum) / (factorMaximum - factorMinimum)
                * (request.SourceMaximum - (double)request.SourceMinimum));
        if (!double.IsFinite(sourceDefault)
            || sourceDefault != Math.Truncate(sourceDefault)
            || sourceDefault < request.SourceMinimum
            || sourceDefault > request.SourceMaximum)
        {
            throw new ArgumentException(
                "Multiply requires factor 1 to map back to an exact Integer source default inside the source range.",
                nameof(request));
        }
        return sourceDefault;
    }

    private static LogicalParameterMapping CreateLogicalParameterEventMapping(
        MidoraProject project,
        LogicalParameterDefinition parameter,
        CSharpMappingFunction? multiplyFunction,
        SubVoice voice,
        FrozenLogicalParameterEventBindingRequest request)
    {
        LogicalParameterMapping mapping = new(project)
        {
            ParameterId = parameter.Id,
            SubVoiceId = voice.Id,
            Target = request.Target
        };
        mapping.TargetSettings.Rounding = MappingRounding.Round;
        mapping.TargetSettings.Overflow = MappingOverflow.Clamp;
        ValueMappingStep step = new(project)
        {
            Source = request.Operation == LogicalParameterEventBindingOperation.Multiply
                ? MappingSource.CurrentValue
                : MappingSource.LogicalParameter,
            Operation = request.Operation switch
            {
                LogicalParameterEventBindingOperation.Override => MappingOperation.Override,
                LogicalParameterEventBindingOperation.Add => MappingOperation.Add,
                LogicalParameterEventBindingOperation.Multiply => MappingOperation.CustomCSharp,
                _ => throw new ArgumentOutOfRangeException(nameof(request.Operation))
            },
            LogicalParameterId = parameter.Id,
            MappingFunctionId = multiplyFunction?.Id,
            SourceMinimum = request.SourceMinimum,
            SourceMaximum = request.SourceMaximum,
            TargetMinimum = request.FactorMinimum ?? request.SourceMinimum,
            TargetMaximum = request.FactorMaximum ?? request.SourceMaximum,
            InputOverflow = MappingInputOverflow.Clamp,
            DivideByZero = DivideByZeroPolicy.Fail
        };
        mapping.Steps.Add(step);
        return mapping;
    }

    private static string CreateLogicalParameterMultiplyExpression(
        FrozenLogicalParameterEventBindingRequest request,
        double parameterDefault)
    {
        string sourceMinimum = FormatMappingExpressionNumber(request.SourceMinimum);
        string sourceSpan = FormatMappingExpressionNumber(
            request.SourceMaximum - (double)request.SourceMinimum);
        string sourceDefault = FormatMappingExpressionNumber(parameterDefault);
        string factorMinimum = FormatMappingExpressionNumber(request.FactorMinimum!.Value);
        string factorSpan = FormatMappingExpressionNumber(
            request.FactorMaximum!.Value - request.FactorMinimum.Value);
        return $"value * (context.LogicalParameterValue == ({sourceDefault}) ? 1 : (({factorMinimum}) + (((context.LogicalParameterValue - ({sourceMinimum})) / ({sourceSpan})) * ({factorSpan}))))";
    }

    private static string FormatMappingExpressionNumber(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string CreateLogicalParameterMultiplyFunctionName(EventInstrument instrument)
    {
        const string baseName = "Quick Multiply";
        string candidate = baseName;
        for (int suffix = 2; instrument.MappingFunctions.Any(value =>
            string.Equals(value.Name.Trim(), candidate, StringComparison.OrdinalIgnoreCase)); suffix++)
        {
            candidate = $"{baseName} ({suffix})";
        }
        return candidate;
    }

    private static LogicalParameterMapping[] CreateLogicalParameterEventBindingMappingOrder(
        IReadOnlyList<LogicalParameterMapping> before,
        IReadOnlyList<LogicalParameterMapping> created,
        LogicalParameterEventBindingConflictPolicy policy)
    {
        if (policy == LogicalParameterEventBindingConflictPolicy.Append)
        {
            List<LogicalParameterMapping> result = before.ToList();
            foreach (LogicalParameterMapping mapping in created)
            {
                int insertionIndex = result.FindLastIndex(value =>
                    value.SubVoiceId == mapping.SubVoiceId && value.Target == mapping.Target);
                if (insertionIndex < 0) result.Add(mapping);
                else result.Insert(insertionIndex + 1, mapping);
            }
            return result.ToArray();
        }

        Dictionary<MidoraId, LogicalParameterMapping> replacements = created
            .ToDictionary(value => value.SubVoiceId);
        HashSet<MidoraId> inserted = [];
        List<LogicalParameterMapping> replaced = [];
        foreach (LogicalParameterMapping mapping in before)
        {
            if (replacements.TryGetValue(mapping.SubVoiceId, out LogicalParameterMapping? replacement)
                && mapping.Target == replacement.Target)
            {
                if (inserted.Add(mapping.SubVoiceId)) replaced.Add(replacement);
                continue;
            }
            replaced.Add(mapping);
        }
        foreach (LogicalParameterMapping mapping in created)
        {
            if (inserted.Add(mapping.SubVoiceId)) replaced.Add(mapping);
        }
        return replaced.ToArray();
    }

    private static void ValidateLogicalParameterEventBindingApplyPreconditions(
        MidoraProject applyProject,
        MidoraProject preparedProject,
        EventInstrument instrument,
        IReadOnlyList<SubVoice> targetVoices,
        IReadOnlyDictionary<MidoraId, SubVoiceEventMapping?> existingOwners,
        IReadOnlyDictionary<MidoraId, SubVoiceEventMapping[]> expectedOwnerSequences,
        IReadOnlyList<LogicalParameterMapping> expectedMappings,
        TemplateEventMappingTarget eventMappingTarget,
        string parameterName,
        string? mappingFunctionName)
    {
        if (!ReferenceEquals(applyProject, preparedProject))
        {
            throw new InvalidOperationException(
                "The prepared Logical Parameter event binding belongs to a different Project.");
        }
        EventInstrument[] matchingInstruments = applyProject.EventInstruments
            .Where(candidate => candidate.Id == instrument.Id)
            .Take(2)
            .ToArray();
        if (matchingInstruments.Length != 1
            || !ReferenceEquals(matchingInstruments[0], instrument))
        {
            throw new InvalidOperationException(
                "The selected Event Instrument was removed or replaced before applying the event binding.");
        }
        RequireReferenceSequence(
            instrument.ParameterMappings,
            expectedMappings,
            "Logical Parameter Mapping order changed before applying the event binding.");
        if (instrument.LogicalParameters.Any(value => string.Equals(
                value.Name.Trim(),
                parameterName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The Logical Parameter name became unavailable before applying the event binding.");
        }
        if (mappingFunctionName is not null
            && instrument.MappingFunctions.Any(value => string.Equals(
                value.Name.Trim(),
                mappingFunctionName,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The Mapping Function name became unavailable before applying the event binding.");
        }

        foreach (SubVoice voice in targetVoices)
        {
            if (!instrument.SubVoices.Any(candidate => ReferenceEquals(candidate, voice)))
            {
                throw new InvalidOperationException(
                    "A selected SubVoice was removed before applying the event binding.");
            }
            RequireReferenceSequence(
                voice.EventMappings,
                expectedOwnerSequences[voice.Id],
                "A selected SubVoice event-lane owner collection changed before applying the event binding.");
            SubVoiceEventMapping? expectedOwner = existingOwners[voice.Id];
            SubVoiceEventMapping[] currentOwners = voice.EventMappings
                .Where(value => value.Target == eventMappingTarget)
                .ToArray();
            if (expectedOwner is null
                ? currentOwners.Length != 0
                : currentOwners.Length != 1
                    || !ReferenceEquals(currentOwners[0], expectedOwner))
            {
                throw new InvalidOperationException(
                    "An existing SubVoice event-lane owner changed before applying the event binding.");
            }
        }
    }

    private static long GetLogicalParameterEventBindingStableIdCount(
        int targetVoiceCount,
        int newOwnerCount,
        bool createsMappingFunction)
    {
        if (targetVoiceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetVoiceCount));
        if (newOwnerCount < 0 || newOwnerCount > targetVoiceCount)
            throw new ArgumentOutOfRangeException(nameof(newOwnerCount));

        // Parameter: 1. Optional function: 1. Each Logical Parameter Mapping:
        // mapping + MappingChain + ValueMappingStep = 3. Each missing event owner
        // owns one newly allocated MappingChain.
        return checked(
            1L
            + (createsMappingFunction ? 1L : 0L)
            + (3L * targetVoiceCount)
            + newOwnerCount);
    }

    private static void EnsureStableIdCapacity(MidoraProject project, long requiredStableIds)
    {
        if (requiredStableIds <= 0)
            throw new ArgumentOutOfRangeException(nameof(requiredStableIds));
        long available = long.MaxValue - project.NextStableId;
        if (requiredStableIds > available)
        {
            throw new InvalidOperationException(
                "The Project does not have enough remaining stable IDs to create the Logical Parameter event binding.");
        }
    }

    private static void ApplyLogicalParameterEventBinding(
        EventInstrument instrument,
        IReadOnlyList<LogicalParameterMapping> expectedMappings,
        IReadOnlyList<LogicalParameterMapping> replacementMappings,
        LogicalParameterDefinition parameter,
        CSharpMappingFunction? function,
        IReadOnlyList<CreatedEventLaneOwner> owners)
    {
        RequireReferenceSequence(
            instrument.ParameterMappings,
            expectedMappings,
            "Logical Parameter Mapping order changed before applying the event binding.");
        if (instrument.LogicalParameters.Any(value => value.Id == parameter.Id))
            throw new InvalidOperationException("The new Logical Parameter identity is already attached.");
        if (function is not null && instrument.MappingFunctions.Any(value => value.Id == function.Id))
            throw new InvalidOperationException("The new Mapping Function identity is already attached.");
        foreach (CreatedEventLaneOwner owner in owners)
        {
            if (owner.Voice.EventMappings.Any(value => value.Target == owner.Owner.Target))
            {
                throw new InvalidOperationException(
                    "The requested SubVoice event-lane owner was added after the command was prepared.");
            }
        }

        foreach (CreatedEventLaneOwner owner in owners)
        {
            if ((uint)owner.Index > (uint)owner.Voice.EventMappings.Count)
                throw new InvalidOperationException("The event-lane owner insertion index is no longer valid.");
        }

        bool parameterAttached = false;
        bool functionAttached = false;
        int ownerCount = 0;
        bool mappingsTouched = false;
        try
        {
            instrument.LogicalParameters.Add(parameter);
            parameterAttached = true;
            if (function is not null)
            {
                instrument.MappingFunctions.Add(function);
                functionAttached = true;
            }
            foreach (CreatedEventLaneOwner owner in owners)
            {
                owner.Voice.EventMappings.Insert(owner.Index, owner.Owner);
                ownerCount++;
            }
            instrument.ParameterMappings.Clear();
            mappingsTouched = true;
            instrument.ParameterMappings.AddRange(replacementMappings);
        }
        catch
        {
            if (mappingsTouched)
            {
                instrument.ParameterMappings.Clear();
                instrument.ParameterMappings.AddRange(expectedMappings);
            }
            for (int index = ownerCount - 1; index >= 0; index--)
                _ = owners[index].Voice.EventMappings.Remove(owners[index].Owner);
            if (functionAttached) _ = instrument.MappingFunctions.Remove(function!);
            if (parameterAttached) _ = instrument.LogicalParameters.Remove(parameter);
            throw;
        }
    }

    private static void UndoLogicalParameterEventBinding(
        EventInstrument instrument,
        IReadOnlyList<LogicalParameterMapping> originalMappings,
        IReadOnlyList<LogicalParameterMapping> expectedCurrentMappings,
        LogicalParameterDefinition parameter,
        CSharpMappingFunction? function,
        IReadOnlyList<CreatedEventLaneOwner> owners)
    {
        RequireReferenceSequence(
            instrument.ParameterMappings,
            expectedCurrentMappings,
            "Logical Parameter Mapping order changed before undoing the event binding.");
        if (!instrument.LogicalParameters.Contains(parameter))
            throw new InvalidOperationException("The created Logical Parameter is missing before Undo.");
        if (function is not null && !instrument.MappingFunctions.Contains(function))
            throw new InvalidOperationException("The created Mapping Function is missing before Undo.");
        if (owners.Any(value => !value.Voice.EventMappings.Contains(value.Owner)))
            throw new InvalidOperationException("A created SubVoice event-lane owner is missing before Undo.");
        instrument.ParameterMappings.Clear();
        instrument.ParameterMappings.AddRange(originalMappings);
        for (int index = owners.Count - 1; index >= 0; index--)
        {
            RemoveRequired(
                owners[index].Voice.EventMappings,
                owners[index].Owner,
                "SubVoice event-lane owner");
        }
        if (function is not null)
            RemoveRequired(instrument.MappingFunctions, function, "Mapping Function");
        RemoveRequired(instrument.LogicalParameters, parameter, "Logical Parameter");
    }

    private static void RequireReferenceSequence<T>(
        IReadOnlyList<T> actual,
        IReadOnlyList<T> expected,
        string message)
        where T : class
    {
        if (actual.Count != expected.Count
            || !actual.SequenceEqual(expected, ReferenceEqualityComparer.Instance))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record FrozenLogicalParameterEventBindingRequest(
        string Name,
        MidiValueTarget Target,
        LogicalParameterEventBindingOperation Operation,
        LogicalParameterEventBindingSubVoiceScope SubVoiceScope,
        MidoraId[] SubVoiceIds,
        int SourceMinimum,
        int SourceMaximum,
        double? FactorMinimum,
        double? FactorMaximum,
        LogicalParameterEventBindingConflictPolicy ConflictPolicy);

    private sealed record CreatedEventLaneOwner(
        SubVoice Voice,
        SubVoiceEventMapping Owner,
        int Index);
}
