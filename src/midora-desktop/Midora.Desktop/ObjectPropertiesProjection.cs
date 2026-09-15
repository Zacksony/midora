using System.Globalization;
using Midora.Application;
using Midora.Domain;
using Midora.Desktop.Presentation.Interaction;

namespace Midora.Desktop;

internal sealed record ObjectPropertiesSelectionContext(
    TimelineWorkspaceMode? Mode, bool IsInstrument, MidoraId? ObjectId,
    IReadOnlySet<MidoraId> Ids, WorkspaceTimelineSelectionSource? Source = null)
{
    public static ObjectPropertiesSelectionContext Capture(WorkspaceViewModel workspace) =>
        new((workspace as TimelineWorkspaceViewModel)?.Mode,
            workspace is InstrumentWorkspaceViewModel, workspace.ObjectId,
            workspace.Selection.IdSet, workspace.Selection.HomogeneousTimelineSource);
}

internal static partial class ObjectPropertiesProjection
{
    private static readonly AsyncLocal<PropertiesReadProgress?> ReadProgress = new();

    public static ObjectPropertiesViewModel ReadConductorSelection(
        ConductorTrack frozen, MidoraId id, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        token.ThrowIfCancellationRequested();
        ObjectPropertiesViewModel result = new();
        if (!TryRebuildConductorSelection(result, frozen, id, token))
            result.Replace("Missing Conductor Event", "The referenced object no longer exists.", []);
        token.ThrowIfCancellationRequested();
        return result;
    }

    public static ObjectPropertiesViewModel ReadMultiSelection(
        MidoraProject project, ObjectPropertiesSelectionContext selection,
        CancellationToken token, IProgress<TimelineEditPreparationProgress>? progress)
    {
        PropertiesReadProgress? previous = ReadProgress.Value;
        ReadProgress.Value = new(token, progress);
        try
        {
            token.ThrowIfCancellationRequested();
            ObjectPropertiesViewModel result = new();
            RebuildMultiSelection(result, project, selection);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(TimelineEditPreparationPhase.Ready, 1, 1));
            return result;
        }
        finally { ReadProgress.Value = previous; }
    }

    private sealed class PropertiesReadProgress(CancellationToken token,
        IProgress<TimelineEditPreparationProgress>? progress)
    {
        public CancellationToken Token => token;
        public IProgress<TimelineEditPreparationProgress>? Progress => progress;
        public void Checkpoint()
        {
            token.ThrowIfCancellationRequested();
        }
    }

    private static readonly string[] TimelineExactEditPrefixes =
    [
        "segment.",
        "midiSegment.",
        "note.",
        "midiNote.",
        "midiEvent.",
        "parameterPoint.",
        "template.",
        "valueCurvePoint.",
        "batch.note.",
        "batch.segment.",
        "batch.midiNote.",
        "batch.midiEvent.",
        "batch.parameterPoint.",
        "batch.valueCurvePoint."
    ];
    private static readonly string[] InstrumentStructureEditPrefixes =
    [
        "instrument.",
        "subvoice.",
        "parameter.",
        "parameterMapping.",
        "mappingChain.enabled",
        "mappingChain.rounding",
        "mappingChain.overflow",
        "mappingStep.",
        "envelope."
    ];

    public static bool CanEditInPropertiesDialog(
        WorkspaceViewModel workspace,
        ObjectPropertiesViewModel properties)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(properties);
        bool foundField = properties.Fields.Count != 0;
        foreach (PropertyField field in properties.Fields.Where(field => field.IsEditable))
        {
            if (!CanApplyFromOwnedEditor(workspace, field))
            {
                return false;
            }
        }
        return foundField;
    }

    public static bool CanApplyFromOwnedEditor(
        WorkspaceViewModel workspace,
        PropertyField field)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(field);
        if (!field.IsEditable) return false;
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            return timeline.Mode == TimelineWorkspaceMode.Conductor
                ? field.Key.StartsWith("conductor.", StringComparison.Ordinal)
                : IsTimelineExactEditKey(field.Key);
        }
        return workspace is InstrumentWorkspaceViewModel
            && (IsTimelineExactEditKey(field.Key)
                || InstrumentStructureEditPrefixes.Any(prefix =>
                    field.Key.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static bool IsTimelineExactEditKey(string key) =>
        TimelineExactEditPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    public static ObjectPropertiesViewModel CreateMappingStepCreationProperties(
        EventInstrument instrument,
        InstrumentWorkspaceViewModel workspace,
        MidoraId preferredChainId)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(workspace);
        MappingChainListItem[] chains = workspace.MappingChains.ToArray();
        if (chains.All(value => value.Id != preferredChainId))
        {
            throw new InvalidOperationException("Select a valid Mapping Chain.");
        }
        MappingSource[] sources = chains
            .Select(chain => MappingEditingPolicy.Resolve(instrument, chain.Id))
            .SelectMany(context => MappingEditingPolicy.AllowedSources(instrument, context))
            .Append(MappingSource.CurrentValue)
            .Distinct()
            .ToArray();
        bool hasCustomFunction = chains.Any(chain =>
            MappingEditingPolicy.AllowedFunctions(
                instrument,
                MappingEditingPolicy.Resolve(instrument, chain.Id)).Count != 0);
        ObjectPropertiesViewModel result = new();
        result.Replace(
            "New Mapping Step",
            "Choose the owner and every Mapping Step property before adding it. "
                + "Custom C# receives the current accumulated chain value; Source is ignored.",
            [ChoiceField(
                 "mappingStep.chain",
                 "MAPPING CHAIN",
                 preferredChainId.Value.ToString(CultureInfo.InvariantCulture),
                 chains.Select(chain => new PropertyChoiceOption(
                     chain.Id.Value.ToString(CultureInfo.InvariantCulture),
                     chain.Owner))),
             Field("mappingStep.enabled", "ENABLED", true),
             ChoiceField(
                 "mappingStep.source",
                 "SOURCE (BUILT-IN OPERATIONS ONLY)",
                 MappingSource.CurrentValue.ToString(),
                 sources.Select(source => new PropertyChoiceOption(
                     source.ToString(),
                     $"{source} — {MappingEditingPolicy.DescribeSource(source)}"))),
             ChoiceField(
                 "mappingStep.operation",
                 "OPERATION",
                 MappingOperation.Add.ToString(),
                 Enum.GetValues<MappingOperation>()
                     .Where(operation => operation != MappingOperation.CustomCSharp || hasCustomFunction)
                     .Select(operation => new PropertyChoiceOption(
                         operation.ToString(),
                         DescribeMappingOperation(operation)))),
             ChoiceField(
                 "mappingStep.logicalParameter",
                 "LOGICAL PARAMETER (WHEN SOURCE USES IT)",
                 string.Empty,
                 OptionalIdChoices(
                     instrument.LogicalParameters.Select(item => (item.Id, item.Name)),
                     null,
                     "Missing Logical Parameter")),
             ChoiceField(
                 "mappingStep.envelope",
                 "ENVELOPE PRESET (WHEN SOURCE USES IT)",
                 string.Empty,
                 OptionalIdChoices(
                     instrument.Envelopes.Select(item => (
                         item.Id,
                         string.IsNullOrWhiteSpace(item.Name) ? "Envelope Preset" : item.Name)),
                     null,
                     "Missing Envelope")),
             ChoiceField(
                 "mappingStep.function",
                 "C# MAPPING FUNCTION (CUSTOM C# OPERATION)",
                 string.Empty,
                 OptionalIdChoices(
                     instrument.MappingFunctions.Select(item => (item.Id, item.Name)),
                     null,
                     "Missing Mapping Function")),
             Field("mappingStep.constant", "CONSTANT", 0d),
             Field("mappingStep.sourceMinimum", "SOURCE MINIMUM", 0d),
             Field("mappingStep.sourceMaximum", "SOURCE MAXIMUM", 1d),
             Field("mappingStep.targetMinimum", "TARGET MINIMUM", 0d),
             Field("mappingStep.targetMaximum", "TARGET MAXIMUM", 127d),
             Field("mappingStep.inputOverflow", "INPUT OVERFLOW", MappingInputOverflow.Clamp),
             Field("mappingStep.divideByZero", "DIVIDE BY ZERO", DivideByZeroPolicy.TargetMaximum)]);
        return result;
    }

    public static IProjectEditCommand CreateMappingStepCreationCommand(
        EventInstrument instrument,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(values);
        string Required(string key) => values.TryGetValue(key, out string? value)
            ? value
            : throw new InvalidOperationException($"The required Mapping Step field '{key}' is missing.");
        MidoraId chainId = NullableId(Required("mappingStep.chain"), "Mapping Chain")
            ?? throw new InvalidOperationException("Select a Mapping Chain.");
        MappingChainEditingContext context = MappingEditingPolicy.Resolve(instrument, chainId);
        MappingOperation operation = EnumValue<MappingOperation>(Required("mappingStep.operation"), "Operation");
        MappingSource source = operation == MappingOperation.CustomCSharp
            ? MappingSource.CurrentValue
            : EnumValue<MappingSource>(Required("mappingStep.source"), "Source");
        MidoraId? functionId = NullableId(Required("mappingStep.function"), "Mapping Function");
        if (operation != MappingOperation.CustomCSharp
            && !MappingEditingPolicy.AllowedSources(instrument, context).Contains(source))
        {
            throw new InvalidOperationException(
                $"{source} is not legal in the selected Mapping Chain context.");
        }
        if (operation == MappingOperation.CustomCSharp
            && (functionId is not MidoraId selectedFunctionId
                || MappingEditingPolicy.AllowedFunctions(instrument, context)
                    .All(candidate => candidate.Id != selectedFunctionId)))
        {
            throw new InvalidOperationException(
                "Select a Mapping Function legal in the selected Mapping Chain context.");
        }
        return ProjectDomainEditCommands.CreateMappingStep(
            instrument.Id,
            chainId,
            source,
            operation,
            NullableId(Required("mappingStep.logicalParameter"), "Logical Parameter"),
            NullableId(Required("mappingStep.envelope"), "Envelope"),
            functionId,
            Double(Required("mappingStep.constant"), "Constant"),
            Double(Required("mappingStep.sourceMinimum"), "Source Minimum"),
            Double(Required("mappingStep.sourceMaximum"), "Source Maximum"),
            Double(Required("mappingStep.targetMinimum"), "Target Minimum"),
            Double(Required("mappingStep.targetMaximum"), "Target Maximum"),
            EnumValue<MappingInputOverflow>(Required("mappingStep.inputOverflow"), "Input Overflow"),
            EnumValue<DivideByZeroPolicy>(Required("mappingStep.divideByZero"), "Divide By Zero"),
            isEnabled: Bool(Required("mappingStep.enabled"), "Enabled"));
    }

    public static ObjectPropertiesViewModel CreateEnvelopeCreationProperties(string name)
    {
        ObjectPropertiesViewModel result = new();
        result.Replace(
            "New Envelope Preset",
            "Define the complete Envelope Preset before adding it.",
            [Field("envelope.name", "NAME", name),
             Field("envelope.delay", "DELAY TICKS", 0L),
             Field("envelope.attack", "ATTACK TICKS", 48L),
             Field("envelope.hold", "HOLD TICKS", 0L),
             Field("envelope.decay", "DECAY TICKS", 48L),
             Field("envelope.release", "RELEASE TICKS", 96L),
             Field("envelope.start", "START VALUE", 0d),
             Field("envelope.peak", "PEAK VALUE", 1d),
             Field("envelope.sustain", "SUSTAIN VALUE", 0.75d),
             Field("envelope.end", "END VALUE", 0d)]);
        return result;
    }

    public static IProjectEditCommand CreateEnvelopeCreationCommand(
        MidoraId instrumentId,
        IReadOnlyDictionary<string, string> values)
    {
        string Required(string key) => values.TryGetValue(key, out string? value)
            ? value
            : throw new InvalidOperationException($"The required Envelope field '{key}' is missing.");
        return ProjectDomainEditCommands.CreateInstrumentEnvelope(
            instrumentId,
            Required("envelope.name"),
            Long(Required("envelope.delay"), "Delay"),
            Long(Required("envelope.attack"), "Attack"),
            Long(Required("envelope.hold"), "Hold"),
            Long(Required("envelope.decay"), "Decay"),
            Double(Required("envelope.start"), "Start Value"),
            Double(Required("envelope.peak"), "Peak Value"),
            Double(Required("envelope.sustain"), "Sustain Value"),
            Long(Required("envelope.release"), "Release"),
            Double(Required("envelope.end"), "End Value"));
    }

    public static IProjectEditCommand CreateEditCommand(
        MidoraProject project,
        WorkspaceViewModel workspace,
        IReadOnlyDictionary<string, string> edits)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count == 0)
        {
            throw new ArgumentException("At least one property edit is required.", nameof(edits));
        }
        if (workspace.Selection.Ids.Count <= 1 && edits.Count == 1
            && !edits.Keys.Single().StartsWith("mappingStep.", StringComparison.Ordinal))
        {
            KeyValuePair<string, string> edit = edits.Single();
            return CreateEditCommand(project, workspace, edit.Key, edit.Value);
        }

        bool HasOnly(string prefix) => edits.Keys.All(key =>
            key.StartsWith(prefix, StringComparison.Ordinal));
        bool TryValue(string key, out string value) => edits.TryGetValue(key, out value!);

        if (workspace.Selection.Ids.Count > 1)
            return CreateDeferredMultiSelectionEdit(ObjectPropertiesSelectionContext.Capture(workspace), edits);

        if (HasOnly("segment."))
        {
            (LogicalTrack Track, Segment Segment) location = FindSegmentContext(project, workspace);
            Segment segment = location.Segment;
            return ProjectDomainEditCommands.SetSegmentWindow(
                segment.Id,
                TryValue("segment.start", out string start)
                    ? Long(start, "Project Start Tick")
                    : segment.ProjectStartTick,
                TryValue("segment.length", out string length)
                    ? Long(length, "Length")
                    : segment.LengthTicks,
                TryValue("segment.offset", out string offset)
                    ? Long(offset, "Content Offset")
                    : segment.ContentOffsetTick);
        }
        if (HasOnly("midiSegment."))
        {
            (PureMidiTrack Track, MidiSegment Segment) location =
                FindMidiSegmentContext(project, workspace);
            MidiSegment segment = location.Segment;
            return ProjectDomainEditCommands.SetMidiSegmentWindow(
                segment.Id,
                TryValue("midiSegment.start", out string start)
                    ? Long(start, "Project Start Tick")
                    : segment.ProjectStartTick,
                TryValue("midiSegment.length", out string length)
                    ? Long(length, "Length")
                    : segment.LengthTicks,
                TryValue("midiSegment.offset", out string offset)
                    ? Long(offset, "Content Offset")
                    : segment.ContentOffsetTick);
        }
        if (HasOnly("note."))
        {
            (Segment segment, LogicalNote note) = FindLogicalNoteContext(project, workspace);
            return ProjectDomainEditCommands.UpdateLogicalNote(
                segment.Id,
                note.Id,
                TryValue("note.start", out string start)
                    ? Long(start, "Start Tick")
                    : note.StartTick,
                TryValue("note.length", out string length)
                    ? Long(length, "Length")
                    : note.LengthTicks,
                TryValue("note.number", out string number)
                    ? Int(number, "MIDI Note")
                    : note.Note,
                TryValue("note.velocity", out string velocity)
                    ? Int(velocity, "Velocity")
                    : note.Velocity);
        }
        if (HasOnly("midiNote."))
        {
            (MidiSegment segment, DirectMidiNote note) =
                FindDirectMidiNoteContext(project, workspace);
            return ProjectDomainEditCommands.SetDirectMidiNoteValues(
                segment.Id,
                [note.Id],
                TryValue("midiNote.start", out string start)
                    ? Long(start, "Start Tick")
                    : null,
                TryValue("midiNote.length", out string length)
                    ? Long(length, "Length")
                    : null,
                TryValue("midiNote.key", out string key)
                    ? IntRange(key, "Key Number", 0, 127)
                    : null,
                TryValue("midiNote.onVelocity", out string onVelocity)
                    ? IntRange(onVelocity, "Note On Velocity", 1, 127)
                    : null,
                TryValue("midiNote.offVelocity", out string offVelocity)
                    ? IntRange(offVelocity, "Note Off Velocity", 0, 127)
                    : null);
        }
        if (HasOnly("midiEvent."))
        {
            (MidiSegment segment, DirectMidiChannelEvent directEvent) =
                FindDirectMidiEventContext(project, workspace);
            return CreateDirectMidiEventPropertyEdit(
                segment.Id,
                [directEvent.Id],
                edits,
                "midiEvent.");
        }
        if (HasOnly("parameterPoint."))
        {
            (Segment segment, LogicalParameterLane lane, CurvePoint point) =
                FindLogicalParameterPointContext(project, workspace);
            return ProjectDomainEditCommands.UpdateLogicalParameterPoint(
                segment.Id,
                lane.Id,
                point.Id,
                TryValue("parameterPoint.tick", out string tick)
                    ? Long(tick, "Tick")
                    : point.Tick,
                TryValue("parameterPoint.value", out string pointValue)
                    ? Double(pointValue, "Value")
                    : point.Value,
                CurveInterpolation.Step);
        }

        if (workspace is InstrumentWorkspaceViewModel ownedWorkspace
            && ownedWorkspace.ObjectId is MidoraId ownedInstrumentId
            && project.EventInstruments.FirstOrDefault(item => item.Id == ownedInstrumentId)
                is EventInstrument ownedInstrument)
        {
            if (HasOnly("mappingStep.")
                && ownedWorkspace.Selection.Primary is MidoraId stepId
                && FindMappingStep(ownedInstrument, stepId) is MappingStepContext stepContext)
            {
                ValueMappingStep step = stepContext.Step;
                MidoraId targetChainId = TryValue("mappingStep.chain", out string chainText)
                    ? NullableId(chainText, "Mapping Chain")
                        ?? throw new InvalidOperationException("Select a Mapping Chain.")
                    : stepContext.Chain.Id;
                MappingChainEditingContext targetContext = MappingEditingPolicy.Resolve(
                    ownedInstrument,
                    targetChainId);
                MappingSource source = TryValue("mappingStep.source", out string sourceText)
                    ? EnumValue<MappingSource>(sourceText, "Source")
                    : step.Source;
                MappingOperation operation = TryValue("mappingStep.operation", out string operationText)
                    ? EnumValue<MappingOperation>(operationText, "Operation")
                    : step.Operation;
                if (operation == MappingOperation.CustomCSharp)
                {
                    source = MappingSource.CurrentValue;
                }
                MidoraId? mappingFunctionId = TryValue("mappingStep.function", out string function)
                    ? NullableId(function, "Mapping Function")
                    : step.MappingFunctionId;
                if (operation != MappingOperation.CustomCSharp
                    && !MappingEditingPolicy.AllowedSources(ownedInstrument, targetContext).Contains(source))
                {
                    throw new InvalidOperationException(
                        $"{source} is not legal in the selected Mapping Chain context.");
                }
                if (operation == MappingOperation.CustomCSharp
                    && (mappingFunctionId is not MidoraId functionId
                        || MappingEditingPolicy.AllowedFunctions(ownedInstrument, targetContext)
                            .All(candidate => candidate.Id != functionId)))
                {
                    throw new InvalidOperationException(
                        "Select a Mapping Function legal in the selected Mapping Chain context.");
                }
                return ProjectDomainEditCommands.UpdateMappingStepAndOwner(
                    ownedInstrument.Id,
                    stepContext.Chain.Id,
                    targetChainId,
                    step.Id,
                    TryValue("mappingStep.enabled", out string enabled)
                        ? Bool(enabled, "Enabled")
                        : step.IsEnabled,
                    source,
                    operation,
                    TryValue("mappingStep.logicalParameter", out string parameter)
                        ? NullableId(parameter, "Logical Parameter")
                        : step.LogicalParameterId,
                    TryValue("mappingStep.envelope", out string envelopeText)
                        ? NullableId(envelopeText, "Envelope")
                        : step.EnvelopeId,
                    mappingFunctionId,
                    TryValue("mappingStep.constant", out string constant)
                        ? Double(constant, "Constant")
                        : step.Constant,
                    TryValue("mappingStep.sourceMinimum", out string sourceMinimum)
                        ? Double(sourceMinimum, "Source Minimum")
                        : step.SourceMinimum,
                    TryValue("mappingStep.sourceMaximum", out string sourceMaximum)
                        ? Double(sourceMaximum, "Source Maximum")
                        : step.SourceMaximum,
                    TryValue("mappingStep.targetMinimum", out string targetMinimum)
                        ? Double(targetMinimum, "Target Minimum")
                        : step.TargetMinimum,
                    TryValue("mappingStep.targetMaximum", out string targetMaximum)
                        ? Double(targetMaximum, "Target Maximum")
                        : step.TargetMaximum,
                    TryValue("mappingStep.inputOverflow", out string inputOverflow)
                        ? EnumValue<MappingInputOverflow>(inputOverflow, "Input Overflow")
                        : step.InputOverflow,
                    TryValue("mappingStep.divideByZero", out string divideByZero)
                        ? EnumValue<DivideByZeroPolicy>(divideByZero, "Divide By Zero")
                        : step.DivideByZero);
            }
            if (HasOnly("envelope.")
                && ownedWorkspace.Selection.Primary is MidoraId envelopeId
                && ownedInstrument.Envelopes.FirstOrDefault(item => item.Id == envelopeId)
                    is InstrumentEnvelope envelope)
            {
                return ProjectDomainEditCommands.UpdateInstrumentEnvelope(
                    ownedInstrument.Id,
                    envelope.Id,
                    TryValue("envelope.name", out string name) ? name : envelope.Name,
                    TryValue("envelope.delay", out string delay) ? Long(delay, "Delay") : envelope.DelayTicks,
                    TryValue("envelope.attack", out string attack) ? Long(attack, "Attack") : envelope.AttackTicks,
                    TryValue("envelope.hold", out string hold) ? Long(hold, "Hold") : envelope.HoldTicks,
                    TryValue("envelope.decay", out string decay) ? Long(decay, "Decay") : envelope.DecayTicks,
                    TryValue("envelope.start", out string start) ? Double(start, "Start Value") : envelope.StartValue,
                    TryValue("envelope.peak", out string peak) ? Double(peak, "Peak Value") : envelope.PeakValue,
                    TryValue("envelope.sustain", out string sustain) ? Double(sustain, "Sustain Value") : envelope.SustainValue,
                    TryValue("envelope.release", out string release) ? Long(release, "Release") : envelope.ReleaseTicks,
                    TryValue("envelope.end", out string end) ? Double(end, "End Value") : envelope.EndValue);
            }
            if (HasOnly("template."))
            {
                return CreateTemplateEventEdit(project, ownedInstrument, ownedWorkspace, edits);
            }
            if (HasOnly("valueCurvePoint.")
                && TryFindValueCurvePointBatch(project,
                    ownedInstrument,
                    ownedWorkspace.Selection.Ids) is { } valueCurveBatch)
            {
                return ProjectDomainEditCommands.SetValueCurvePoints(
                    ownedInstrument.Id,
                    valueCurveBatch.Voice.Id,
                    valueCurveBatch.Curve.Id,
                    ownedWorkspace.Selection.SharedIds,
                    TryValue("valueCurvePoint.tick", out string tick) ? Long(tick, "Tick") : null,
                    TryValue("valueCurvePoint.value", out string pointValue) ? Double(pointValue, "Value") : null,
                    TryValue("valueCurvePoint.interpolation", out string interpolation)
                        ? EnumValue<CurveInterpolation>(interpolation, "Interpolation")
                        : null);
            }
        }

        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor }
            && HasOnly("conductor."))
        {
            return CreateConductorEdit(project, workspace.Selection.Primary, edits);
        }

        if (workspace is InstrumentWorkspaceViewModel timingWorkspace
            && timingWorkspace.ObjectId is MidoraId timingInstrumentId
            && (edits.ContainsKey("instrument.templateLength")
                || edits.ContainsKey("instrument.preRollTicks")
                || edits.ContainsKey("instrument.loopStart")
                || edits.ContainsKey("instrument.loopEnd")))
        {
            Func<MidoraProject, IProjectEditCommand> timingFactory = current =>
            {
                EventInstrument currentInstrument = current.EventInstruments.Single(
                    item => item.Id == timingInstrumentId);
                long templateLength = TryValue("instrument.templateLength", out string length)
                    ? Long(length, "Template Length")
                    : currentInstrument.TemplateLengthTicks;
                long preRoll = TryValue("instrument.preRollTicks", out string offset)
                    ? Long(offset, "Pre-Roll Ticks")
                    : currentInstrument.PreRollTicks;
                long? loopStart = TryValue("instrument.loopStart", out string loopStartText)
                    ? NullableLong(loopStartText, "Loop Start")
                    : currentInstrument.LoopStartTick;
                long? loopEnd = TryValue("instrument.loopEnd", out string loopEndText)
                    ? NullableLong(loopEndText, "Loop End")
                    : currentInstrument.LoopEndTick;
                bool isolation = TryValue("instrument.isolation", out string isolationText)
                    ? Bool(isolationText, "Channel Isolation")
                    : currentInstrument.RequiresChannelIsolation;
                return ProjectDomainEditCommands.UpdateEventInstrumentTimingLoopAndIsolation(
                    timingInstrumentId,
                    templateLength,
                    preRoll,
                    loopStart,
                    loopEnd,
                    isolation);
            };
            IEnumerable<Func<MidoraProject, IProjectEditCommand>> remaining = edits
                .Where(edit => edit.Key is not "instrument.templateLength"
                    and not "instrument.preRollTicks"
                    and not "instrument.loopStart"
                    and not "instrument.loopEnd"
                    and not "instrument.isolation")
                .Select(edit => new Func<MidoraProject, IProjectEditCommand>(current =>
                    CreateEditCommand(current, workspace, edit.Key, edit.Value)));
            return new SequentialProjectEditCommand(
                "Update Properties",
                new[] { timingFactory }.Concat(remaining));
        }

        return new SequentialProjectEditCommand(
            "Update Properties",
            edits.Select(edit => new Func<MidoraProject, IProjectEditCommand>(current =>
                CreateEditCommand(current, workspace, edit.Key, edit.Value))));
    }

    public static IProjectEditCommand CreateDeferredMultiSelectionEdit(
        ObjectPropertiesSelectionContext selection, IReadOnlyDictionary<string, string> edits) =>
        new DeferredPropertiesEditCommand(selection, edits);

    private sealed class DeferredPropertiesEditCommand(
        ObjectPropertiesSelectionContext selection, IReadOnlyDictionary<string, string> edits)
        : IProgressReportingProjectEditCommand
    {
        public string Name => "Update Properties";
        public IPreparedProjectEdit Prepare(MidoraProject project) => Prepare(project, default, null);
        public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) =>
            Prepare(project, token, null);
        public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token,
            IProgress<TimelineEditPreparationProgress>? progress)
        {
            PropertiesReadProgress? previous = ReadProgress.Value;
            ReadProgress.Value = new(token, progress);
            try
            {
                token.ThrowIfCancellationRequested();
                IProjectEditCommand command = CreateMultiSelectionEdit(project, selection, edits);
                token.ThrowIfCancellationRequested();
                return command switch
                {
                    IProgressReportingProjectEditCommand reporting => reporting.Prepare(project, token, progress),
                    ICancellableProjectEditCommand cancellable => cancellable.Prepare(project, token),
                    _ => command.Prepare(project)
                };
            }
            finally { ReadProgress.Value = previous; }
        }
    }

    private static IProjectEditCommand CreateMultiSelectionEdit(MidoraProject project,
        ObjectPropertiesSelectionContext selection, IReadOnlyDictionary<string, string> edits)
    {
        bool HasOnly(string prefix) => edits.Keys.All(key => key.StartsWith(prefix, StringComparison.Ordinal));
        bool TryValue(string key, out string value) => edits.TryGetValue(key, out value!);

        IReadOnlyCollection<MidoraId> ids = selection.Ids;
        if (selection.Mode == TimelineWorkspaceMode.Arrangement
            && HasOnly("batch.segment."))
        {
            return ProjectDomainEditCommands.SetArrangementSegmentValues(
                ids,
                TryValue("batch.segment.start", out string start)
                    ? Long(start, "Project Start Tick")
                    : null,
                TryValue("batch.segment.length", out string length)
                    ? Long(length, "Length")
                    : null,
                TryValue("batch.segment.offset", out string offset)
                    ? Long(offset, "Content Offset")
                    : null);
        }
        if (selection.Mode == TimelineWorkspaceMode.Segment
            && TimelineWorkspaceViewModel.FindSegment(project, selection.ObjectId) is { } logical
            && HasOnly("batch.note."))
        {
            return ProjectDomainEditCommands.SetLogicalNoteValues(
                logical.Segment.Id,
                ids,
                TryValue("batch.note.start", out string start)
                    ? Long(start, "Start Tick")
                    : null,
                TryValue("batch.note.length", out string length)
                    ? Long(length, "Length")
                    : null,
                TryValue("batch.note.number", out string number)
                    ? Int(number, "MIDI Note")
                    : null,
                TryValue("batch.note.velocity", out string velocity)
                    ? Int(velocity, "Velocity")
                    : null);
        }
        if (selection.Mode == TimelineWorkspaceMode.Segment
            && TimelineWorkspaceViewModel.FindMidiSegment(project, selection.ObjectId)
                is { } midi)
        {
            if (HasOnly("batch.midiNote."))
            {
                return ProjectDomainEditCommands.SetDirectMidiNoteValues(
                    midi.Segment.Id,
                    ids,
                    TryValue("batch.midiNote.start", out string start)
                        ? Long(start, "Start Tick")
                        : null,
                    TryValue("batch.midiNote.length", out string length)
                        ? Long(length, "Length")
                        : null,
                    TryValue("batch.midiNote.key", out string key)
                        ? IntRange(key, "Key Number", 0, 127)
                        : null,
                    TryValue("batch.midiNote.onVelocity", out string onVelocity)
                        ? IntRange(onVelocity, "Note On Velocity", 1, 127)
                        : null,
                    TryValue("batch.midiNote.offVelocity", out string offVelocity)
                        ? IntRange(offVelocity, "Note Off Velocity", 0, 127)
                        : null);
            }
            if (HasOnly("batch.midiEvent."))
            {
                return CreateDirectMidiEventPropertyEdit(
                    midi.Segment.Id,
                    ids,
                    edits,
                    "batch.midiEvent.");
            }
        }
        if (selection.Mode == TimelineWorkspaceMode.Segment
            && TimelineWorkspaceViewModel.FindSegment(project, selection.ObjectId)
                is { } pointLocation
            && HasOnly("batch.parameterPoint.")
            && TryFindLogicalParameterPointBatch(project,
                pointLocation.Segment,
                selection.Ids) is { } pointBatch)
        {
            return ProjectDomainEditCommands.SetLogicalParameterPoints(
                pointLocation.Segment.Id,
                pointBatch.Lane.Id,
                ids,
                TryValue("batch.parameterPoint.tick", out string tick)
                    ? Long(tick, "Tick")
                    : null,
                TryValue("batch.parameterPoint.value", out string pointValue)
                    ? Double(pointValue, "Value")
                    : null,
                CurveInterpolation.Step);
        }
        if (selection.IsInstrument
            && selection.ObjectId is MidoraId instrumentId
            && project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId)
                is EventInstrument instrument
            && HasOnly("batch.valueCurvePoint.")
            && TryFindValueCurvePointBatch(project, instrument, selection.Ids)
                is { } curveBatch)
        {
            return ProjectDomainEditCommands.SetValueCurvePoints(
                instrument.Id,
                curveBatch.Voice.Id,
                curveBatch.Curve.Id,
                ids,
                TryValue("batch.valueCurvePoint.tick", out string tick)
                    ? Long(tick, "Tick")
                    : null,
                TryValue("batch.valueCurvePoint.value", out string pointValue)
                    ? Double(pointValue, "Value")
                    : null,
                TryValue("batch.valueCurvePoint.interpolation", out string interpolation)
                    ? EnumValue<CurveInterpolation>(interpolation, "Interpolation")
                    : null);
        }
        throw new InvalidOperationException("The current selection has no common editable field.");
    }

    public static void Rebuild(
        ObjectPropertiesViewModel properties,
        MidoraProject? project,
        WorkspaceViewModel? workspace,
        InstrumentCatalogResolver instrumentCatalogResolver)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(instrumentCatalogResolver);
        if (project is null || workspace is null)
        {
            properties.Replace("No selection", "Select an object in the active Workspace.", []);
            return;
        }

        if (workspace.Selection.Ids.Count > 1)
        {
            RebuildMultiSelection(properties, project, ObjectPropertiesSelectionContext.Capture(workspace));
            return;
        }

        MidoraId? selectedId = workspace.Selection.Primary;
        if (workspace is TimelineWorkspaceViewModel timeline)
        {
            RebuildTimeline(properties, project, timeline, selectedId);
            return;
        }
        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace)
        {
            RebuildInstrument(
                properties,
                project,
                instrumentWorkspace,
                selectedId,
                instrumentCatalogResolver);
            return;
        }

        properties.Replace(
            workspace.Header,
            workspace.Kind.ToString(),
            [Field("workspace.kind", "WORKSPACE TYPE", workspace.Kind, false)]);
    }

    public static IProjectEditCommand CreateEditCommand(
        MidoraProject project,
        WorkspaceViewModel workspace,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        value ??= string.Empty;

        if (workspace.Selection.Ids.Count > 1)
        {
            return CreateMultiSelectionEditCommand(project, workspace, key, value);
        }

        if (key.StartsWith("segment.", StringComparison.Ordinal))
        {
            (LogicalTrack Track, Segment Segment) location = FindSegmentContext(project, workspace);
            Segment segment = location.Segment;
            long start = key == "segment.start" ? Long(value, "Project Start Tick") : segment.ProjectStartTick;
            long length = key == "segment.length" ? Long(value, "Length") : segment.LengthTicks;
            long offset = key == "segment.offset" ? Long(value, "Content Offset") : segment.ContentOffsetTick;
            return ProjectDomainEditCommands.SetSegmentWindow(segment.Id, start, length, offset);
        }

        if (key.StartsWith("midiSegment.", StringComparison.Ordinal))
        {
            (PureMidiTrack Track, MidiSegment Segment) location =
                FindMidiSegmentContext(project, workspace);
            MidiSegment segment = location.Segment;
            long start = key == "midiSegment.start"
                ? Long(value, "Project Start Tick")
                : segment.ProjectStartTick;
            long length = key == "midiSegment.length"
                ? Long(value, "Length")
                : segment.LengthTicks;
            long offset = key == "midiSegment.offset"
                ? Long(value, "Content Offset")
                : segment.ContentOffsetTick;
            return ProjectDomainEditCommands.SetMidiSegmentWindow(
                segment.Id,
                start,
                length,
                offset);
        }

        if (key.StartsWith("note.", StringComparison.Ordinal))
        {
            (Segment segment, LogicalNote note) = FindLogicalNoteContext(project, workspace);
            return ProjectDomainEditCommands.UpdateLogicalNote(
                segment.Id,
                note.Id,
                key == "note.start" ? Long(value, "Start Tick") : note.StartTick,
                key == "note.length" ? Long(value, "Length") : note.LengthTicks,
                key == "note.number" ? Int(value, "MIDI Note") : note.Note,
                key == "note.velocity" ? Int(value, "Velocity") : note.Velocity);
        }

        if (key.StartsWith("midiNote.", StringComparison.Ordinal))
        {
            (MidiSegment segment, DirectMidiNote note) =
                FindDirectMidiNoteContext(project, workspace);
            return ProjectDomainEditCommands.SetDirectMidiNoteValues(
                segment.Id,
                [note.Id],
                startTick: key == "midiNote.start" ? Long(value, "Start Tick") : null,
                lengthTicks: key == "midiNote.length" ? Long(value, "Length") : null,
                key: key == "midiNote.key" ? Int(value, "Key Number") : null,
                noteOnVelocity: key == "midiNote.onVelocity" ? Int(value, "Note On Velocity") : null,
                noteOffVelocity: key == "midiNote.offVelocity" ? Int(value, "Note Off Velocity") : null);
        }

        if (key.StartsWith("midiEvent.", StringComparison.Ordinal))
        {
            (MidiSegment segment, DirectMidiChannelEvent directEvent) =
                FindDirectMidiEventContext(project, workspace);
            int? data1 = key switch
            {
                "midiEvent.key" or "midiEvent.controller" =>
                    IntRange(value, key == "midiEvent.key" ? "Key Number" : "Controller", 0, 127),
                "midiEvent.program" => IntRange(value, "Program", 0, 127),
                "midiEvent.channelPressure" => IntRange(value, "Channel Pressure", 0, 127),
                "midiEvent.pitchBend" => IntRange(value, "Pitch Bend", 0, 16_383) & 0x7f,
                _ => null
            };
            int? data2 = key switch
            {
                "midiEvent.velocity" or "midiEvent.pressure" or "midiEvent.value" =>
                    IntRange(value, "Value", 0, 127),
                "midiEvent.pitchBend" => IntRange(value, "Pitch Bend", 0, 16_383) >> 7,
                _ => null
            };
            return ProjectDomainEditCommands.SetDirectMidiEventValues(
                segment.Id,
                [directEvent.Id],
                tick: key == "midiEvent.tick" ? Long(value, "Tick") : null,
                data1: data1,
                data2: data2);
        }

        if (key.StartsWith("parameterPoint.", StringComparison.Ordinal))
        {
            (Segment segment, LogicalParameterLane lane, CurvePoint point) =
                FindLogicalParameterPointContext(project, workspace);
            return ProjectDomainEditCommands.UpdateLogicalParameterPoint(
                segment.Id,
                lane.Id,
                point.Id,
                key == "parameterPoint.tick" ? Long(value, "Tick") : point.Tick,
                key == "parameterPoint.value" ? Double(value, "Value") : point.Value,
                CurveInterpolation.Step);
        }

        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.ObjectId is MidoraId instrumentId)
        {
            EventInstrument instrument = project.EventInstruments.Single(item => item.Id == instrumentId);
            return key switch
            {
                "instrument.name" => ProjectDomainEditCommands.RenameEventInstrument(instrumentId, value),
                "instrument.description" => ProjectDomainEditCommands.UpdateEventInstrumentDescription(instrumentId, value),
                "instrument.root" => ProjectDomainEditCommands.UpdateEventInstrumentRootNote(instrumentId, Int(value, "Root Note")),
                "instrument.templateLength" => ProjectDomainEditCommands.UpdateEventInstrumentTemplateLength(instrumentId, Long(value, "Template Length")),
                "instrument.preRollTicks" => ProjectDomainEditCommands.UpdateEventInstrumentPreRoll(instrumentId, Long(value, "Pre-Roll Ticks")),
                "instrument.isolation" => ProjectDomainEditCommands.UpdateEventInstrumentIsolation(instrumentId, Bool(value, "Channel Isolation")),
                "instrument.overlapPolicy" or "instrument.overlapScope" =>
                    ProjectDomainEditCommands.UpdateEventInstrumentOverlap(
                        instrumentId,
                        key == "instrument.overlapPolicy" ? EnumValue<OverlapPolicy>(value, "Overlap Policy") : instrument.OverlapPolicy,
                        key == "instrument.overlapScope" ? EnumValue<OverlapScope>(value, "Overlap Scope") : instrument.OverlapScope),
                "instrument.shortLifecycle" or "instrument.longLifecycle" =>
                    ProjectDomainEditCommands.UpdateEventInstrumentLifecycle(
                        instrumentId,
                        key == "instrument.shortLifecycle" ? EnumValue<ShortNoteLifecycle>(value, "Short Note Lifecycle") : instrument.ShortLifecycle,
                        key == "instrument.longLifecycle" ? EnumValue<LongNoteLifecycle>(value, "Long Note Lifecycle") : instrument.LongLifecycle),
                "instrument.loopStart" or "instrument.loopEnd" =>
                    CreateLoopEdit(instrument, key, value),
                _ when key.StartsWith("instrument.initial.", StringComparison.Ordinal) =>
                    ProjectDomainEditCommands.UpdateEventInstrumentInitialStateValue(
                        instrument.Id,
                        ParseInitialStateTarget(key["instrument.initial.".Length..]),
                        NullableInt(value, "Initial State Value")),
                _ when key.StartsWith("subvoice.", StringComparison.Ordinal) =>
                    CreateSubVoiceEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("parameter.", StringComparison.Ordinal) =>
                    CreateLogicalParameterEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("valueCurvePoint.", StringComparison.Ordinal) =>
                    CreateValueCurvePointEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("parameterMapping.", StringComparison.Ordinal) =>
                    CreateParameterMappingEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("mappingChain.", StringComparison.Ordinal) =>
                    CreateMappingChainEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("mappingStep.", StringComparison.Ordinal) =>
                    CreateMappingStepEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("envelope.", StringComparison.Ordinal) =>
                    CreateEnvelopeEdit(instrument, instrumentWorkspace.Selection.Primary, key, value),
                _ when key.StartsWith("template.", StringComparison.Ordinal) =>
                    CreateTemplateEventEdit(project, instrument, instrumentWorkspace, key, value),
                _ => throw new InvalidOperationException("This object property is read-only.")
            };
        }

        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Conductor })
        {
            return CreateConductorEdit(project, workspace.Selection.Primary, key, value);
        }

        throw new InvalidOperationException("This object property is read-only.");
    }


    private static IProjectEditCommand CreateMultiSelectionEditCommand(
        MidoraProject project,
        WorkspaceViewModel workspace,
        string key,
        string value)
    {
        IReadOnlyCollection<MidoraId> ids = workspace.Selection.SharedIds;
        IReadOnlySet<MidoraId> selectedIds = workspace.Selection.IdSet;
        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Arrangement })
        {
            int selectedCount = project.Tracks
                    .SelectMany(track => track.Segments)
                    .Count(segment => selectedIds.Contains(segment.Id))
                + project.PureMidiTracks
                    .SelectMany(track => track.Segments)
                    .Count(segment => selectedIds.Contains(segment.Id));
            if (selectedCount == ids.Count)
            {
                return key switch
                {
                    "batch.segment.start" => ProjectDomainEditCommands.SetArrangementSegmentValues(
                        ids,
                        projectStartTick: Long(value, "Project Start Tick")),
                    "batch.segment.length" => ProjectDomainEditCommands.SetArrangementSegmentValues(
                        ids,
                        lengthTicks: Long(value, "Length")),
                    "batch.segment.offset" => ProjectDomainEditCommands.SetArrangementSegmentValues(
                        ids,
                        contentOffsetTick: Long(value, "Content Offset")),
                    _ => throw new InvalidOperationException("This Segment batch field is read-only.")
                };
            }
        }

        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline
            && TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId) is { } location)
        {
            if (SelectPropertyValues(project, location.Segment.Notes.CreateQuerySnapshot(), selectedIds) is not null)
            {
                return key switch
                {
                    "batch.note.start" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, startTick: Long(value, "Start Tick")),
                    "batch.note.length" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, lengthTicks: Long(value, "Length")),
                    "batch.note.number" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, note: Int(value, "MIDI Note")),
                    "batch.note.velocity" => ProjectDomainEditCommands.SetLogicalNoteValues(
                        location.Segment.Id, ids, velocity: Int(value, "Velocity")),
                    _ => throw new InvalidOperationException("This batch field is read-only.")
                };
            }

            if (TryFindLogicalParameterPointBatch(project, location.Segment, selectedIds)
                is { } pointBatch)
            {
                return key switch
                {
                    "batch.parameterPoint.tick" => ProjectDomainEditCommands.SetLogicalParameterPoints(
                        location.Segment.Id, pointBatch.Lane.Id, ids, tick: Long(value, "Tick")),
                    "batch.parameterPoint.value" => ProjectDomainEditCommands.SetLogicalParameterPoints(
                        location.Segment.Id, pointBatch.Lane.Id, ids, value: Double(value, "Value")),
                    _ => throw new InvalidOperationException("This batch field is read-only.")
                };
            }
        }

        if (workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } midiTimeline
            && TimelineWorkspaceViewModel.FindMidiSegment(project, midiTimeline.ObjectId) is { } midiLocation)
        {
            if (SelectPropertyValues(project, midiLocation.Segment.Notes.CreateObjectSource(), selectedIds) is not null)
            {
                return key switch
                {
                    "batch.midiNote.start" => ProjectDomainEditCommands.SetDirectMidiNoteValues(
                        midiLocation.Segment.Id, ids, startTick: Long(value, "Start Tick")),
                    "batch.midiNote.length" => ProjectDomainEditCommands.SetDirectMidiNoteValues(
                        midiLocation.Segment.Id, ids, lengthTicks: Long(value, "Length")),
                    "batch.midiNote.key" => ProjectDomainEditCommands.SetDirectMidiNoteValues(
                        midiLocation.Segment.Id, ids, key: Int(value, "Key Number")),
                    "batch.midiNote.onVelocity" => ProjectDomainEditCommands.SetDirectMidiNoteValues(
                        midiLocation.Segment.Id, ids, noteOnVelocity: Int(value, "Note On Velocity")),
                    "batch.midiNote.offVelocity" => ProjectDomainEditCommands.SetDirectMidiNoteValues(
                        midiLocation.Segment.Id, ids, noteOffVelocity: Int(value, "Note Off Velocity")),
                    _ => throw new InvalidOperationException("This Direct MIDI Note batch field is read-only.")
                };
            }
            if (SelectPropertyValues(project, midiLocation.Segment.ChannelEvents.CreateObjectSource(), selectedIds) is not null)
            {
                return key switch
                {
                    "batch.midiEvent.tick" => ProjectDomainEditCommands.SetDirectMidiEventValues(
                        midiLocation.Segment.Id, ids, tick: Long(value, "Tick")),
                    "batch.midiEvent.key" or "batch.midiEvent.controller" =>
                        ProjectDomainEditCommands.SetDirectMidiEventValues(
                            midiLocation.Segment.Id,
                            ids,
                            data1: IntRange(value, "Number", 0, 127)),
                    "batch.midiEvent.program" => ProjectDomainEditCommands.SetDirectMidiEventValues(
                        midiLocation.Segment.Id,
                        ids,
                        data1: IntRange(value, "Program", 0, 127)),
                    "batch.midiEvent.channelPressure" =>
                        ProjectDomainEditCommands.SetDirectMidiEventValues(
                            midiLocation.Segment.Id,
                            ids,
                            data1: IntRange(value, "Channel Pressure", 0, 127)),
                    "batch.midiEvent.velocity" or "batch.midiEvent.pressure" or
                        "batch.midiEvent.value" =>
                        ProjectDomainEditCommands.SetDirectMidiEventValues(
                            midiLocation.Segment.Id,
                            ids,
                            data2: IntRange(value, "Value", 0, 127)),
                    "batch.midiEvent.pitchBend" => CreateDirectMidiPitchBendPropertyEdit(
                        midiLocation.Segment.Id,
                        ids,
                        value),
                    _ => throw new InvalidOperationException("This Direct MIDI Event batch field is read-only.")
                };
            }
        }

        if (workspace is InstrumentWorkspaceViewModel instrumentWorkspace
            && instrumentWorkspace.ObjectId is MidoraId instrumentId
            && project.EventInstruments.FirstOrDefault(item => item.Id == instrumentId) is EventInstrument instrument
            && TryFindValueCurvePointBatch(project, instrument, selectedIds) is { } curveBatch)
        {
            return key switch
            {
                "batch.valueCurvePoint.tick" => ProjectDomainEditCommands.SetValueCurvePoints(
                    instrument.Id, curveBatch.Voice.Id, curveBatch.Curve.Id, ids,
                    tick: Long(value, "Tick")),
                "batch.valueCurvePoint.value" => ProjectDomainEditCommands.SetValueCurvePoints(
                    instrument.Id, curveBatch.Voice.Id, curveBatch.Curve.Id, ids,
                    value: Double(value, "Value")),
                "batch.valueCurvePoint.interpolation" => ProjectDomainEditCommands.SetValueCurvePoints(
                    instrument.Id, curveBatch.Voice.Id, curveBatch.Curve.Id, ids,
                    interpolation: EnumValue<CurveInterpolation>(value, "Interpolation")),
                _ => throw new InvalidOperationException("This batch field is read-only.")
            };
        }

        throw new InvalidOperationException(
            "The current selection has no common field that can be edited atomically.");
    }

    private static LogicalParameterPointBatch? TryFindLogicalParameterPointBatch(
        MidoraProject project,
        Segment segment,
        IReadOnlyCollection<MidoraId> ids)
    {
        foreach (LogicalParameterLane lane in segment.ParameterLanes)
        {
            if (SelectPropertyValues(project, lane.Points.CreateQuerySnapshot(), ids) is { } points)
                return new(lane, points);
        }
        return null;
    }

    private static ValueCurvePointBatch? TryFindValueCurvePointBatch(
        MidoraProject project,
        EventInstrument instrument,
        IReadOnlyCollection<MidoraId> ids)
    {
        foreach (SubVoice voice in instrument.SubVoices)
        {
            foreach (ValueCurve curve in voice.Curves)
            {
                if (SelectPropertyValues(project, curve.Points.CreateQuerySnapshot(), ids) is { } points)
                    return new(voice, curve, points);
            }
        }
        return null;
    }

    private static IReadOnlyCollection<T>? SelectPropertyValues<T>(MidoraProject project, ITimelineObjectSource<T> source,
        IReadOnlyCollection<MidoraId> ids) where T : unmanaged
    {
        if (!SummarizePropertyOwner(project, source, ids, [])) return null;
        return new PropertySelectionValues<T>(project, source, ids);
    }

    private sealed class PropertySelectionValues<T>(MidoraProject project, ITimelineObjectSource<T> source,
        IReadOnlyCollection<MidoraId> ids) : IReadOnlyCollection<T> where T : unmanaged
    {
        public int Count => ids.Count;
        public IEnumerator<T> GetEnumerator()
        {
            return ProjectTimelineReadPreparation.ReadSelectedValues(project, source, ids,
                ReadProgress.Value?.Token ?? default, ReadProgress.Value?.Progress,
                preserveFormalOrder: false).GetEnumerator();
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }


    private static IReadOnlyList<PropertyField> DirectMidiEventFields(
        DirectMidiChannelEvent item)
    {
        List<PropertyField> fields =
        [
            Field("midiEvent.tick", "TICK", item.Tick),
            Field("midiEvent.kind", "EVENT TYPE", item.Kind, editable: false)
        ];
        switch (item.Kind)
        {
            case DirectMidiChannelEventKind.NoteOff:
            case DirectMidiChannelEventKind.NoteOn:
                fields.Add(Field("midiEvent.key", "KEY NUMBER", item.Data1));
                fields.Add(Field("midiEvent.velocity", "VELOCITY", item.Data2));
                break;
            case DirectMidiChannelEventKind.PolyphonicKeyPressure:
                fields.Add(Field("midiEvent.key", "KEY NUMBER", item.Data1));
                fields.Add(Field("midiEvent.pressure", "PRESSURE", item.Data2));
                break;
            case DirectMidiChannelEventKind.ControlChange:
                fields.Add(Field("midiEvent.controller", "CONTROLLER", item.Data1));
                fields.Add(Field("midiEvent.value", "VALUE", item.Data2));
                break;
            case DirectMidiChannelEventKind.ProgramChange:
                fields.Add(Field("midiEvent.program", "PROGRAM (0–127)", item.Data1));
                break;
            case DirectMidiChannelEventKind.ChannelPressure:
                fields.Add(Field("midiEvent.channelPressure", "PRESSURE", item.Data1));
                break;
            case DirectMidiChannelEventKind.PitchBend:
                fields.Add(Field(
                    "midiEvent.pitchBend",
                    "PITCH BEND (0–16383)",
                    item.Data1 | item.Data2 << 7));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(item));
        }
        return fields;
    }


    private static IProjectEditCommand CreateDirectMidiPitchBendPropertyEdit(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        string value)
    {
        int parsed = IntRange(value, "Pitch Bend", 0, 16_383);
        return ProjectDomainEditCommands.SetDirectMidiEventValues(
            segmentId,
            eventIds,
            data1: parsed & 0x7f,
            data2: parsed >> 7);
    }

    private static IProjectEditCommand CreateDirectMidiEventPropertyEdit(
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds,
        IReadOnlyDictionary<string, string> edits,
        string prefix)
    {
        bool TryValue(string suffix, out string value) =>
            edits.TryGetValue(prefix + suffix, out value!);

        long? tick = TryValue("tick", out string tickText)
            ? Long(tickText, "Tick")
            : null;
        int? data1 = null;
        int? data2 = null;
        if (TryValue("key", out string keyText))
        {
            data1 = IntRange(keyText, "Key Number", 0, 127);
        }
        else if (TryValue("controller", out string controllerText))
        {
            data1 = IntRange(controllerText, "Controller", 0, 127);
        }
        else if (TryValue("program", out string programText))
        {
            data1 = IntRange(programText, "Program", 0, 127);
        }
        else if (TryValue("channelPressure", out string channelPressureText))
        {
            data1 = IntRange(channelPressureText, "Channel Pressure", 0, 127);
        }

        if (TryValue("velocity", out string velocityText))
        {
            data2 = IntRange(velocityText, "Velocity", 0, 127);
        }
        else if (TryValue("pressure", out string pressureText))
        {
            data2 = IntRange(pressureText, "Pressure", 0, 127);
        }
        else if (TryValue("value", out string valueText))
        {
            data2 = IntRange(valueText, "Value", 0, 127);
        }

        if (TryValue("pitchBend", out string pitchBendText))
        {
            int pitchBend = IntRange(pitchBendText, "Pitch Bend", 0, 16_383);
            data1 = pitchBend & 0x7f;
            data2 = pitchBend >> 7;
        }

        return ProjectDomainEditCommands.SetDirectMidiEventValues(
            segmentId,
            eventIds,
            tick: tick,
            data1: data1,
            data2: data2);
    }

    private sealed record LogicalParameterPointBatch(
        LogicalParameterLane Lane,
        IReadOnlyCollection<CurvePointSnapshotValue> Points);

    private sealed record ValueCurvePointBatch(
        SubVoice Voice,
        ValueCurve Curve,
        IReadOnlyCollection<CurvePointSnapshotValue> Points);

    private readonly record struct ArrangementSegmentProperty(
        MidoraId Id,
        long StartTick,
        long LengthTicks,
        long ContentOffsetTick);

    private static void RebuildTimeline(
        ObjectPropertiesViewModel properties,
        MidoraProject project,
        TimelineWorkspaceViewModel workspace,
        MidoraId? selectedId)
    {
        if (workspace.Mode == TimelineWorkspaceMode.Arrangement && selectedId is MidoraId segmentId)
        {
            (LogicalTrack Track, Segment Segment)? location = TimelineWorkspaceViewModel.FindSegment(project, segmentId);
            if (location is not null)
            {
                Segment segment = location.Value.Segment;
                properties.Replace(
                    "Segment",
                    TimelineWorkspaceViewModel.TrackDisplayName(project, location.Value.Track),
                    [Field("segment.start", "PROJECT START TICK", segment.ProjectStartTick),
                     Field("segment.length", "LENGTH TICKS", segment.LengthTicks),
                     Field("segment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick)]);
                return;
            }
            if (TimelineWorkspaceViewModel.FindMidiSegment(project, segmentId) is { } midiLocation)
            {
                MidiSegment segment = midiLocation.Segment;
                properties.Replace(
                    "MIDI Segment",
                    string.IsNullOrWhiteSpace(midiLocation.Track.Name)
                        ? "Unnamed MIDI Track"
                        : midiLocation.Track.Name,
                    [Field("midiSegment.start", "PROJECT START TICK", segment.ProjectStartTick),
                     Field("midiSegment.length", "LENGTH TICKS", segment.LengthTicks),
                     Field("midiSegment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick)]);
                return;
            }
        }

        if (workspace.Mode == TimelineWorkspaceMode.Segment)
        {
            (LogicalTrack Track, Segment Segment)? location =
                TimelineWorkspaceViewModel.FindSegment(project, workspace.ObjectId);
            if (location is not null && selectedId is MidoraId noteId)
            {
                if (location.Value.Segment.Notes.TryGetById(noteId, out LogicalNote? note)
                    && note is not null)
                {
                    properties.Replace(
                        "Logical Note",
                        $"{TimelineWorkspaceViewModel.MidiNoteName(note.Note)} in {workspace.Header}",
                        [Field("note.start", "START TICK", note.StartTick),
                         Field("note.length", "LENGTH TICKS", note.LengthTicks),
                         Field("note.number", "MIDI NOTE", note.Note),
                         Field("note.velocity", "VELOCITY", note.Velocity)]);
                    return;
                }
                LogicalParameterLane? lane = null;
                CurvePoint? point = null;
                foreach (LogicalParameterLane candidate in location.Value.Segment.ParameterLanes)
                {
                    if (!candidate.Points.TryGetById(noteId, out CurvePoint? resolved)
                        || resolved is null)
                    {
                        continue;
                    }
                    lane = candidate;
                    point = resolved;
                    break;
                }
                if (lane is not null && point is not null)
                {
                    EventInstrument? instrument = project.FindEventInstrumentDefinition(
                        location.Value.Track);
                    LogicalParameterDefinition? definition = instrument?.LogicalParameters
                        .FirstOrDefault(item => item.Id == lane.ParameterId);
                    properties.Replace(
                        "Logical Parameter Point",
                        definition is null ? "Missing parameter definition" : definition.Name,
                        [Field("parameterPoint.tick", "TICK", point.Tick),
                         Field("parameterPoint.value", "VALUE", point.Value)]);
                    return;
                }
            }
            if (location is not null)
            {
                Segment segment = location.Value.Segment;
                properties.Replace(
                    "Segment Window",
                    TimelineWorkspaceViewModel.TrackDisplayName(project, location.Value.Track),
                    [Field("segment.start", "PROJECT START TICK", segment.ProjectStartTick),
                     Field("segment.length", "LENGTH TICKS", segment.LengthTicks),
                     Field("segment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick)]);
                return;
            }

            if (TimelineWorkspaceViewModel.FindMidiSegment(project, workspace.ObjectId) is { } midiLocation)
            {
                MidiSegment segment = midiLocation.Segment;
                if (selectedId is MidoraId midiObjectId)
                {
                    if (segment.Notes.TryGetById(midiObjectId, out DirectMidiNote? note)
                        && note is not null)
                    {
                        properties.Replace(
                            "Direct MIDI Note",
                            $"{TimelineWorkspaceViewModel.MidiNoteName(note.Key)} in {workspace.Header}",
                            [Field("midiNote.start", "START TICK", note.StartTick),
                             Field("midiNote.length", "LENGTH TICKS", note.LengthTicks),
                             Field("midiNote.key", "KEY NUMBER", note.Key),
                             Field("midiNote.onVelocity", "NOTE ON VELOCITY", note.NoteOnVelocity),
                             Field("midiNote.offVelocity", "NOTE OFF VELOCITY", note.NoteOffVelocity)]);
                        return;
                    }
                    if (segment.ChannelEvents.TryGetById(
                            midiObjectId,
                            out DirectMidiChannelEvent? channelEvent)
                        && channelEvent is not null)
                    {
                        properties.Replace(
                            "Direct MIDI Event",
                            TimelineWorkspaceViewModel.DirectMidiLaneLabel(
                                TimelineWorkspaceViewModel.ToDirectMidiLaneTarget(channelEvent)),
                            DirectMidiEventFields(channelEvent));
                        return;
                    }
                    if (ProjectTimelineReadPreparation.ReadOpaqueProperties(project,
                            segment.OpaqueEvents.CreateObjectSource(), midiObjectId) is { } opaque)
                    {
                        properties.Replace(
                            "Imported MIDI Event",
                            TimelineWorkspaceViewModel.OpaqueMidiEventLabel(opaque.Kind, opaque.MetaType, opaque.PayloadLength),
                            [Field("opaqueMidi.tick", "TICK", opaque.Tick, false),
                             Field("opaqueMidi.kind", "EVENT KIND", opaque.Kind, false),
                             Field("opaqueMidi.metaType", "META TYPE", $"0x{opaque.MetaType:X2}", false),
                             Field("opaqueMidi.payloadLength", "PAYLOAD BYTES", opaque.PayloadLength, false),
                             Field("opaqueMidi.payload", "PAYLOAD HEX PREVIEW", opaque.PayloadHexPreview, false)]);
                        return;
                    }
                }
                properties.Replace(
                    "MIDI Segment Window",
                    string.IsNullOrWhiteSpace(midiLocation.Track.Name)
                        ? "Unnamed MIDI Track"
                        : midiLocation.Track.Name,
                    [Field("midiSegment.start", "PROJECT START TICK", segment.ProjectStartTick),
                     Field("midiSegment.length", "LENGTH TICKS", segment.LengthTicks),
                     Field("midiSegment.offset", "CONTENT OFFSET TICK", segment.ContentOffsetTick)]);
                return;
            }
        }

        if (workspace.Mode == TimelineWorkspaceMode.Conductor && selectedId is MidoraId conductorId &&
            TryRebuildConductorSelection(properties, project.Conductor, conductorId, CancellationToken.None))
            return;

        properties.Replace(workspace.Header, workspace.Context, [
            Field("viewport.start", "VIEW START TICK", workspace.StartTick, false),
            Field("viewport.span", "VISIBLE TICK SPAN", workspace.TickSpan, false)]);
    }

    private static bool TryRebuildConductorSelection(
        ObjectPropertiesViewModel properties, ConductorTrack conductor,
        MidoraId conductorId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (conductor.Tempos.CaptureQuerySnapshot().TryGetById(conductorId, out TempoChange tempo))
        {
            properties.Replace("Tempo", "Conductor event", [
                Field("conductor.tick", "TICK", tempo.Tick),
                Field("conductor.bpm", "BEATS PER MINUTE", tempo.BeatsPerMinute)]);
            return true;
        }
        token.ThrowIfCancellationRequested();
        if (conductor.TimeSignatures.CaptureQuerySnapshot().TryGetById(conductorId, out TimeSignatureChange signature))
        {
            properties.Replace("Time Signature", "Conductor event", [
                Field("conductor.tick", "TICK", signature.Tick),
                Field("conductor.numerator", "NUMERATOR", signature.Numerator),
                Field("conductor.denominator", "DENOMINATOR", signature.Denominator)]);
            return true;
        }
        token.ThrowIfCancellationRequested();
        if (conductor.KeySignatures.CaptureQuerySnapshot().TryGetById(conductorId, out KeySignatureChange key))
        {
            properties.Replace("Key Signature", "Conductor event", [
                Field("conductor.tick", "TICK", key.Tick),
                Field("conductor.sharpsFlats", "SHARPS / FLATS", key.SharpsFlats),
                Field("conductor.isMinor", "IS MINOR", key.IsMinor)]);
            return true;
        }
        token.ThrowIfCancellationRequested();
        if (conductor.Markers.CaptureQuerySnapshot().TryGetById(conductorId, out ProjectMarker marker))
        {
            properties.Replace("Project Marker", "Conductor event", [
                Field("conductor.tick", "TICK", marker.Tick),
                Field("conductor.name", "NAME", marker.Name)]);
            return true;
        }
        token.ThrowIfCancellationRequested();
        if (conductor.EndMarker is ProjectEndMarker end && end.Id == conductorId)
        {
            properties.Replace("Project End Marker", "Hard Project boundary", [
                Field("conductor.tick", "TICK", end.Tick)]);
            return true;
        }
        return false;
    }

    private static void RebuildInstrument(
        ObjectPropertiesViewModel properties,
        MidoraProject project,
        InstrumentWorkspaceViewModel workspace,
        MidoraId? selectedId,
        InstrumentCatalogResolver instrumentCatalogResolver)
    {
        EventInstrument? instrument = project.EventInstruments.FirstOrDefault(item => item.Id == workspace.ObjectId);
        if (instrument is null)
        {
            properties.Replace("Missing Event Instrument", "The referenced object no longer exists.", []);
            return;
        }
        if (selectedId is MidoraId eventId)
        {
            if (instrument.SubVoices.FirstOrDefault(item => item.Id == eventId) is SubVoice selectedVoice)
            {
                properties.Replace(
                    string.IsNullOrWhiteSpace(selectedVoice.Name) ? "SubVoice" : selectedVoice.Name,
                    "SubVoice definition",
                    [Field("subvoice.name", "NAME", selectedVoice.Name ?? string.Empty),
                     Field("subvoice.root", "ROOT NOTE OVERRIDE", selectedVoice.RootNoteOverride?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                     Field("subvoice.events", "TEMPLATE EVENT COUNT", selectedVoice.Events.Count, false),
                     Field("subvoice.curves", "VALUE CURVE COUNT", selectedVoice.Curves.Count, false)]);
                return;
            }
            if (instrument.LogicalParameters.FirstOrDefault(item => item.Id == eventId) is LogicalParameterDefinition parameter)
            {
                properties.Replace(
                    parameter.Name,
                    $"Logical Parameter · {parameter.Type}",
                    [Field("parameter.name", "NAME", parameter.Name),
                     Field("parameter.type", "TYPE", parameter.Type, false),
                     Field("parameter.minimum", "LEGAL MINIMUM", parameter.Minimum),
                     Field("parameter.maximum", "LEGAL MAXIMUM", parameter.Maximum),
                     Field("parameter.default", "DEFAULT VALUE", parameter.DefaultValue),
                     Field("parameter.displayMinimum", "DISPLAY MINIMUM", parameter.DisplayMinimum),
                     Field("parameter.displayMaximum", "DISPLAY MAXIMUM", parameter.DisplayMaximum),
                     Field("parameter.enumCount", "ENUM ITEM COUNT", parameter.EnumItems.Count, false)]);
                return;
            }
            if (instrument.MappingFunctions.FirstOrDefault(item => item.Id == eventId) is CSharpMappingFunction function)
            {
                properties.Replace(
                    function.Name,
                    "Mapping Function",
                    [Field("mapping.abi", "ABI VERSION", function.AbiVersion, false),
                     Field("mapping.open", "EDITING", "Double-click the function to open its Draft Workspace.", false)]);
                return;
            }
            if (instrument.ParameterMappings.FirstOrDefault(item => item.Id == eventId) is LogicalParameterMapping mapping)
            {
                string sourceName = instrument.LogicalParameters.FirstOrDefault(item => item.Id == mapping.ParameterId)?.Name
                    ?? "Missing Logical Parameter";
                SubVoice? targetVoice = instrument.SubVoices.FirstOrDefault(item => item.Id == mapping.SubVoiceId);
                properties.Replace(
                    "Logical Parameter Mapping",
                    $"{sourceName} → {(targetVoice?.Name ?? "Missing SubVoice")} · {FormatMidiTarget(mapping.Target)}",
                    [ChoiceField(
                         "parameterMapping.source",
                         "SOURCE PARAMETER",
                         mapping.ParameterId.Value.ToString(CultureInfo.InvariantCulture),
                         IdChoices(
                             instrument.LogicalParameters.Select(item => (item.Id, item.Name)),
                             mapping.ParameterId,
                             "Missing Logical Parameter")),
                     ChoiceField(
                         "parameterMapping.subVoice",
                         "TARGET SUBVOICE",
                         mapping.SubVoiceId.Value.ToString(CultureInfo.InvariantCulture),
                         IdChoices(
                             instrument.SubVoices.Select((item, index) => (
                                 item.Id,
                                 string.IsNullOrWhiteSpace(item.Name)
                                     ? $"SubVoice {index + 1}"
                                     : item.Name)),
                             mapping.SubVoiceId,
                             "Missing SubVoice")),
                     Field("parameterMapping.target", "MIDI TARGET", FormatMidiTarget(mapping.Target), false),
                     Field("parameterMapping.rounding", "FINAL ROUNDING", mapping.TargetSettings.Rounding),
                     Field("parameterMapping.overflow", "FINAL OVERFLOW", mapping.TargetSettings.Overflow),
                     Field("parameterMapping.steps", "MAPPING STEPS", mapping.Steps.Count, false)]);
                return;
            }
            if (workspace.MappingChains.Any(item => item.Id == eventId))
            {
                MappingChainEditingContext chainContext = MappingEditingPolicy.Resolve(
                    instrument,
                    eventId);
                string owner = workspace.MappingChains.Single(item => item.Id == eventId).Owner;
                MidiIntegerTargetSettings settings = chainContext.TargetSettings;
                properties.Replace(
                    "Mapping Chain",
                    owner,
                    [Field("mappingChain.enabled", "ENABLED", chainContext.Chain.IsEnabled),
                     Field(
                         "mappingChain.ownerKind",
                         "OWNER KIND",
                         chainContext.IsLogicalParameterMapping
                             ? "Logical Parameter Mapping"
                             : "SubVoice Event Mapping",
                         false),
                     Field(
                         "mappingChain.target",
                         "TARGET",
                         chainContext.ParameterMapping is LogicalParameterMapping parameterOwner
                             ? FormatMidiTarget(parameterOwner.Target)
                             : FormatEventMappingTarget(chainContext.EventMapping!.Target),
                         false),
                     Field("mappingChain.rounding", "FINAL ROUNDING", settings.Rounding),
                     Field("mappingChain.overflow", "FINAL OVERFLOW", settings.Overflow),
                     Field("mappingChain.steps", "STEP COUNT", chainContext.Chain.Count, false)]);
                return;
            }
            if (FindMappingStep(instrument, eventId) is MappingStepContext mappingStep)
            {
                ValueMappingStep step = mappingStep.Step;
                MappingChainEditingContext chainContext = MappingEditingPolicy.Resolve(
                    instrument,
                    mappingStep.Chain.Id);
                MappingSource[] sources = workspace.MappingChains
                    .Select(chain => MappingEditingPolicy.Resolve(instrument, chain.Id))
                    .SelectMany(context => MappingEditingPolicy.AllowedSources(instrument, context))
                    .Append(step.Source)
                    .Distinct()
                    .ToArray();
                MappingOperation[] operations = Enum.GetValues<MappingOperation>()
                    .Where(value => value != MappingOperation.CustomCSharp
                        || workspace.MappingChains.Any(chain =>
                            MappingEditingPolicy.AllowedFunctions(
                                instrument,
                                MappingEditingPolicy.Resolve(instrument, chain.Id)).Count != 0)
                        || value == step.Operation)
                    .ToArray();
                List<PropertyField> fields =
                [
                    ChoiceField(
                        "mappingStep.chain",
                        "MAPPING CHAIN",
                        mappingStep.Chain.Id.Value.ToString(CultureInfo.InvariantCulture),
                        workspace.MappingChains.Select(chain => new PropertyChoiceOption(
                            chain.Id.Value.ToString(CultureInfo.InvariantCulture),
                            chain.Owner))),
                    Field("mappingStep.enabled", "ENABLED", step.IsEnabled),
                    ChoiceField(
                        "mappingStep.source",
                        "SOURCE (BUILT-IN OPERATIONS ONLY)",
                        step.Source.ToString(),
                        sources.Select(source => new PropertyChoiceOption(
                            source.ToString(),
                            $"{source} — {MappingEditingPolicy.DescribeSource(source)}"))),
                    ChoiceField(
                        "mappingStep.operation",
                        "OPERATION",
                        step.Operation.ToString(),
                        operations.Select(operation => new PropertyChoiceOption(
                            operation.ToString(),
                            DescribeMappingOperation(operation))))
                ];
                // Keep every semantic reference selectable in the transactional dialog.
                // Source and Operation may change in the same OK action, so deriving field
                // visibility from the old values would make the required new reference
                // impossible to choose without reopening Properties.
                fields.Add(ChoiceField(
                    "mappingStep.logicalParameter",
                    "LOGICAL PARAMETER (WHEN SOURCE USES IT)",
                    step.LogicalParameterId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    OptionalIdChoices(
                        instrument.LogicalParameters.Select(item => (item.Id, item.Name)),
                        step.LogicalParameterId,
                        "Missing Logical Parameter")));
                fields.Add(ChoiceField(
                    "mappingStep.envelope",
                    "ENVELOPE PRESET (WHEN SOURCE USES IT)",
                    step.EnvelopeId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    OptionalIdChoices(
                        instrument.Envelopes.Select(item => (
                            item.Id,
                            string.IsNullOrWhiteSpace(item.Name) ? "Envelope Preset" : item.Name)),
                        step.EnvelopeId,
                        "Missing Envelope")));
                fields.Add(ChoiceField(
                    "mappingStep.function",
                    "C# MAPPING FUNCTION (CUSTOM C# OPERATION)",
                    step.MappingFunctionId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    OptionalIdChoices(
                        instrument.MappingFunctions.Select(item => (item.Id, item.Name)),
                        step.MappingFunctionId,
                        "Missing or illegal Mapping Function")));
                fields.AddRange(
                [
                    Field("mappingStep.constant", "CONSTANT", step.Constant),
                    Field("mappingStep.sourceMinimum", "SOURCE MINIMUM", step.SourceMinimum),
                    Field("mappingStep.sourceMaximum", "SOURCE MAXIMUM", step.SourceMaximum),
                    Field("mappingStep.targetMinimum", "TARGET MINIMUM", step.TargetMinimum),
                    Field("mappingStep.targetMaximum", "TARGET MAXIMUM", step.TargetMaximum),
                    Field("mappingStep.inputOverflow", "INPUT OVERFLOW", step.InputOverflow),
                    Field("mappingStep.divideByZero", "DIVIDE BY ZERO", step.DivideByZero)
                ]);
                properties.Replace(
                    $"Mapping Step {mappingStep.Index + 1}",
                    "Ordered Mapping Step · Custom C# receives the current accumulated chain value; "
                        + "Source applies only to built-in operations.",
                    fields);
                return;
            }
            if (instrument.Envelopes.FirstOrDefault(item => item.Id == eventId) is InstrumentEnvelope envelope)
            {
                bool canEditEnvelope = instrument.RequiresChannelIsolation;
                properties.Replace(
                    string.IsNullOrWhiteSpace(envelope.Name) ? "Envelope Preset" : envelope.Name,
                    instrument.RequiresChannelIsolation
                        ? "Fixed ADSR-like lifecycle envelope"
                        : "Incompatible while Per-Note Instance Isolation is disabled",
                    [Field("envelope.name", "NAME", envelope.Name ?? string.Empty, canEditEnvelope),
                     Field("envelope.delay", "DELAY TICKS", envelope.DelayTicks, canEditEnvelope),
                     Field("envelope.attack", "ATTACK TICKS", envelope.AttackTicks, canEditEnvelope),
                     Field("envelope.hold", "HOLD TICKS", envelope.HoldTicks, canEditEnvelope),
                     Field("envelope.decay", "DECAY TICKS", envelope.DecayTicks, canEditEnvelope),
                     Field("envelope.release", "RELEASE TICKS", envelope.ReleaseTicks, canEditEnvelope),
                     Field("envelope.start", "START VALUE", envelope.StartValue, canEditEnvelope),
                     Field("envelope.peak", "PEAK VALUE", envelope.PeakValue, canEditEnvelope),
                     Field("envelope.sustain", "SUSTAIN VALUE", envelope.SustainValue, canEditEnvelope),
                     Field("envelope.end", "END VALUE", envelope.EndValue, canEditEnvelope)]);
                return;
            }
            foreach (SubVoice curveVoice in instrument.SubVoices)
            {
                ValueCurve? curve = null;
                CurvePoint? point = null;
                foreach (ValueCurve candidate in curveVoice.Curves)
                {
                    if (!candidate.Points.TryGetById(eventId, out CurvePoint? resolved)
                        || resolved is null)
                    {
                        continue;
                    }
                    curve = candidate;
                    point = resolved;
                    break;
                }
                if (curve is null || point is null) continue;
                properties.Replace(
                    "Value Curve Point",
                    $"{curveVoice.Name ?? "SubVoice"} · {FormatMidiTarget(curve.Target)}",
                    [Field("valueCurvePoint.tick", "TICK", point.Tick),
                     Field("valueCurvePoint.value", "VALUE", point.Value),
                     Field("valueCurvePoint.interpolation", "INTERPOLATION", point.Interpolation)]);
                return;
            }
            SubVoice? voice = null;
            TemplateEvent? template = null;
            foreach (SubVoice candidate in instrument.SubVoices)
            {
                if (!candidate.Events.TryGetById(eventId, out TemplateEvent? resolved)
                    || resolved is null)
                {
                    continue;
                }
                voice = candidate;
                template = resolved;
                break;
            }
            if (voice is not null && template is not null)
            {
                List<PropertyField> fields = [
                    Field("template.tick", "TICK", template.Tick),
                    Field("template.kind", "EVENT KIND", template.Kind, false)];
                if (template.Kind == TemplateEventKind.Note)
                {
                    fields.Add(Field("template.length", "LENGTH TICKS", template.LengthTicks));
                }
                if (template.Kind is TemplateEventKind.Note or TemplateEventKind.ControlChange
                    or TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter)
                {
                    fields.Add(Field(
                        "template.number",
                        template.Kind == TemplateEventKind.ControlChange
                            ? $"CONTROLLER · {MidiControlChangeCatalog.Format(template.Number)}"
                            : "NUMBER",
                        template.Number));
                }
                if (template.Kind != TemplateEventKind.Bank || template.HasBankMsb)
                {
                    fields.Add(Field(
                        "template.value",
                        template.Kind == TemplateEventKind.Program ? "PROGRAM (0–127)" : "VALUE",
                        template.Value));
                }
                if (template.Kind is TemplateEventKind.Bank or TemplateEventKind.PitchBendRange)
                {
                    fields.Add(Field("template.secondary", "SECONDARY VALUE", template.SecondaryValue));
                }
                if (template.Kind == TemplateEventKind.Note)
                {
                    fields.Add(Field("template.followPitch", "FOLLOW PITCH DELTA", template.FollowPitchDelta));
                }
                properties.Replace(template.Kind.ToString(), string.IsNullOrWhiteSpace(voice.Name) ? "SubVoice event" : voice.Name, fields);
                return;
            }
        }

        PropertyField bankMsb = StateField(
            "instrument.initial.bankMsb",
            "INITIAL BANK MSB",
            instrument.InitialState.BankMsb);
        PropertyField bankLsb = StateField(
            "instrument.initial.bankLsb",
            "INITIAL BANK LSB",
            instrument.InitialState.BankLsb);
        PropertyField program = StateField(
            "instrument.initial.program",
            "INITIAL PROGRAM (0–127)",
            instrument.InitialState.Program);
        PropertyField catalogName = CreateCatalogProgramNameField(
            instrumentCatalogResolver,
            bankMsb,
            bankLsb,
            program);
        properties.Replace(
            instrument.Name,
            "Event Instrument",
            [Field("instrument.name", "NAME", instrument.Name),
             Field("instrument.description", "DESCRIPTION", instrument.Description ?? string.Empty),
             Field("instrument.root", "ROOT MIDI NOTE", instrument.RootNote),
             Field("instrument.templateLength", "TEMPLATE LENGTH TICKS", instrument.TemplateLengthTicks),
             Field(
                 "instrument.preRollTicks",
                 $"PRE-ROLL TICKS (0–{instrument.TemplateLengthTicks.ToString(CultureInfo.InvariantCulture)})",
                 instrument.PreRollTicks),
             Field("instrument.isolation", "REQUIRES CHANNEL ISOLATION", instrument.RequiresChannelIsolation),
             Field("instrument.overlapPolicy", "OVERLAP POLICY", instrument.OverlapPolicy),
             Field("instrument.overlapScope", "OVERLAP SCOPE", instrument.OverlapScope),
             Field("instrument.shortLifecycle", "SHORT NOTE LIFECYCLE", instrument.ShortLifecycle),
             Field("instrument.longLifecycle", "LONG NOTE LIFECYCLE", instrument.LongLifecycle),
             Field("instrument.loopStart", "LOOP START (blank = unset)", instrument.LoopStartTick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
             Field("instrument.loopEnd", "LOOP END (blank = unset)", instrument.LoopEndTick?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
             bankMsb,
             bankLsb,
             program,
             catalogName,
             StateField("instrument.initial.pitchBend", "INITIAL PITCH BEND", instrument.InitialState.PitchBend),
             StateField("instrument.initial.pitchRangeSemitones", "INITIAL PITCH RANGE SEMITONES", instrument.InitialState.PitchBendRangeSemitones),
             StateField("instrument.initial.pitchRangeCents", "INITIAL PITCH RANGE CENTS", instrument.InitialState.PitchBendRangeCents),
             .. ExtraStateFields("instrument.initial", instrument.InitialState)]);
    }

    private static PropertyField CreateCatalogProgramNameField(
        InstrumentCatalogResolver resolver,
        PropertyField bankMsb,
        PropertyField bankLsb,
        PropertyField program)
    {
        PropertyField result = Field(
            "instrument.initial.catalogProgramName",
            "CATALOG NAME (AUXILIARY)",
            string.Empty,
            editable: false);

        void Refresh()
        {
            string[] values = [bankMsb.Value, bankLsb.Value, program.Value];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                result.Value = "No complete Bank/Program state";
                return;
            }
            if (values.Any(string.IsNullOrWhiteSpace))
            {
                result.Value = "Complete Bank MSB, Bank LSB, and Program to resolve a Catalog name";
                return;
            }
            if (!byte.TryParse(bankMsb.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedMsb)
                || parsedMsb > 127
                || !byte.TryParse(bankLsb.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedLsb)
                || parsedLsb > 127
                || !byte.TryParse(program.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte parsedProgram)
                || parsedProgram > 127)
            {
                result.Value = "Enter Bank MSB, Bank LSB, and Program values from 0 to 127";
                return;
            }

            result.Value = resolver.ResolveProgram(
                new InstrumentAddress(parsedMsb, parsedLsb, parsedProgram)).DisplayText;
        }

        bankMsb.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PropertyField.Value)) Refresh();
        };
        bankLsb.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PropertyField.Value)) Refresh();
        };
        program.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PropertyField.Value)) Refresh();
        };
        Refresh();
        return result;
    }

    private static IProjectEditCommand CreateSubVoiceEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one SubVoice first.");
        _ = instrument.SubVoices.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected SubVoice no longer exists.");
        return key switch
        {
            "subvoice.name" => ProjectDomainEditCommands.UpdateSubVoiceName(instrument.Id, id, value),
            "subvoice.root" => ProjectDomainEditCommands.UpdateSubVoiceRootNote(
                instrument.Id,
                id,
                NullableInt(value, "Root Note Override")),
            _ when key.StartsWith("subvoice.initial.", StringComparison.Ordinal) =>
                ProjectDomainEditCommands.UpdateSubVoiceInitialStateValue(
                    instrument.Id,
                    id,
                    ParseInitialStateTarget(key["subvoice.initial.".Length..]),
                    NullableInt(value, "Initial State Value")),
            _ => throw new InvalidOperationException("This SubVoice property is read-only.")
        };
    }

    private static IProjectEditCommand CreateLoopEdit(
        EventInstrument instrument,
        string key,
        string value)
    {
        long? edited = NullableLong(value, key == "instrument.loopStart" ? "Loop Start" : "Loop End");
        return ProjectDomainEditCommands.UpdateEventInstrumentLoop(
            instrument.Id,
            key == "instrument.loopStart" ? edited : instrument.LoopStartTick,
            key == "instrument.loopEnd" ? edited : instrument.LoopEndTick);
    }

    private static IProjectEditCommand CreateLogicalParameterEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Logical Parameter first.");
        LogicalParameterDefinition parameter = instrument.LogicalParameters.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected Logical Parameter no longer exists.");
        return key switch
        {
            "parameter.name" => ProjectDomainEditCommands.RenameLogicalParameter(instrument.Id, id, value),
            "parameter.default" => ProjectDomainEditCommands.UpdateLogicalParameterDefaultValue(
                instrument.Id, id, Double(value, "Default Value")),
            "parameter.minimum" or "parameter.maximum" => ProjectDomainEditCommands.UpdateLogicalParameterLegalRange(
                instrument.Id,
                id,
                key == "parameter.minimum" ? Double(value, "Legal Minimum") : parameter.Minimum,
                key == "parameter.maximum" ? Double(value, "Legal Maximum") : parameter.Maximum),
            "parameter.displayMinimum" or "parameter.displayMaximum" => ProjectDomainEditCommands.UpdateLogicalParameterDisplayRange(
                instrument.Id,
                id,
                key == "parameter.displayMinimum" ? Double(value, "Display Minimum") : parameter.DisplayMinimum,
                key == "parameter.displayMaximum" ? Double(value, "Display Maximum") : parameter.DisplayMaximum),
            _ => throw new InvalidOperationException("This Logical Parameter property is read-only.")
        };
    }

    private static IProjectEditCommand CreateValueCurvePointEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Value Curve point first.");
        foreach (SubVoice voice in instrument.SubVoices)
        {
            ValueCurve? curve = null;
            CurvePoint? point = null;
            foreach (ValueCurve candidate in voice.Curves)
            {
                if (!candidate.Points.TryGetById(id, out CurvePoint? resolved)
                    || resolved is null)
                {
                    continue;
                }
                curve = candidate;
                point = resolved;
                break;
            }
            if (curve is null || point is null) continue;
            return ProjectDomainEditCommands.UpdateValueCurvePoint(
                instrument.Id,
                voice.Id,
                curve.Id,
                point.Id,
                key == "valueCurvePoint.tick" ? Long(value, "Tick") : point.Tick,
                key == "valueCurvePoint.value" ? Double(value, "Value") : point.Value,
                key == "valueCurvePoint.interpolation"
                    ? EnumValue<CurveInterpolation>(value, "Interpolation")
                    : point.Interpolation);
        }
        throw new InvalidOperationException("The selected Value Curve point no longer exists.");
    }

    private static IProjectEditCommand CreateParameterMappingEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Logical Parameter Mapping first.");
        LogicalParameterMapping mapping = instrument.ParameterMappings.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected Logical Parameter Mapping no longer exists.");
        return key switch
        {
            "parameterMapping.source" =>
                ProjectDomainEditCommands.UpdateLogicalParameterMappingSource(
                    instrument.Id,
                    mapping.Id,
                    NullableId(value, "Logical Parameter")
                        ?? throw new FormatException("Select a Logical Parameter.")),
            "parameterMapping.subVoice" =>
                ProjectDomainEditCommands.UpdateLogicalParameterMappingTarget(
                    instrument.Id,
                    mapping.Id,
                    NullableId(value, "SubVoice")
                        ?? throw new FormatException("Select a SubVoice."),
                    mapping.Target),
            "parameterMapping.rounding" or "parameterMapping.overflow" =>
                ProjectDomainEditCommands.UpdateLogicalParameterMappingTargetSettings(
                    instrument.Id,
                    mapping.Id,
                    key == "parameterMapping.rounding"
                        ? EnumValue<MappingRounding>(value, "Final Rounding")
                        : mapping.TargetSettings.Rounding,
                    key == "parameterMapping.overflow"
                        ? EnumValue<MappingOverflow>(value, "Final Overflow")
                        : mapping.TargetSettings.Overflow),
            _ => throw new InvalidOperationException("This Logical Parameter Mapping property is read-only.")
        };
    }

    private static IProjectEditCommand CreateMappingChainEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId
            ?? throw new InvalidOperationException("Select one Mapping Chain first.");
        MappingChainEditingContext context = MappingEditingPolicy.Resolve(instrument, id);
        return key switch
        {
            "mappingChain.enabled" => ProjectDomainEditCommands.UpdateMappingChainEnabled(
                instrument.Id,
                context.Chain.Id,
                Bool(value, "Enabled")),
            "mappingChain.rounding" or "mappingChain.overflow" =>
                ProjectDomainEditCommands.UpdateMappingChainTargetSettings(
                    instrument.Id,
                    context.Chain.Id,
                    key == "mappingChain.rounding"
                        ? EnumValue<MappingRounding>(value, "Final Rounding")
                        : context.TargetSettings.Rounding,
                    key == "mappingChain.overflow"
                        ? EnumValue<MappingOverflow>(value, "Final Overflow")
                        : context.TargetSettings.Overflow),
            _ => throw new InvalidOperationException("This Mapping Chain property is read-only.")
        };
    }

    private static IProjectEditCommand CreateMappingStepEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Mapping Step first.");
        MappingStepContext context = FindMappingStep(instrument, id)
            ?? throw new InvalidOperationException("The selected Mapping Step no longer exists.");
        ValueMappingStep step = context.Step;
        if (key == "mappingStep.enabled")
        {
            return ProjectDomainEditCommands.UpdateMappingStepEnabled(
                instrument.Id,
                context.Chain.Id,
                step.Id,
                Bool(value, "Enabled"));
        }
        return ProjectDomainEditCommands.UpdateMappingStep(
            instrument.Id,
            context.Chain.Id,
            step.Id,
            key == "mappingStep.source" ? EnumValue<MappingSource>(value, "Source") : step.Source,
            key == "mappingStep.operation" ? EnumValue<MappingOperation>(value, "Operation") : step.Operation,
            key == "mappingStep.logicalParameter" ? NullableId(value, "Logical Parameter") : step.LogicalParameterId,
            key == "mappingStep.envelope" ? NullableId(value, "Envelope") : step.EnvelopeId,
            key == "mappingStep.function" ? NullableId(value, "Mapping Function") : step.MappingFunctionId,
            key == "mappingStep.constant" ? Double(value, "Constant") : step.Constant,
            key == "mappingStep.sourceMinimum" ? Double(value, "Source Minimum") : step.SourceMinimum,
            key == "mappingStep.sourceMaximum" ? Double(value, "Source Maximum") : step.SourceMaximum,
            key == "mappingStep.targetMinimum" ? Double(value, "Target Minimum") : step.TargetMinimum,
            key == "mappingStep.targetMaximum" ? Double(value, "Target Maximum") : step.TargetMaximum,
            key == "mappingStep.inputOverflow" ? EnumValue<MappingInputOverflow>(value, "Input Overflow") : step.InputOverflow,
            key == "mappingStep.divideByZero" ? EnumValue<DivideByZeroPolicy>(value, "Divide By Zero") : step.DivideByZero);
    }

    private static MappingStepContext? FindMappingStep(EventInstrument instrument, MidoraId id)
    {
        foreach (MappingChain chain in EnumerateMappingChains(instrument))
        {
            for (int index = 0; index < chain.Count; index++)
            {
                if (chain[index].Id == id) return new(chain, chain[index], index);
            }
        }
        return null;
    }

    private static IEnumerable<MappingChain> EnumerateMappingChains(EventInstrument instrument) =>
        instrument.ParameterMappings.Select(item => item.Steps)
            .Concat(instrument.SubVoices
                .SelectMany(voice => voice.EventMappings)
                .Select(item => item.Steps));

    private readonly record struct MappingStepContext(
        MappingChain Chain,
        ValueMappingStep Step,
        int Index);

    private static IProjectEditCommand CreateEnvelopeEdit(
        EventInstrument instrument,
        MidoraId? selectedId,
        string key,
        string value)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Envelope Preset first.");
        InstrumentEnvelope envelope = instrument.Envelopes.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected Envelope Preset no longer exists.");
        return ProjectDomainEditCommands.UpdateInstrumentEnvelope(
            instrument.Id,
            envelope.Id,
            key == "envelope.name" ? value : envelope.Name,
            key == "envelope.delay" ? Long(value, "Delay") : envelope.DelayTicks,
            key == "envelope.attack" ? Long(value, "Attack") : envelope.AttackTicks,
            key == "envelope.hold" ? Long(value, "Hold") : envelope.HoldTicks,
            key == "envelope.decay" ? Long(value, "Decay") : envelope.DecayTicks,
            key == "envelope.start" ? Double(value, "Start Value") : envelope.StartValue,
            key == "envelope.peak" ? Double(value, "Peak Value") : envelope.PeakValue,
            key == "envelope.sustain" ? Double(value, "Sustain Value") : envelope.SustainValue,
            key == "envelope.release" ? Long(value, "Release") : envelope.ReleaseTicks,
            key == "envelope.end" ? Double(value, "End Value") : envelope.EndValue);
    }

    private static string FormatMidiTarget(MidiValueTarget target) => target.Kind switch
    {
        MidiValueKind.ControlChange => MidiControlChangeCatalog.Format(target.Number),
        MidiValueKind.RegisteredParameter => $"RPN {target.Number}",
        MidiValueKind.NonRegisteredParameter => $"NRPN {target.Number}",
        _ => target.Kind.ToString()
    };

    private static string FormatEventMappingTarget(TemplateEventMappingTarget target)
    {
        if (target.EventKind == TemplateEventKind.Note)
        {
            return target.Parameter == TemplateEventMappingParameter.Number
                ? "Note · Number"
                : "Note · Velocity";
        }
        return TemplateEventMidiTargets.TryFromMappingTarget(target, out MidiValueTarget midiTarget)
            ? TemplateEventMidiTargets.Format(midiTarget)
            : $"{target.EventKind} · {target.Parameter}";
    }

    private static IProjectEditCommand CreateTemplateEventEdit(
        MidoraProject project,
        EventInstrument instrument,
        InstrumentWorkspaceViewModel workspace,
        string key,
        string value) =>
        CreateTemplateEventEdit(
            project,
            instrument,
            workspace,
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    private static IProjectEditCommand CreateTemplateEventEdit(
        MidoraProject project,
        EventInstrument instrument,
        InstrumentWorkspaceViewModel workspace,
        IReadOnlyDictionary<string, string> edits)
    {
        ArgumentNullException.ThrowIfNull(project);
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one SubVoice event first.");
        SubVoice? voice = null;
        TemplateEvent? item = null;
        foreach (SubVoice candidate in instrument.SubVoices)
        {
            if (!candidate.Events.TryGetById(id, out TemplateEvent? resolved)
                || resolved is null)
            {
                continue;
            }
            voice = candidate;
            item = resolved;
            break;
        }
        if (voice is null || item is null)
            throw new InvalidOperationException("The selected SubVoice event no longer exists.");
        bool TryValue(string key, out string value) => edits.TryGetValue(key, out value!);
        long tick = TryValue("template.tick", out string tickText)
            ? Long(tickText, "Tick")
            : item.Tick;
        int number = TryValue("template.number", out string numberText)
            ? Int(numberText, "Number")
            : item.Number;
        int eventValue = TryValue("template.value", out string valueText)
            ? item.Kind == TemplateEventKind.Program
                ? IntRange(valueText, "Program", 0, 127)
                : Int(valueText, "Value")
            : item.Value;
        int secondary = TryValue("template.secondary", out string secondaryText)
            ? Int(secondaryText, "Secondary Value")
            : item.SecondaryValue;
        return item.Kind switch
        {
            TemplateEventKind.Note => ProjectDomainEditCommands.UpdateTemplateNote(
                instrument.Id, voice.Id, item.Id, tick,
                TryValue("template.length", out string lengthText)
                    ? Long(lengthText, "Length")
                    : item.LengthTicks,
                number, eventValue,
                TryValue("template.followPitch", out string followPitchText)
                    ? Bool(followPitchText, "Follow Pitch Delta")
                    : item.FollowPitchDelta),
            TemplateEventKind.ControlChange => ProjectDomainEditCommands.UpdateTemplateControlChange(
                instrument.Id, voice.Id, item.Id, tick, number, eventValue),
            TemplateEventKind.Bank => ProjectDomainEditCommands.UpdateTemplateBank(
                instrument.Id, voice.Id, item.Id, tick,
                item.HasBankMsb ? eventValue : null,
                item.HasBankLsb ? secondary : null),
            TemplateEventKind.Program => ProjectDomainEditCommands.UpdateTemplateProgram(
                instrument.Id, voice.Id, item.Id, tick, eventValue),
            TemplateEventKind.PitchBend => ProjectDomainEditCommands.UpdateTemplatePitchBend(
                instrument.Id, voice.Id, item.Id, tick, eventValue),
            TemplateEventKind.RegisteredParameter => ProjectDomainEditCommands.UpdateTemplateRegisteredParameter(
                instrument.Id, voice.Id, item.Id, tick, number, eventValue),
            TemplateEventKind.NonRegisteredParameter => ProjectDomainEditCommands.UpdateTemplateNonRegisteredParameter(
                instrument.Id, voice.Id, item.Id, tick, number, eventValue),
            TemplateEventKind.PitchBendRange => ProjectDomainEditCommands.UpdateTemplatePitchBendRange(
                instrument.Id, voice.Id, item.Id, tick, eventValue, secondary),
            _ => throw new InvalidOperationException("Unsupported Template Event kind.")
        };
    }

    private static IProjectEditCommand CreateConductorEdit(
        MidoraProject project,
        MidoraId? selectedId,
        string key,
        string value) =>
        CreateConductorEdit(
            project,
            selectedId,
            new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    private static IProjectEditCommand CreateConductorEdit(
        MidoraProject project,
        MidoraId? selectedId,
        IReadOnlyDictionary<string, string> edits)
    {
        MidoraId id = selectedId ?? throw new InvalidOperationException("Select one Conductor event first.");
        bool TryValue(string key, out string value) => edits.TryGetValue(key, out value!);
        if (project.Conductor.Tempos.CaptureQuerySnapshot().TryGetById(id, out TempoChange tempo))
        {
            return ProjectDomainEditCommands.UpdateTempo(
                id,
                TryValue("conductor.tick", out string tickText) ? Long(tickText, "Tick") : tempo.Tick,
                TryValue("conductor.bpm", out string bpmText)
                    ? Decimal(bpmText, "Beats Per Minute")
                    : tempo.BeatsPerMinute);
        }
        if (project.Conductor.TimeSignatures.CaptureQuerySnapshot().TryGetById(id, out TimeSignatureChange signature))
        {
            return ProjectDomainEditCommands.UpdateTimeSignature(
                id,
                TryValue("conductor.tick", out string tickText) ? Long(tickText, "Tick") : signature.Tick,
                TryValue("conductor.numerator", out string numeratorText)
                    ? Int(numeratorText, "Numerator")
                    : signature.Numerator,
                TryValue("conductor.denominator", out string denominatorText)
                    ? Int(denominatorText, "Denominator")
                    : signature.Denominator);
        }
        if (project.Conductor.KeySignatures.CaptureQuerySnapshot().TryGetById(id, out KeySignatureChange keySignature))
        {
            return ProjectDomainEditCommands.UpdateKeySignature(
                id,
                TryValue("conductor.tick", out string tickText) ? Long(tickText, "Tick") : keySignature.Tick,
                TryValue("conductor.sharpsFlats", out string sharpsFlatsText)
                    ? Int(sharpsFlatsText, "Sharps / Flats")
                    : keySignature.SharpsFlats,
                TryValue("conductor.isMinor", out string isMinorText)
                    ? Bool(isMinorText, "Is Minor")
                    : keySignature.IsMinor);
        }
        if (project.Conductor.Markers.CaptureQuerySnapshot().TryGetById(id, out ProjectMarker marker))
        {
            return ProjectDomainEditCommands.UpdateProjectMarker(
                id,
                TryValue("conductor.tick", out string tickText) ? Long(tickText, "Tick") : marker.Tick,
                TryValue("conductor.name", out string nameText) ? nameText : marker.Name);
        }
        if (project.Conductor.EndMarker is ProjectEndMarker end && end.Id == id)
        {
            return ProjectDomainEditCommands.UpdateProjectEndMarker(
                TryValue("conductor.tick", out string tickText) ? Long(tickText, "Tick") : end.Tick);
        }
        throw new InvalidOperationException("The selected Conductor event no longer exists.");
    }

    private static (LogicalTrack Track, Segment Segment) FindSegmentContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        MidoraId? id = workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment }
            ? workspace.ObjectId
            : workspace.Selection.Primary;
        return TimelineWorkspaceViewModel.FindSegment(project, id)
            ?? throw new InvalidOperationException("Select one Segment first.");
    }

    private static (PureMidiTrack Track, MidiSegment Segment) FindMidiSegmentContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        MidoraId? id = workspace is TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment }
            ? workspace.ObjectId
            : workspace.Selection.Primary;
        return TimelineWorkspaceViewModel.FindMidiSegment(project, id)
            ?? throw new InvalidOperationException("Select one MIDI Segment first.");
    }

    private static (Segment Segment, LogicalNote Note) FindLogicalNoteContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        if (workspace is not TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline)
            throw new InvalidOperationException("A Logical Note must be edited in its Segment Workspace.");
        Segment segment = TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId)?.Segment
            ?? throw new InvalidOperationException("The Segment no longer exists.");
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one Logical Note first.");
        if (!segment.Notes.TryGetById(id, out LogicalNote? note) || note is null)
            throw new InvalidOperationException("The Logical Note no longer exists.");
        return (segment, note);
    }

    private static (MidiSegment Segment, DirectMidiNote Note) FindDirectMidiNoteContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        if (workspace is not TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline)
        {
            throw new InvalidOperationException(
                "A Direct MIDI Note must be edited in its MIDI Segment Workspace.");
        }
        MidiSegment segment = TimelineWorkspaceViewModel.FindMidiSegment(project, timeline.ObjectId)?.Segment
            ?? throw new InvalidOperationException("The MIDI Segment no longer exists.");
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one Direct MIDI Note first.");
        if (!segment.Notes.TryGetById(id, out DirectMidiNote? note) || note is null)
            throw new InvalidOperationException("The Direct MIDI Note no longer exists.");
        return (segment, note);
    }

    private static (MidiSegment Segment, DirectMidiChannelEvent Event) FindDirectMidiEventContext(
        MidoraProject project,
        WorkspaceViewModel workspace)
    {
        if (workspace is not TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline)
        {
            throw new InvalidOperationException(
                "A Direct MIDI Event must be edited in its MIDI Segment Workspace.");
        }
        MidiSegment segment = TimelineWorkspaceViewModel.FindMidiSegment(project, timeline.ObjectId)?.Segment
            ?? throw new InvalidOperationException("The MIDI Segment no longer exists.");
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one Direct MIDI Event first.");
        if (!segment.ChannelEvents.TryGetById(id, out DirectMidiChannelEvent? value) || value is null)
            throw new InvalidOperationException("The Direct MIDI Event no longer exists.");
        return (segment, value);
    }

    private static (Segment Segment, LogicalParameterLane Lane, CurvePoint Point)
        FindLogicalParameterPointContext(MidoraProject project, WorkspaceViewModel workspace)
    {
        if (workspace is not TimelineWorkspaceViewModel { Mode: TimelineWorkspaceMode.Segment } timeline)
            throw new InvalidOperationException("A Logical Parameter point must be edited in its Segment Workspace.");
        Segment segment = TimelineWorkspaceViewModel.FindSegment(project, timeline.ObjectId)?.Segment
            ?? throw new InvalidOperationException("The Segment no longer exists.");
        MidoraId id = workspace.Selection.Primary
            ?? throw new InvalidOperationException("Select one Logical Parameter point first.");
        LogicalParameterLane? lane = null;
        CurvePoint? point = null;
        foreach (LogicalParameterLane candidate in segment.ParameterLanes)
        {
            if (!candidate.Points.TryGetById(id, out CurvePoint? resolved)
                || resolved is null)
            {
                continue;
            }
            lane = candidate;
            point = resolved;
            break;
        }
        if (lane is null || point is null)
            throw new InvalidOperationException("The Logical Parameter point no longer exists.");
        return (segment, lane, point);
    }

    private static PropertyField Field(string key, string label, object value, bool editable = true) =>
        new(
            key,
            label,
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            editable,
            PropertyFieldValueState.SameValue,
            value.GetType().IsEnum ? Enum.GetNames(value.GetType()) : null,
            value is bool);

    private static PropertyField ChoiceField(
        string key,
        string label,
        string value,
        IEnumerable<PropertyChoiceOption> choices,
        bool editable = true) =>
        new(
            key,
            label,
            value,
            editable,
            PropertyFieldValueState.SameValue,
            options: null,
            isBoolean: false,
            choices: choices.ToArray());

    private static IReadOnlyList<PropertyChoiceOption> IdChoices(
        IEnumerable<(MidoraId Id, string Label)> values,
        MidoraId current,
        string missingLabel)
    {
        List<PropertyChoiceOption> result = values
            .Select(value => new PropertyChoiceOption(
                value.Id.Value.ToString(CultureInfo.InvariantCulture),
                value.Label))
            .ToList();
        string currentValue = current.Value.ToString(CultureInfo.InvariantCulture);
        if (result.All(value => value.Value != currentValue))
        {
            result.Add(new(currentValue, missingLabel));
        }
        return result;
    }

    private static IReadOnlyList<PropertyChoiceOption> OptionalIdChoices(
        IEnumerable<(MidoraId Id, string Label)> values,
        MidoraId? current,
        string missingLabel)
    {
        List<PropertyChoiceOption> result =
        [
            new(string.Empty, "None")
        ];
        result.AddRange(values.Select(value => new PropertyChoiceOption(
            value.Id.Value.ToString(CultureInfo.InvariantCulture),
            value.Label)));
        if (current is MidoraId currentId)
        {
            string currentValue = currentId.Value.ToString(CultureInfo.InvariantCulture);
            if (result.All(value => value.Value != currentValue))
            {
                result.Add(new(currentValue, missingLabel));
            }
        }
        return result;
    }

    private static PropertyField StateField(string key, string label, int? value) =>
        new(key, label, value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);

    private static IEnumerable<PropertyField> ExtraStateFields(string prefix, MidiInitialState state) =>
        state.Controllers.OrderBy(item => item.Key)
            .Select(item => StateField(
                $"{prefix}.cc.{item.Key}",
                $"INITIAL {MidiControlChangeCatalog.Format(item.Key)}",
                item.Value))
            .Concat(state.RegisteredParameters.OrderBy(item => item.Key)
                .Select(item => StateField($"{prefix}.rpn.{item.Key}", $"INITIAL RPN {item.Key}", item.Value)))
            .Concat(state.NonRegisteredParameters.OrderBy(item => item.Key)
                .Select(item => StateField($"{prefix}.nrpn.{item.Key}", $"INITIAL NRPN {item.Key}", item.Value)));

    private static MidiValueTarget ParseInitialStateTarget(string key)
    {
        if (TryInitialTarget(key, "cc.", MidiValueKind.ControlChange, out MidiValueTarget target)
            || TryInitialTarget(key, "rpn.", MidiValueKind.RegisteredParameter, out target)
            || TryInitialTarget(key, "nrpn.", MidiValueKind.NonRegisteredParameter, out target))
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
            _ => throw new InvalidOperationException("This Initial State target is unsupported.")
        };
    }

    private static bool TryInitialTarget(
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

    private static long Long(string value, string label) => long.TryParse(
        value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
        ? result : throw new FormatException($"{label} must be a base-10 integer.");

    private static int Int(string value, string label) => int.TryParse(
        value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
        ? result : throw new FormatException($"{label} must be a base-10 integer.");

    private static int IntRange(
        string value,
        string label,
        int minimum,
        int maximum)
    {
        int result = Int(value, label);
        return result >= minimum && result <= maximum
            ? result
            : throw new FormatException(
                $"{label} must be from {minimum.ToString(CultureInfo.InvariantCulture)} through {maximum.ToString(CultureInfo.InvariantCulture)}.");
    }

    private static int? NullableInt(string value, string label) => string.IsNullOrWhiteSpace(value)
        ? null
        : Int(value, label);

    private static long? NullableLong(string value, string label) => string.IsNullOrWhiteSpace(value)
        ? null
        : Long(value, label);

    private static MidoraId? NullableId(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        long parsed = Long(value, label);
        return parsed > 0
            ? new MidoraId(parsed)
            : throw new FormatException($"{label} must reference an existing object.");
    }

    private static decimal Decimal(string value, string label) => decimal.TryParse(
        value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal result)
        ? result : throw new FormatException($"{label} must be a decimal number using '.'.");

    private static double Double(string value, string label) => double.TryParse(
        value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
        && double.IsFinite(result)
        ? result : throw new FormatException($"{label} must be a finite number using '.'.");

    private static string DescribeMappingOperation(MappingOperation operation) =>
        operation == MappingOperation.CustomCSharp
            ? "Expression — receives the current accumulated chain value"
            : operation.ToString();

    private static T EnumValue<T>(string value, string label) where T : struct, Enum =>
        Enum.TryParse(value.Trim(), ignoreCase: true, out T result) && Enum.IsDefined(result)
            ? result
            : throw new FormatException($"{label} is not a supported {typeof(T).Name} value.");

    private static bool Bool(string value, string label) => bool.TryParse(value.Trim(), out bool result)
        ? result : throw new FormatException($"{label} must be True or False.");
}
