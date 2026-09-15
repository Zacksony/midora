using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyLogicalParameterDefinition(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId parameterId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        EventInstrument instrument = FindClipboardInstrument(document, eventInstrumentId);
        LogicalParameterDefinition parameter = instrument.LogicalParameters
            .SingleOrDefault(value => value.Id == parameterId)
            ?? throw new ArgumentOutOfRangeException(nameof(parameterId));
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalParameterDefinition,
            1,
            $"1 Logical Parameter · {parameter.Name}",
            new LogicalParameterDefinitionClipboardData(
                eventInstrumentId,
                SnapshotLogicalParameter(parameter)));
    }

    public static ProjectObjectClipboardPayload CopyLogicalParameterMapping(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId mappingId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        EventInstrument instrument = FindClipboardInstrument(document, eventInstrumentId);
        LogicalParameterMapping mapping = instrument.ParameterMappings
            .SingleOrDefault(value => value.Id == mappingId)
            ?? throw new ArgumentOutOfRangeException(nameof(mappingId));
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.LogicalParameterMapping,
            1,
            "1 Logical Parameter Mapping",
            new LogicalParameterMappingClipboardData(
                eventInstrumentId,
                new(mapping.TargetSettings.Rounding, mapping.TargetSettings.Overflow),
                SnapshotMappingChain(mapping.Steps)));
    }

    public static ProjectObjectClipboardPayload CopyMappingStep(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MidoraId mappingStepId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        EventInstrument instrument = FindClipboardInstrument(document, eventInstrumentId);
        MappingChain chain = ProjectDomainEditCommands.FindMappingChainForClipboard(
            instrument,
            mappingChainId);
        ValueMappingStep step = chain.SingleOrDefault(value => value.Id == mappingStepId)
            ?? throw new ArgumentOutOfRangeException(nameof(mappingStepId));
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.MappingStep,
            1,
            "1 Mapping Step",
            new MappingStepClipboardData(eventInstrumentId, SnapshotMappingStep(step)));
    }

    public static ProjectObjectClipboardPayload CopyEnvelopePreset(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId envelopeId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        EventInstrument instrument = FindClipboardInstrument(document, eventInstrumentId);
        InstrumentEnvelope envelope = instrument.Envelopes
            .SingleOrDefault(value => value.Id == envelopeId)
            ?? throw new ArgumentOutOfRangeException(nameof(envelopeId));
        string name = string.IsNullOrWhiteSpace(envelope.Name) ? "Envelope Preset" : envelope.Name;
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.EnvelopePreset,
            1,
            $"1 Envelope Preset · {name}",
            new EnvelopePresetClipboardData(eventInstrumentId, SnapshotEnvelope(envelope)));
    }

    public static ProjectObjectClipboardPayload CopyMappingFunction(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId mappingFunctionId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        EventInstrument instrument = FindClipboardInstrument(document, eventInstrumentId);
        CSharpMappingFunction function = instrument.MappingFunctions
            .SingleOrDefault(value => value.Id == mappingFunctionId)
            ?? throw new ArgumentOutOfRangeException(nameof(mappingFunctionId));
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.MappingFunction,
            1,
            $"1 Mapping Function · {function.Name}",
            new MappingFunctionDefinitionClipboardData(
                eventInstrumentId,
                SnapshotMappingFunction(function)));
    }

    public static IProjectEditCommand CreatePasteLogicalParameterDefinitionCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        int? insertionIndex = null) =>
        KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalParameterDefinitionClipboard(
            RequirePayload<LogicalParameterDefinitionClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.LogicalParameterDefinition),
            targetEventInstrumentId,
            insertionIndex));

    public static IProjectEditCommand CreatePasteLogicalParameterMappingCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingId,
        bool nonEmptyReplacementConfirmed) =>
        KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteLogicalParameterMappingClipboard(
            RequirePayload<LogicalParameterMappingClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.LogicalParameterMapping),
            targetEventInstrumentId,
            targetMappingId,
            nonEmptyReplacementConfirmed));

    public static IProjectEditCommand CreatePasteMappingStepCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingChainId,
        int? insertionIndex = null) =>
        KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteMappingStepClipboard(
            RequirePayload<MappingStepClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.MappingStep),
            targetEventInstrumentId,
            targetMappingChainId,
            insertionIndex));

    public static IProjectEditCommand CreatePasteEnvelopePresetCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        int? insertionIndex = null) =>
        KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteEnvelopePresetClipboard(
            RequirePayload<EnvelopePresetClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.EnvelopePreset),
            targetEventInstrumentId,
            insertionIndex));

    public static IProjectEditCommand CreatePasteMappingFunctionCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        int? insertionIndex = null) =>
        KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteMappingFunctionClipboard(
            RequirePayload<MappingFunctionDefinitionClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.MappingFunction),
            targetEventInstrumentId,
            insertionIndex));

    public static ProjectObjectClipboardCutPreparation PrepareCutLogicalParameterDefinition(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId parameterId) =>
        PrepareCut(
            document,
            CopyLogicalParameterDefinition(document, eventInstrumentId, parameterId),
            ProjectDomainEditCommands.DeleteLogicalParameter(
                eventInstrumentId,
                parameterId,
                referencedDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutLogicalParameterMapping(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId mappingId) =>
        PrepareCut(
            document,
            CopyLogicalParameterMapping(document, eventInstrumentId, mappingId),
            ProjectDomainEditCommands.DeleteLogicalParameterMapping(
                eventInstrumentId,
                mappingId,
                deletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutMappingStep(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId mappingChainId,
        MidoraId mappingStepId) =>
        PrepareCut(
            document,
            CopyMappingStep(document, eventInstrumentId, mappingChainId, mappingStepId),
            ProjectDomainEditCommands.DeleteMappingStep(
                eventInstrumentId,
                mappingChainId,
                mappingStepId));

    public static ProjectObjectClipboardCutPreparation PrepareCutEnvelopePreset(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId envelopeId) =>
        PrepareCut(
            document,
            CopyEnvelopePreset(document, eventInstrumentId, envelopeId),
            ProjectDomainEditCommands.DeleteInstrumentEnvelope(
                eventInstrumentId,
                envelopeId,
                referencedDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutMappingFunction(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId mappingFunctionId) =>
        PrepareCut(
            document,
            CopyMappingFunction(document, eventInstrumentId, mappingFunctionId),
            ProjectDomainEditCommands.DeleteMappingFunction(
                eventInstrumentId,
                mappingFunctionId,
                referencedDeletionConfirmed: true));

    private static EventInstrument FindClipboardInstrument(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
    }
}

internal sealed record LogicalParameterDefinitionClipboardData(
    MidoraId SourceEventInstrumentId,
    LogicalParameterClipboardSnapshot Parameter) : ProjectObjectClipboardData;

internal sealed record LogicalParameterMappingClipboardData(
    MidoraId SourceEventInstrumentId,
    IntegerTargetSettingsClipboardSnapshot TargetSettings,
    MappingChainClipboardSnapshot Steps) : ProjectObjectClipboardData;

internal sealed record MappingStepClipboardData(
    MidoraId SourceEventInstrumentId,
    MappingStepClipboardSnapshot Step) : ProjectObjectClipboardData;

internal sealed record EnvelopePresetClipboardData(
    MidoraId SourceEventInstrumentId,
    InstrumentEnvelopeClipboardSnapshot Envelope) : ProjectObjectClipboardData;

internal sealed record MappingFunctionDefinitionClipboardData(
    MidoraId SourceEventInstrumentId,
    MappingFunctionClipboardSnapshot Function) : ProjectObjectClipboardData;

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteLogicalParameterDefinitionClipboard(
        LogicalParameterDefinitionClipboardData data,
        MidoraId targetEventInstrumentId,
        int? insertionIndex) =>
        Command("Paste Logical Parameter", project =>
        {
            ArgumentNullException.ThrowIfNull(data);
            EventInstrument target = FindEventInstrument(project, targetEventInstrumentId);
            int index = insertionIndex ?? target.LogicalParameters.Count;
            ValidateInsertionIndex(index, target.LogicalParameters.Count, nameof(insertionIndex));
            LogicalParameterDefinition? copy = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (copy is null)
                    {
                        Dictionary<MidoraId, MidoraId> map = [];
                        HashSet<string> names = target.LogicalParameters
                            .Select(value => value.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        copy = CreateParameterCopy(owner, data.Parameter, map, names);
                    }
                    InsertAt(target.LogicalParameters, index, copy, "pasted Logical Parameter");
                },
                _ => RemoveRequired(
                    target.LogicalParameters,
                    copy ?? throw new InvalidOperationException(
                        "The pasted Logical Parameter does not exist before Apply."),
                    "pasted Logical Parameter"));
        });

    internal static IProjectEditCommand PasteLogicalParameterMappingClipboard(
        LogicalParameterMappingClipboardData data,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingId,
        bool nonEmptyReplacementConfirmed) =>
        Command("Paste Logical Parameter Mapping", project =>
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "Logical Parameter Mapping configuration can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            LogicalParameterMapping target = FindLogicalParameterMapping(instrument, targetMappingId);
            ValidateIntegerTargetSettings(data.TargetSettings.Rounding, data.TargetSettings.Overflow);
            ValidateMappingChainClipboard(data.Steps);
            if (target.Steps.Count != 0 && !nonEmptyReplacementConfirmed)
            {
                throw new InvalidOperationException(
                    "Replacing a non-empty Logical Parameter Mapping requires explicit confirmation.");
            }
            MappingChain oldSteps = target.Steps;
            MappingRounding oldRounding = target.TargetSettings.Rounding;
            MappingOverflow oldOverflow = target.TargetSettings.Overflow;
            MappingChain? replacement = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (replacement is null)
                    {
                        replacement = new MappingChain(owner);
                        ApplyMappingChainClipboard(owner, replacement, data.Steps);
                    }
                    target.Steps = replacement;
                    target.TargetSettings.Rounding = data.TargetSettings.Rounding;
                    target.TargetSettings.Overflow = data.TargetSettings.Overflow;
                },
                _ =>
                {
                    target.Steps = oldSteps;
                    target.TargetSettings.Rounding = oldRounding;
                    target.TargetSettings.Overflow = oldOverflow;
                });
        });

    internal static IProjectEditCommand PasteMappingStepClipboard(
        MappingStepClipboardData data,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingChainId,
        int? insertionIndex) =>
        Command("Paste Mapping Step", project =>
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "Mapping Steps can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            MappingChain chain = FindMappingChain(instrument, targetMappingChainId);
            ValidateMappingStepValue(ToMappingStepValue(data.Step));
            int index = insertionIndex ?? chain.Count;
            ValidateInsertionIndex(index, chain.Count, nameof(insertionIndex));
            ValueMappingStep? copy = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (copy is null)
                    {
                        copy = new(owner) { IsEnabled = data.Step.IsEnabled };
                        SetMappingStep(copy, ToMappingStepValue(data.Step));
                    }
                    InsertAt(chain, index, copy, "pasted Mapping Step");
                },
                _ => RemoveRequired(
                    chain,
                    copy ?? throw new InvalidOperationException(
                        "The pasted Mapping Step does not exist before Apply."),
                    "pasted Mapping Step"));
        });

    internal static IProjectEditCommand PasteEnvelopePresetClipboard(
        EnvelopePresetClipboardData data,
        MidoraId targetEventInstrumentId,
        int? insertionIndex) =>
        Command("Paste Envelope Preset", project =>
        {
            ArgumentNullException.ThrowIfNull(data);
            EventInstrument target = FindEventInstrument(project, targetEventInstrumentId);
            if (!target.RequiresChannelIsolation)
            {
                throw new InvalidOperationException(
                    "Envelope Presets cannot be pasted while Per-Note Instance Isolation is disabled.");
            }
            int index = insertionIndex ?? target.Envelopes.Count;
            ValidateInsertionIndex(index, target.Envelopes.Count, nameof(insertionIndex));
            InstrumentEnvelope? copy = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (copy is null)
                    {
                        Dictionary<MidoraId, MidoraId> map = [];
                        copy = CreateEnvelopeCopy(owner, data.Envelope, map);
                    }
                    InsertAt(target.Envelopes, index, copy, "pasted Envelope Preset");
                },
                _ => RemoveRequired(
                    target.Envelopes,
                    copy ?? throw new InvalidOperationException(
                        "The pasted Envelope Preset does not exist before Apply."),
                    "pasted Envelope Preset"));
        });

    internal static IProjectEditCommand PasteMappingFunctionClipboard(
        MappingFunctionDefinitionClipboardData data,
        MidoraId targetEventInstrumentId,
        int? insertionIndex) =>
        Command("Paste Mapping Function", project =>
        {
            ArgumentNullException.ThrowIfNull(data);
            EventInstrument target = FindEventInstrument(project, targetEventInstrumentId);
            int index = insertionIndex ?? target.MappingFunctions.Count;
            ValidateInsertionIndex(index, target.MappingFunctions.Count, nameof(insertionIndex));
            CSharpMappingFunction? copy = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (copy is null)
                    {
                        Dictionary<MidoraId, MidoraId> map = [];
                        HashSet<string> names = target.MappingFunctions
                            .Select(value => value.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        copy = CreateFunctionCopy(owner, data.Function, map, names);
                    }
                    InsertAt(target.MappingFunctions, index, copy, "pasted Mapping Function");
                },
                _ => RemoveRequired(
                    target.MappingFunctions,
                    copy ?? throw new InvalidOperationException(
                        "The pasted Mapping Function does not exist before Apply."),
                    "pasted Mapping Function"));
        });
}
