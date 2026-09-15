using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopySubVoice(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        SubVoice voice = instrument.SubVoices
            .SingleOrDefault(value => value.Id == subVoiceId)
            ?? throw new ArgumentOutOfRangeException(nameof(subVoiceId));
        ClipboardCaptureScope.ReserveMetadata(checked((long)voice.EventMappings.Count + voice.Curves.Count
            + instrument.LogicalParameters.Count + instrument.Envelopes.Count + instrument.MappingFunctions.Count));
        foreach (SubVoiceEventMapping mapping in voice.EventMappings)
            ClipboardCaptureScope.ReserveMetadata(mapping.Steps.Count);
        MappingStepClipboardSnapshot[] mappingSteps = voice.EventMappings
            .SelectMany(value => value.Steps)
            .Select(SnapshotMappingStep)
            .ToArray();
        HashSet<MidoraId> parameterIds = mappingSteps
            .Where(value => value.LogicalParameterId.HasValue)
            .Select(value => value.LogicalParameterId!.Value)
            .ToHashSet();
        HashSet<MidoraId> envelopeIds = mappingSteps
            .Where(value => value.EnvelopeId.HasValue)
            .Select(value => value.EnvelopeId!.Value)
            .ToHashSet();
        HashSet<MidoraId> functionIds = mappingSteps
            .Where(value => value.MappingFunctionId.HasValue)
            .Select(value => value.MappingFunctionId!.Value)
            .ToHashSet();
        SubVoiceClipboardSnapshot snapshot = new(
            voice.Name,
            voice.RootNoteOverride,
            SnapshotState(voice.InitialState),
            voice.EventMappings.Select(value => new SubVoiceEventMappingClipboardSnapshot(
                value.Target,
                SnapshotMappingChain(value.Steps),
                new(
                    value.TargetSettings.Rounding,
                    value.TargetSettings.Overflow))).ToArray(),
            ClipboardCaptureScope.Capture(voice.Events.CreateQuerySnapshot().EnumerateAll().Select(value =>
                new TemplateEventClipboardSnapshot(value.Kind, value.Tick, value.LengthTicks, value.Number,
                    value.Value, value.SecondaryValue, value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta)), voice.Events.Count),
            voice.Curves.Select(value => new ValueCurveClipboardSnapshot(
                value.Target,
                new(value.TargetSettings.Rounding, value.TargetSettings.Overflow),
                ClipboardCaptureScope.Capture(value.Points.CreateQuerySnapshot().EnumerateAll().Select(point => new CurvePointClipboardSnapshot(
                    point.Tick,
                    point.Value,
                    point.Interpolation)), value.Points.Count))).ToArray(),
            instrument.LogicalParameters
                .Where(value => parameterIds.Contains(value.Id))
                .Select(SnapshotLogicalParameter)
                .ToArray(),
            instrument.Envelopes
                .Where(value => envelopeIds.Contains(value.Id))
                .Select(SnapshotEnvelope)
                .ToArray(),
            instrument.MappingFunctions
                .Where(value => functionIds.Contains(value.Id))
                .Select(SnapshotMappingFunction)
                .ToArray())
        {
            InstrumentChanges = ClipboardCaptureScope.Capture(InstrumentChangeCopies.Capture(
                voice.InstrumentChanges, voice.Events.CreateQuerySnapshot()), voice.InstrumentChanges.Count)
        };
        string name = string.IsNullOrWhiteSpace(voice.Name) ? "SubVoice" : voice.Name;
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.SubVoice,
            1,
            $"1 SubVoice · {name}",
            new SubVoiceClipboardData(eventInstrumentId, snapshot));
    }

    public static IProjectEditCommand CreatePasteSubVoiceCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        int? insertionIndex = null)
    {
        SubVoiceClipboardData data = RequirePayload<SubVoiceClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.SubVoice);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteSubVoiceClipboard(
            data,
            targetEventInstrumentId,
            insertionIndex));
    }

    private static MappingStepClipboardSnapshot SnapshotMappingStep(ValueMappingStep value) => new(
        value.IsEnabled,
        value.Source,
        value.Operation,
        value.LogicalParameterId,
        value.EnvelopeId,
        value.MappingFunctionId,
        value.Constant,
        value.SourceMinimum,
        value.SourceMaximum,
        value.TargetMinimum,
        value.TargetMaximum,
        value.InputOverflow,
        value.DivideByZero);

    private static MidiInitialStateClipboardSnapshot SnapshotState(MidiInitialState value)
    {
        ClipboardCaptureScope.ReserveMetadata(checked((long)value.Controllers.Count
            + value.RegisteredParameters.Count + value.NonRegisteredParameters.Count), 128);
        return new(
        value.BankMsb,
        value.BankLsb,
        value.Program,
        value.PitchBend,
        value.PitchBendRangeSemitones,
        value.PitchBendRangeCents,
        value.Controllers.OrderBy(item => item.Key).ToArray(),
        value.RegisteredParameters.OrderBy(item => item.Key).ToArray(),
        value.NonRegisteredParameters.OrderBy(item => item.Key).ToArray());
    }

    private static LogicalParameterClipboardSnapshot SnapshotLogicalParameter(
        LogicalParameterDefinition value)
    {
        ClipboardCaptureScope.ReserveMetadata(value.EnumItems.Count, 128);
        return new(
        value.Id,
        value.Name,
        value.Type,
        value.Minimum,
        value.Maximum,
        value.DisplayMinimum,
        value.DisplayMaximum,
        value.DefaultValue,
        value.UsesExplicitEnumValues,
        value.EnumItems.Select(item => new LogicalParameterEnumClipboardSnapshot(
            item.Name,
            item.Value)).ToArray());
    }

    private static InstrumentEnvelopeClipboardSnapshot SnapshotEnvelope(InstrumentEnvelope value) => new(
        value.Id,
        value.Name,
        value.DelayTicks,
        value.AttackTicks,
        value.HoldTicks,
        value.DecayTicks,
        value.StartValue,
        value.PeakValue,
        value.SustainValue,
        value.ReleaseTicks,
        value.EndValue);

    private static MappingFunctionClipboardSnapshot SnapshotMappingFunction(CSharpMappingFunction value) => new(
        value.Id,
        value.Name,
        value.Body,
        value.AbiVersion,
        value.DeclaredContextFields.Order(StringComparer.Ordinal).ToArray());
}

internal sealed record SubVoiceClipboardData(
    MidoraId SourceEventInstrumentId,
    SubVoiceClipboardSnapshot SubVoice) : ProjectObjectClipboardData;

internal sealed record SubVoiceClipboardSnapshot(
    string? Name,
    int? RootNoteOverride,
    MidiInitialStateClipboardSnapshot InitialState,
    SubVoiceEventMappingClipboardSnapshot[] EventMappings,
    IReadOnlyList<TemplateEventClipboardSnapshot> Events,
    ValueCurveClipboardSnapshot[] Curves,
    LogicalParameterClipboardSnapshot[] LogicalParameters,
    InstrumentEnvelopeClipboardSnapshot[] Envelopes,
    MappingFunctionClipboardSnapshot[] MappingFunctions)
{
    public IReadOnlyList<InstrumentChangeClipboardRecord> InstrumentChanges { get; init; } = [];
}

internal sealed record SubVoiceEventMappingClipboardSnapshot(
    TemplateEventMappingTarget Target,
    MappingChainClipboardSnapshot Steps,
    IntegerTargetSettingsClipboardSnapshot TargetSettings);

internal sealed record MidiInitialStateClipboardSnapshot(
    int? BankMsb,
    int? BankLsb,
    int? Program,
    int? PitchBend,
    int? PitchBendRangeSemitones,
    int? PitchBendRangeCents,
    KeyValuePair<int, int>[] Controllers,
    KeyValuePair<int, int>[] RegisteredParameters,
    KeyValuePair<int, int>[] NonRegisteredParameters);

internal sealed record ValueCurveClipboardSnapshot(
    MidiValueTarget Target,
    IntegerTargetSettingsClipboardSnapshot TargetSettings,
    IReadOnlyList<CurvePointClipboardSnapshot> Points);

internal sealed record LogicalParameterClipboardSnapshot(
    MidoraId SourceId,
    string Name,
    LogicalParameterType Type,
    double Minimum,
    double Maximum,
    double DisplayMinimum,
    double DisplayMaximum,
    double DefaultValue,
    bool UsesExplicitEnumValues,
    LogicalParameterEnumClipboardSnapshot[] EnumItems);

internal sealed record LogicalParameterEnumClipboardSnapshot(string Name, int Value);

internal sealed record InstrumentEnvelopeClipboardSnapshot(
    MidoraId SourceId,
    string? Name,
    long DelayTicks,
    long AttackTicks,
    long HoldTicks,
    long DecayTicks,
    double StartValue,
    double PeakValue,
    double SustainValue,
    long ReleaseTicks,
    double EndValue);

internal sealed record MappingFunctionClipboardSnapshot(
    MidoraId SourceId,
    string Name,
    string Body,
    int AbiVersion,
    string[] DeclaredContextFields);

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteSubVoiceClipboard(
        SubVoiceClipboardData data,
        MidoraId targetEventInstrumentId,
        int? insertionIndex) =>
        Command("Paste SubVoice", project =>
        {
            ArgumentNullException.ThrowIfNull(data);
            EventInstrument target = FindEventInstrument(project, targetEventInstrumentId);
            EnsureSubVoiceCapacity(target);
            int index = insertionIndex ?? target.SubVoices.Count;
            ValidateInsertionIndex(index, target.SubVoices.Count, nameof(insertionIndex));
            bool sameInstrument = data.SourceEventInstrumentId == targetEventInstrumentId;
            if (!sameInstrument) ValidateDependencyClosure(data.SubVoice);
            LogicalParameterDefinition[]? parameters = null;
            InstrumentEnvelope[]? envelopes = null;
            CSharpMappingFunction[]? functions = null;
            SubVoice? copy = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (copy is null)
                    {
                        Dictionary<MidoraId, MidoraId> parameterMap = [];
                        Dictionary<MidoraId, MidoraId> envelopeMap = [];
                        Dictionary<MidoraId, MidoraId> functionMap = [];
                        if (sameInstrument)
                        {
                            foreach (LogicalParameterClipboardSnapshot value in data.SubVoice.LogicalParameters)
                                parameterMap[value.SourceId] = value.SourceId;
                            foreach (InstrumentEnvelopeClipboardSnapshot value in data.SubVoice.Envelopes)
                                envelopeMap[value.SourceId] = value.SourceId;
                            foreach (MappingFunctionClipboardSnapshot value in data.SubVoice.MappingFunctions)
                                functionMap[value.SourceId] = value.SourceId;
                            parameters = [];
                            envelopes = [];
                            functions = [];
                        }
                        else
                        {
                            HashSet<string> parameterNames = target.LogicalParameters
                                .Select(value => value.Name)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            HashSet<string> functionNames = target.MappingFunctions
                                .Select(value => value.Name)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            parameters = data.SubVoice.LogicalParameters
                                .Select(value => CreateParameterCopy(owner, value, parameterMap, parameterNames))
                                .ToArray();
                            envelopes = data.SubVoice.Envelopes
                                .Select(value => CreateEnvelopeCopy(owner, value, envelopeMap))
                                .ToArray();
                            functions = data.SubVoice.MappingFunctions
                                .Select(value => CreateFunctionCopy(owner, value, functionMap, functionNames))
                                .ToArray();
                        }
                        copy = CreateSubVoiceCopy(
                            owner,
                            data.SubVoice,
                            parameterMap,
                            envelopeMap,
                            functionMap);
                    }
                    target.LogicalParameters.AddRange(parameters!);
                    target.Envelopes.AddRange(envelopes!);
                    target.MappingFunctions.AddRange(functions!);
                    InsertAt(target.SubVoices, index, copy, "pasted SubVoice");
                },
                _ =>
                {
                    if (copy is null || parameters is null || envelopes is null || functions is null)
                        throw new InvalidOperationException("The pasted SubVoice does not exist before Apply.");
                    RemoveRequired(target.SubVoices, copy, "pasted SubVoice");
                    foreach (LogicalParameterDefinition value in parameters)
                        RemoveRequired(target.LogicalParameters, value, "pasted Logical Parameter dependency");
                    foreach (InstrumentEnvelope value in envelopes)
                        RemoveRequired(target.Envelopes, value, "pasted Envelope dependency");
                    foreach (CSharpMappingFunction value in functions)
                        RemoveRequired(target.MappingFunctions, value, "pasted Mapping Function dependency");
                });
        });

    private static SubVoice CreateSubVoiceCopy(
        MidoraProject project,
        SubVoiceClipboardSnapshot snapshot,
        IReadOnlyDictionary<MidoraId, MidoraId> parameters,
        IReadOnlyDictionary<MidoraId, MidoraId> envelopes,
        IReadOnlyDictionary<MidoraId, MidoraId> functions)
    {
        SubVoice result = new(project)
        {
            Name = snapshot.Name,
            RootNoteOverride = snapshot.RootNoteOverride
        };
        ApplyState(result.InitialState, snapshot.InitialState);
        foreach (SubVoiceEventMappingClipboardSnapshot value in snapshot.EventMappings)
        {
            MappingChainClipboardSnapshot remapped = RemapChain(
                value.Steps,
                parameters,
                envelopes,
                functions);
            SubVoiceEventMapping mapping = new(project, value.Target);
            ApplyMappingChainClipboard(project, mapping.Steps, remapped);
            SetTargetSettings(
                mapping.TargetSettings,
                new(value.TargetSettings.Rounding, value.TargetSettings.Overflow));
            result.EventMappings.Add(mapping);
        }
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        long firstEventId = project.NextStableId;
        var events = FreezeClipboardSource(FirstTemplateClipboardValues(snapshot.Events.Select(value =>
        {
            _ = PrepareTemplateEventClipboardValue(value, editCursorTick: 0);
            return new TemplateEventSnapshotValue(project.AllocateStableId(), value.Kind, value.TickOffset,
                value.LengthTicks, value.Number, value.Value, value.SecondaryValue,
                value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta);
        })), static value => value.Id);
        result.Events.AdoptSource(project, events, scope.Token);
        result.InstrumentChanges = InstrumentChangeCopies.Restore(project, snapshot.InstrumentChanges, firstEventId, false,
            group => InstrumentChangeResolver.TryRead(result, group, out _));
        foreach (ValueCurveClipboardSnapshot value in snapshot.Curves)
        {
            scope.Token.ThrowIfCancellationRequested();
            ValueCurve curve = new(project) { Target = value.Target };
            SetTargetSettings(curve.TargetSettings, new(value.TargetSettings.Rounding, value.TargetSettings.Overflow));
            var points = FreezeClipboardSource(FirstClipboardValues(value.Points.Select(point =>
                new CurvePointSnapshotValue(project.AllocateStableId(), point.Tick, point.Value, point.Interpolation)),
                static point => point.Tick, static _ => 0), static point => point.Id);
            curve.Points.AdoptSource(project, points, scope.Token);
            result.Curves.Add(curve);
        }
        return result;
    }

    private static MappingChainClipboardSnapshot RemapChain(
        MappingChainClipboardSnapshot value,
        IReadOnlyDictionary<MidoraId, MidoraId> parameters,
        IReadOnlyDictionary<MidoraId, MidoraId> envelopes,
        IReadOnlyDictionary<MidoraId, MidoraId> functions) =>
        value with
        {
            Steps = value.Steps.Select(step => step with
            {
                LogicalParameterId = Remap(parameters, step.LogicalParameterId),
                EnvelopeId = Remap(envelopes, step.EnvelopeId),
                MappingFunctionId = Remap(functions, step.MappingFunctionId)
            }).ToArray()
        };

    private static MidoraId? Remap(IReadOnlyDictionary<MidoraId, MidoraId> map, MidoraId? value) =>
        value.HasValue && map.TryGetValue(value.Value, out MidoraId replacement)
            ? replacement
            : value;

    private static LogicalParameterDefinition CreateParameterCopy(
        MidoraProject project,
        LogicalParameterClipboardSnapshot value,
        IDictionary<MidoraId, MidoraId> map,
        ISet<string> reservedNames)
    {
        LogicalParameterDefinition result = new(project)
        {
            Name = ReserveUniqueName(reservedNames, value.Name),
            Type = value.Type,
            Minimum = value.Minimum,
            Maximum = value.Maximum,
            DisplayMinimum = value.DisplayMinimum,
            DisplayMaximum = value.DisplayMaximum,
            DefaultValue = value.DefaultValue,
            UsesExplicitEnumValues = value.UsesExplicitEnumValues
        };
        foreach (LogicalParameterEnumClipboardSnapshot item in value.EnumItems)
            result.EnumItems.Add(new(project) { Name = item.Name, Value = item.Value });
        map[value.SourceId] = result.Id;
        return result;
    }

    private static InstrumentEnvelope CreateEnvelopeCopy(
        MidoraProject project,
        InstrumentEnvelopeClipboardSnapshot value,
        IDictionary<MidoraId, MidoraId> map)
    {
        InstrumentEnvelope result = new(project)
        {
            Name = value.Name,
            DelayTicks = value.DelayTicks,
            AttackTicks = value.AttackTicks,
            HoldTicks = value.HoldTicks,
            DecayTicks = value.DecayTicks,
            StartValue = value.StartValue,
            PeakValue = value.PeakValue,
            SustainValue = value.SustainValue,
            ReleaseTicks = value.ReleaseTicks,
            EndValue = value.EndValue
        };
        map[value.SourceId] = result.Id;
        return result;
    }

    private static CSharpMappingFunction CreateFunctionCopy(
        MidoraProject project,
        MappingFunctionClipboardSnapshot value,
        IDictionary<MidoraId, MidoraId> map,
        ISet<string> reservedNames)
    {
        CSharpMappingFunction result = new(project)
        {
            Name = ReserveUniqueName(reservedNames, value.Name),
            Body = value.Body,
            AbiVersion = value.AbiVersion
        };
        result.DeclaredContextFields.UnionWith(value.DeclaredContextFields);
        map[value.SourceId] = result.Id;
        return result;
    }

    private static string ReserveUniqueName(ISet<string> reserved, string requested)
    {
        if (reserved.Add(requested)) return requested;
        for (int index = 2; ; index++)
        {
            string candidate = $"{requested} Copy {index}";
            if (reserved.Add(candidate)) return candidate;
        }
    }

    private static void ValidateDependencyClosure(SubVoiceClipboardSnapshot snapshot)
    {
        HashSet<MidoraId> parameters = snapshot.LogicalParameters.Select(value => value.SourceId).ToHashSet();
        HashSet<MidoraId> envelopes = snapshot.Envelopes.Select(value => value.SourceId).ToHashSet();
        HashSet<MidoraId> functions = snapshot.MappingFunctions.Select(value => value.SourceId).ToHashSet();
        foreach (MappingStepClipboardSnapshot step in snapshot.EventMappings
                     .SelectMany(value => value.Steps.Steps))
        {
            if (step.LogicalParameterId is MidoraId parameterId && !parameters.Contains(parameterId)
                || step.EnvelopeId is MidoraId envelopeId && !envelopes.Contains(envelopeId)
                || step.MappingFunctionId is MidoraId functionId && !functions.Contains(functionId))
            {
                throw new InvalidOperationException(
                    "The copied SubVoice contains a broken Mapping dependency and cannot be pasted into another Event Instrument.");
            }
        }
    }

    private static void ApplyState(MidiInitialState target, MidiInitialStateClipboardSnapshot value)
    {
        target.BankMsb = value.BankMsb;
        target.BankLsb = value.BankLsb;
        target.Program = value.Program;
        target.PitchBend = value.PitchBend;
        target.PitchBendRangeSemitones = value.PitchBendRangeSemitones;
        target.PitchBendRangeCents = value.PitchBendRangeCents;
        foreach ((int key, int item) in value.Controllers) target.Controllers.Add(key, item);
        foreach ((int key, int item) in value.RegisteredParameters) target.RegisteredParameters.Add(key, item);
        foreach ((int key, int item) in value.NonRegisteredParameters) target.NonRegisteredParameters.Add(key, item);
    }
}
