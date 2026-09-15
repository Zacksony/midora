using Midora.Domain;
using Midora.Mapping.Contract.V2;

namespace Midora.Compiler;

public static class SemanticValidator
{
    public static List<CompilerDiagnostic> Validate(
        MidoraProject project,
        CompilationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(request);
        List<CompilerDiagnostic> diagnostics = [];
        SourceReference projectSource = new();

        if (project.TicksPerQuarterNote is < MidoraProject.MinimumTicksPerQuarterNote
            or > MidoraProject.MaximumTicksPerQuarterNote)
        {
            Error("MIDORA1001", "TicksPerQuarterNote must be in the range 1..32767.", projectSource);
        }

        if (request.StartTick < 0 || request.EndTick is < 0 || request.EndTick < request.StartTick)
        {
            Error("MIDORA1002", "The compilation range must be a non-negative, non-reversed [startTick, endTick) range.", projectSource);
        }
        if (!Enum.IsDefined(request.Purpose))
        {
            Error("MIDORA1004", "The Compilation Purpose value is invalid.", projectSource);
        }

        ValidateConductor(project, diagnostics);
        ValidateArrangementHierarchy(project, diagnostics, cancellationToken);
        ValidateState(project.GlobalInitialState, projectSource, diagnostics);
        ValidateState(project.GlobalResetDefaults, projectSource, diagnostics);

        Dictionary<MidoraId, EventInstrument> instruments = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<MidoraId> participatingInstrumentIds = GetParticipatingInstrumentIds(project, request);
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceReference source = projectSource with { EventInstrumentId = instrument.Id };
            bool participates = participatingInstrumentIds.Contains(instrument.Id);
            if (!instruments.TryAdd(instrument.Id, instrument)
                && (request.IncludedTrackIds is null || participates))
            {
                Error("MIDORA1201", "The Project contains a duplicate Event Instrument object.", source);
            }
            if ((request.IncludedTrackIds is null || participates)
                && (string.IsNullOrWhiteSpace(instrument.Name)
                    || instrument.Name != instrument.Name.Trim()
                    || !names.Add(instrument.Name)))
            {
                Error("MIDORA1202", "Event Instrument names must be non-empty after trimming and unique ignoring case.", source);
            }
            if (request.IncludedTrackIds is not null && !participates)
            {
                continue;
            }
            int internalDiagnosticStart = diagnostics.Count;
            Dictionary<MidoraId, LogicalParameterDefinition> parameters = ValidateParameterDefinitions(
                instrument.LogicalParameters, source, diagnostics);
            ValidateInstrument(instrument, parameters, source, diagnostics);
            if (!participates)
            {
                for (int i = internalDiagnosticStart; i < diagnostics.Count; i++)
                {
                    CompilerDiagnostic diagnostic = diagnostics[i];
                    if (diagnostic.Severity == DiagnosticSeverity.Error)
                    {
                        diagnostics[i] = diagnostic with { Severity = DiagnosticSeverity.Warning };
                    }
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateTracks(project, request, instruments, diagnostics, cancellationToken);
        ValidatePureMidiTracks(project, request, diagnostics, cancellationToken);
        ValidateIncludedSubVoices(project, request, diagnostics);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateStableIds(project, request, diagnostics, cancellationToken);
        return diagnostics;

        void Error(string code, string message, SourceReference source) =>
            diagnostics.Add(new(code, DiagnosticSeverity.Error, message, source));
    }

    private static void ValidateIncludedSubVoices(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics)
    {
        if (request.IncludedSubVoiceIds is null)
        {
            return;
        }

        HashSet<MidoraId> participatingInstrumentIds = GetParticipatingInstrumentIds(project, request);
        HashSet<MidoraId> participatingSubVoiceIds = project.EventInstruments
            .Where(instrument => participatingInstrumentIds.Contains(instrument.Id))
            .SelectMany(instrument => instrument.SubVoices)
            .Select(subVoice => subVoice.Id)
            .ToHashSet();
        if (request.IncludedSubVoiceIds.Any(id => !participatingSubVoiceIds.Contains(id)))
        {
            AddError(
                "MIDORA1005",
                "The compilation request references a SubVoice that does not belong to a participating Event Instrument.",
                new(),
                diagnostics);
        }
    }

    internal static HashSet<MidoraId> GetParticipatingInstrumentIds(
        MidoraProject project,
        CompilationRequest request)
    {
        HashSet<MidoraId> result = [];
        foreach (LogicalTrack track in project.Tracks)
        {
            MidoraId? instrumentId = ResolveEventInstrumentId(project, track);
            if (!instrumentId.HasValue
                || request.IncludedTrackIds is not null && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            result.Add(instrumentId.Value);
        }
        return result;
    }

    private static void ValidateConductor(MidoraProject project, List<CompilerDiagnostic> diagnostics)
    {
        SourceReference source = new();
        if (project.Conductor.Tempos.Count(change => change.Tick == 0) != 1)
        {
            AddError("MIDORA1010", "The Conductor must contain exactly one Tempo state at tick 0.", source, diagnostics);
        }
        if (project.Conductor.TimeSignatures.Count(change => change.Tick == 0) != 1)
        {
            AddError("MIDORA1011", "The Conductor must contain exactly one Time Signature state at tick 0.", source, diagnostics);
        }
        HashSet<long> tempoTicks = [];
        foreach (TempoChange tempo in project.Conductor.Tempos)
        {
            if (tempo.Tick < 0
                || !IsRepresentableTempo(tempo.BeatsPerMinute)
                || !tempoTicks.Add(tempo.Tick))
            {
                AddError("MIDORA1012", "Tempo ticks must be non-negative, BPM must be positive, and each tick must contain at most one Tempo change.", source with { Tick = tempo.Tick }, diagnostics);
            }
        }
        HashSet<long> signatureTicks = [];
        bool canAnalyzeTimeSignatureBars = true;
        foreach (TimeSignatureChange signature in project.Conductor.TimeSignatures)
        {
            bool supportedDenominator = signature.Denominator is 1 or 2 or 4 or 8 or 16 or 32 or 64;
            if (signature.Tick < 0 || signature.Numerator is < 1 or > 99
                || !supportedDenominator || !signatureTicks.Add(signature.Tick))
            {
                AddError(
                    "MIDORA1013",
                    "The Time Signature is invalid or duplicated at the same tick.",
                    source with { SourceEventId = signature.Id, Tick = signature.Tick },
                    diagnostics);
                canAnalyzeTimeSignatureBars = false;
            }
            else if (!ProjectTimeSignatureRules.IsCompatible(
                project.TicksPerQuarterNote,
                signature.Denominator))
            {
                AddError(
                    "MIDORA1017",
                    $"Time Signature denominator {signature.Denominator} is incompatible with TPQ "
                    + $"{project.TicksPerQuarterNote}; 4 × TPQ must be divisible by the denominator.",
                    source with { SourceEventId = signature.Id, Tick = signature.Tick },
                    diagnostics);
                canAnalyzeTimeSignatureBars = false;
            }
        }
        if (canAnalyzeTimeSignatureBars && signatureTicks.Contains(0))
        {
            TimeSignatureChange[] orderedSignatures = project.Conductor.TimeSignatures
                .OrderBy(value => value.Tick)
                .ThenBy(value => value.Id)
                .ToArray();
            for (int i = 1; i < orderedSignatures.Length; i++)
            {
                TimeSignatureChange previous = orderedSignatures[i - 1];
                TimeSignatureChange current = orderedSignatures[i];
                long previousBarTicks = ProjectTimeSignatureRules.GetTicksPerBar(
                    project.TicksPerQuarterNote,
                    previous.Numerator,
                    previous.Denominator);
                if ((current.Tick - previous.Tick) % previousBarTicks != 0)
                {
                    diagnostics.Add(new(
                        "MIDORA1018",
                        DiagnosticSeverity.Warning,
                        $"The Time Signature change at tick {current.Tick} truncates the previous bar; "
                        + "that tick immediately becomes beat 1 of a new bar.",
                        source with { SourceEventId = current.Id, Tick = current.Tick }));
                }
            }
        }
        if (project.Conductor.EndMarkerTick is < 0)
        {
            AddError("MIDORA1014", "The End Marker tick must not be negative.", source, diagnostics);
        }
        HashSet<long> keyTicks = [];
        foreach (KeySignatureChange key in project.Conductor.KeySignatures)
        {
            if (key.Tick < 0 || key.SharpsFlats is < -7 or > 7 || !keyTicks.Add(key.Tick))
            {
                AddError("MIDORA1015", "The Key Signature is invalid or duplicated at the same tick.", source with { Tick = key.Tick }, diagnostics);
            }
        }
        foreach (ProjectMarker marker in project.Conductor.Markers)
        {
            if (marker.Tick < 0)
            {
                AddError("MIDORA1016", "Marker ticks must not be negative; marker names may be empty or duplicated.", source with { Tick = marker.Tick }, diagnostics);
            }
        }
    }

    private static bool IsRepresentableTempo(decimal beatsPerMinute)
    {
        if (beatsPerMinute <= 0)
        {
            return false;
        }
        decimal exact;
        try
        {
            exact = 60_000_000m / beatsPerMinute;
        }
        catch (OverflowException)
        {
            return false;
        }
        decimal rounded = decimal.Round(exact, 0, MidpointRounding.AwayFromZero);
        return rounded is >= 1m and <= 16_777_215m;
    }

    private static void ValidateInstrument(
        EventInstrument instrument,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> parameters,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        if (instrument.RootNote is < 0 or > 127 || instrument.TemplateLengthTicks <= 0)
        {
            AddError("MIDORA1210", "The Event Instrument Root Note or Template Length is invalid.", source, diagnostics);
        }
        if (instrument.PreRollTicks < 0 || instrument.PreRollTicks > instrument.TemplateLengthTicks)
        {
            AddError(
                "MIDORA1217",
                "Event Instrument Pre-Roll Ticks must be between zero and Template Length Ticks.",
                source,
                diagnostics);
        }
        if (instrument.SubVoices.Count is < 1 or > 256)
        {
            AddError("MIDORA1211", "An Event Instrument must contain 1–256 SubVoices.", source, diagnostics);
        }
        if (!Enum.IsDefined(instrument.OverlapPolicy)
            || !Enum.IsDefined(instrument.OverlapScope)
            || !Enum.IsDefined(instrument.ShortLifecycle)
            || !Enum.IsDefined(instrument.LongLifecycle))
        {
            AddError("MIDORA1216", "An Event Instrument overlap or lifecycle value is invalid.", source, diagnostics);
        }
        bool hasLoop = instrument.LoopStartTick.HasValue || instrument.LoopEndTick.HasValue;
        HashSet<MidoraId> envelopeIds = instrument.Envelopes.Select(value => value.Id).ToHashSet();
        Dictionary<MidoraId, CSharpMappingFunction> functions = [];
        HashSet<string> functionNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CSharpMappingFunction function in instrument.MappingFunctions)
        {
            SourceReference functionSource = source with { MappingFunctionId = function.Id };
            string normalizedName = function.Name?.Trim() ?? string.Empty;
            if (!functions.TryAdd(function.Id, function) || normalizedName.Length == 0
                || !functionNames.Add(normalizedName))
            {
                AddError("MIDORA1273", "Mapping Function IDs and trimmed names must be non-empty and unique within the Event Instrument.", functionSource, diagnostics);
            }
        }
        bool usesEnvelope = EnumerateMappingSteps(instrument)
            .Any(value => value.Source == MappingSource.Envelope);
        if (instrument.LoopStartTick.HasValue != instrument.LoopEndTick.HasValue)
        {
            AddError(
                "MIDORA1212",
                "Loop Start and Loop End must both be present or both be absent.",
                source,
                diagnostics);
        }
        else if (hasLoop && (instrument.LoopStartTick < 0
            || instrument.LoopEndTick <= instrument.LoopStartTick
            || instrument.LoopEndTick > instrument.TemplateLengthTicks))
        {
            AddError("MIDORA1212", "The Loop must be a non-empty [loopStart, loopEnd) range within the Template.", source, diagnostics);
        }
        if ((hasLoop || usesEnvelope) && !instrument.RequiresChannelIsolation)
        {
            AddError("MIDORA1213", "Loop and Envelope require Channel Isolation.", source, diagnostics);
        }
        if (instrument.OverlapPolicy == OverlapPolicy.LetOverlap && !instrument.RequiresChannelIsolation)
        {
            AddError("MIDORA1215", "Let Overlap requires Per-Note Instance Isolation.", source, diagnostics);
        }
        bool perNoteMapping = instrument.ParameterMappings.Any(mapping =>
                ActiveSteps(mapping.Steps).Any(step => RequiresPerNoteIsolation(step, functions)))
            || instrument.SubVoices.SelectMany(value => value.EventMappings).Any(mapping =>
                mapping.Target.EventKind == TemplateEventKind.Note
                    && mapping.Target.Parameter == TemplateEventMappingParameter.Number
                    && HasActiveSteps(mapping.Steps)
                || ActiveSteps(mapping.Steps).Any(step =>
                    RequiresPerNoteIsolation(step, functions, mapping.Target)));
        if (perNoteMapping && !instrument.RequiresChannelIsolation)
        {
            AddError("MIDORA1214", "Mappings that depend on an individual Logical Note context require Channel Isolation.", source, diagnostics);
        }
        ValidateState(instrument.InitialState, source, diagnostics);
        HashSet<MidoraId> subVoiceIds = [];
        foreach (SubVoice subVoice in instrument.SubVoices)
        {
            SourceReference subSource = source with { SubVoiceId = subVoice.Id };
            if (!subVoiceIds.Add(subVoice.Id) || subVoice.RootNoteOverride is < 0 or > 127)
            {
                AddError("MIDORA1220", "The Project contains a duplicate SubVoice object or an invalid Root Note Override.", subSource, diagnostics);
            }
            ValidateState(subVoice.InitialState, subSource, diagnostics);
            HashSet<MidiValueTarget> curveTargets = [];
            foreach (ValueCurve curve in subVoice.Curves)
            {
                SourceReference curveSource = subSource with { ValueCurveId = curve.Id };
                if (!curveTargets.Add(curve.Target) || curve.Points.Count == 0)
                {
                    AddError("MIDORA1221", "Each SubVoice may contain at most one non-empty Curve for the same MIDI target.", curveSource, diagnostics);
                }
                ValidateTarget(curve.Target, curveSource, diagnostics);
                ValidateTargetSettings(curve.TargetSettings, curveSource, diagnostics);
                if (curve.Target.Kind is MidiValueKind.BankMsb or MidiValueKind.BankLsb or MidiValueKind.Program)
                {
                    AddError("MIDORA1223", "Bank and Program do not support Curves.", curveSource, diagnostics);
                }
                long previous = -1;
                foreach (CurvePointSnapshotValue point in curve.Points.CreateQuerySnapshot().EnumerateAll().OrderBy(point => point.Tick))
                {
                    if (point.Tick < 0 || point.Tick >= instrument.TemplateLengthTicks
                        || point.Tick == previous || !double.IsFinite(point.Value)
                        || !Enum.IsDefined(point.Interpolation))
                    {
                        AddError("MIDORA1222", "A Curve point has an invalid tick, value, or interpolation, or is duplicated at the same tick.", curveSource with { Tick = point.Tick }, diagnostics);
                    }
                    double minimum = curve.Target.Kind == MidiValueKind.PitchBend ? -8192 : 0;
                    double maximum = curve.Target.Kind switch
                    {
                        MidiValueKind.PitchBend => 8191,
                        MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => 16383,
                        MidiValueKind.PitchBendRangeCents => 99,
                        _ => 127
                    };
                    if ((point.Value < minimum || point.Value > maximum)
                        && curve.TargetSettings.Overflow == MappingOverflow.Fail)
                    {
                        AddError("MIDORA1224", "A Curve point is outside the target MIDI value range.", curveSource with { Tick = point.Tick }, diagnostics);
                    }
                    previous = point.Tick;
                }
            }
            HashSet<TemplateEventMappingTarget> eventMappingTargets = [];
            foreach (SubVoiceEventMapping mapping in subVoice.EventMappings)
            {
                if (!eventMappingTargets.Add(mapping.Target)
                    || !IsValidEventMappingTarget(mapping.Target))
                {
                    AddError(
                        "MIDORA1255",
                        "Each SubVoice event Mapping target must be valid and unique.",
                        subSource,
                        diagnostics);
                }
                IEnumerable<ValueMappingStep> eventSteps = ActiveSteps(mapping.Steps);
                if (mapping.Target.EventKind != TemplateEventKind.Note
                    && eventSteps.Any(step => step.Source is MappingSource.TemplateNote
                        or MappingSource.TemplateVelocity))
                {
                    AddError(
                        "MIDORA1252",
                        "Non-Note event Mappings must not use templateNote/templateVelocity Mapping Sources.",
                        subSource,
                        diagnostics);
                }
                if (mapping.Target.EventKind != TemplateEventKind.Note
                    && DeclaresNoteOnlyContext(eventSteps, functions))
                {
                    AddError(
                        "MIDORA1254",
                        "A Mapping Function expression for a non-Note event must not depend on TemplateNote/TemplateVelocity.",
                        subSource,
                        diagnostics);
                }
                ValidateMappings(mapping.Steps, subSource, diagnostics);
                ValidateTargetSettings(mapping.TargetSettings, subSource, diagnostics);
                if (mapping.Target.EventKind == TemplateEventKind.Note
                    && mapping.Target.Parameter == TemplateEventMappingParameter.Number
                    && mapping.TargetSettings.Overflow != MappingOverflow.Fail)
                {
                    AddError(
                        "MIDORA1276",
                        "The final-overflow policy for a Note number must be Fail.",
                        subSource,
                        diagnostics);
                }
                ValidateMappingReferences(
                    eventSteps,
                    parameters,
                    envelopeIds,
                    functions,
                    subSource,
                    diagnostics);
            }
            foreach (TemplateEventSnapshotValue templateEvent in subVoice.Events.CreateQuerySnapshot().EnumerateAll())
            {
                ValidateTemplateEvent(templateEvent, instrument.TemplateLengthTicks, subSource, diagnostics);
                if (TemplateEventMappingTarget.Enumerate(templateEvent)
                    .Any(target => target.EventKind == TemplateEventKind.Note
                        && !eventMappingTargets.Contains(target)))
                {
                    AddError(
                        "MIDORA1256",
                        "The SubVoice is missing a mandatory shared Note Mapping definition.",
                        subSource with { SourceEventId = templateEvent.Id },
                        diagnostics);
                }
                if (hasLoop && templateEvent.Kind == TemplateEventKind.Note
                    && templateEvent.Tick >= instrument.LoopStartTick
                    && templateEvent.Tick < instrument.LoopEndTick
                    && templateEvent.Tick + templateEvent.LengthTicks > instrument.LoopEndTick)
                {
                    AddError("MIDORA1248", "A Note that starts inside a Loop must not extend its NoteOff beyond the Loop End.", subSource with { SourceEventId = templateEvent.Id }, diagnostics);
                }
            }
        }
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
        {
            SourceReference mappingSource = source with
            {
                SubVoiceId = mapping.SubVoiceId,
                LogicalParameterId = mapping.ParameterId,
                LogicalParameterMappingId = mapping.Id
            };
            if (!parameters.ContainsKey(mapping.ParameterId))
            {
                AddError("MIDORA1230", "The Parameter Mapping references an unavailable Logical Parameter.", mappingSource, diagnostics);
            }
            if (!subVoiceIds.Contains(mapping.SubVoiceId))
            {
                AddError("MIDORA1235", "The Parameter Mapping references an unavailable SubVoice.", mappingSource, diagnostics);
            }
            ValidateTarget(mapping.Target, mappingSource, diagnostics);
            ValidateTargetSettings(mapping.TargetSettings, mappingSource, diagnostics);
            ValidateMappings(mapping.Steps, mappingSource, diagnostics);
            ValidateMappingReferences(ActiveSteps(mapping.Steps), parameters, envelopeIds, functions, mappingSource, diagnostics);
            if (ActiveSteps(mapping.Steps)
                .Any(step => step.Source is MappingSource.TemplateNote or MappingSource.TemplateVelocity))
            {
                AddError("MIDORA1252", "Logical Parameter Mapping must not use TemplateNote/TemplateVelocity.",
                    mappingSource, diagnostics);
            }
            if (DeclaresNoteOnlyContext(ActiveSteps(mapping.Steps), functions))
            {
                AddError("MIDORA1254", "A Logical Parameter Mapping Function expression must not depend on TemplateNote/TemplateVelocity.",
                    mappingSource, diagnostics);
            }
        }
        foreach (IGrouping<(MidoraId SubVoiceId, MidiValueTarget Target), LogicalParameterMapping> group in
            instrument.ParameterMappings.Where(value => value.Steps.IsEnabled)
                .GroupBy(value => (value.SubVoiceId, value.Target)))
        {
            LogicalParameterMapping first = group.First();
            if (group.Skip(1).Any(value => value.TargetSettings.Rounding != first.TargetSettings.Rounding
                || value.TargetSettings.Overflow != first.TargetSettings.Overflow))
            {
                AddError("MIDORA1275", "Logical Parameter Mappings for the same SubVoice and target parameter must share rounding and final-overflow policies.",
                    source with { SubVoiceId = group.Key.SubVoiceId }, diagnostics);
            }
        }
        foreach (InstrumentEnvelope envelope in instrument.Envelopes)
        {
            if (envelope.DelayTicks < 0 || envelope.AttackTicks < 0 || envelope.HoldTicks < 0
                || envelope.DecayTicks < 0 || envelope.ReleaseTicks < 0
                || !double.IsFinite(envelope.StartValue) || !double.IsFinite(envelope.PeakValue)
                || !double.IsFinite(envelope.SustainValue) || !double.IsFinite(envelope.EndValue)
                || envelope.StartValue is < 0 or > 1 || envelope.PeakValue is < 0 or > 1
                || envelope.SustainValue is < 0 or > 1 || envelope.EndValue is < 0 or > 1)
            {
                AddError("MIDORA1231", "Envelope times and values must be valid.",
                    source with { EnvelopeId = envelope.Id }, diagnostics);
            }
        }
    }

    private static void ValidateTemplateEvent(TemplateEventSnapshotValue value, long templateLength, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        SourceReference eventSource = source with { SourceEventId = value.Id, Tick = value.Tick };
        if (!Enum.IsDefined(value.Kind))
        {
            AddError("MIDORA1249", "The Template Event type value is invalid.", eventSource, diagnostics);
        }
        if (value.Tick < 0 || value.Tick >= templateLength)
        {
            AddError("MIDORA1240", "The Template Event must be within the Template.", eventSource, diagnostics);
        }
        switch (value.Kind)
        {
            case TemplateEventKind.Note:
                if (value.LengthTicks <= 0 || value.Number is < 0 or > 127 || value.Value is < 1 or > 127
                    || value.Tick > long.MaxValue - Math.Max(value.LengthTicks, 0)
                    || value.Tick + Math.Max(value.LengthTicks, 0) > templateLength)
                {
                    AddError("MIDORA1241", "The Template Note has an invalid length, note, or velocity, or its NoteOff exceeds the Template Length.", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.ControlChange:
                if (value.Number is < 0 or > 119 || value.Number is 91 or 93 || value.Value is < 0 or > 127)
                {
                    AddError("MIDORA1242", "Ordinary CC numbers must be in 0–119, and CC91/CC93 are prohibited in the initial release.", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.Bank:
                if (!value.HasBankMsb && !value.HasBankLsb
                    || value.HasBankMsb && value.Value is < 0 or > 127
                    || value.HasBankLsb && value.SecondaryValue is < 0 or > 127)
                {
                    AddError("MIDORA1243", "Bank must contain at least one of MSB/LSB, and each present value must be in 0–127.", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.Program:
                if (value.Value is < 0 or > 127)
                {
                    AddError("MIDORA1244", "Program must be in 0–127.", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.PitchBend:
                if (value.Value is < -8192 or > 8191)
                {
                    AddError("MIDORA1245", "Pitch Bend must be in -8192–8191.", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.RegisteredParameter:
            case TemplateEventKind.NonRegisteredParameter:
                if (value.Number is < 0 or > 16383 || value.Value is < 0 or > 16383)
                {
                    AddError("MIDORA1246", "RPN/NRPN parameter and data values must be 14-bit.", eventSource, diagnostics);
                }
                break;
            case TemplateEventKind.PitchBendRange:
                if (value.Value is < 0 or > 127 || value.SecondaryValue is < 0 or > 99)
                {
                    AddError("MIDORA1247", "Pitch Bend Range must use 0–127 semitones and 0–99 cents.", eventSource, diagnostics);
                }
                break;
        }
    }

    private static void ValidateArrangementHierarchy(
        MidoraProject project,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        Dictionary<MidoraId, EventInstrument> instruments = project.EventInstruments
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());
        Dictionary<MidoraId, EventInstrumentUsage> usages = project.EventInstrumentUsages
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());
        Dictionary<MidoraId, MidiChannelRoot> roots = project.MidiChannelRoots
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());
        Dictionary<MidoraId, LogicalTrack> logicalTracks = project.Tracks
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());
        Dictionary<MidoraId, PureMidiTrack> midiTracks = project.PureMidiTracks
            .GroupBy(value => value.Id)
            .ToDictionary(value => value.Key, value => value.First());

        HashSet<(ArrangementTrackKind Kind, MidoraId Id)> ordered = [];
        for (int index = 0; index < project.ArrangementTracks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArrangementTrackReference reference = project.ArrangementTracks[index];
            bool exists = reference.Kind switch
            {
                ArrangementTrackKind.LogicalTrack => logicalTracks.ContainsKey(reference.TrackId),
                ArrangementTrackKind.PureMidiTrack => midiTracks.ContainsKey(reference.TrackId),
                _ => false
            };
            if (!exists || !ordered.Add((reference.Kind, reference.TrackId)))
            {
                AddError(
                    "MIDORA1401",
                    "The global Arrangement Track order contains a missing, duplicated, or kind-mismatched Track.",
                    reference.Kind == ArrangementTrackKind.LogicalTrack
                        ? new(TrackId: reference.TrackId)
                        : new(PureMidiTrackId: reference.TrackId),
                    diagnostics);
            }
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            if (!ordered.Contains((ArrangementTrackKind.LogicalTrack, track.Id)))
            {
                AddError(
                    "MIDORA1402",
                    "Every Logical Track must appear exactly once in the global Arrangement Track order.",
                    new(TrackId: track.Id),
                    diagnostics);
            }
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            if (!ordered.Contains((ArrangementTrackKind.PureMidiTrack, track.Id)))
            {
                AddError(
                    "MIDORA1403",
                    "Every Pure MIDI Track must appear exactly once in the global Arrangement Track order.",
                    new(PureMidiTrackId: track.Id),
                    diagnostics);
            }
        }

        foreach (EventInstrumentUsage usage in project.EventInstrumentUsages)
        {
            SourceReference usageSource = new(
                EventInstrumentId: usage.EventInstrumentId,
                EventInstrumentUsageId: usage.Id);
            // A missing or damaged Definition is retained as a recoverable broken
            // reference. Participating Tracks report MIDORA1303/MIDORA1305 below;
            // unrelated scoped consumers must not be poisoned by that damage.
            if (!project.Tracks.Any(value => value.EventInstrumentUsageId == usage.Id))
            {
                AddError(
                    "MIDORA1411",
                    "An Event Instrument Usage must have at least one Logical Track member.",
                    usageSource,
                    diagnostics);
            }
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            bool hasContent = track.Segments.Count != 0;
            if (track.EventInstrumentUsageId is not MidoraId usageId)
            {
                if (hasContent)
                {
                    AddError(
                        "MIDORA1412",
                        "An unbound Logical Track must be an empty shell before it can carry musical content.",
                        new(TrackId: track.Id),
                        diagnostics);
                }
                continue;
            }
            if (!usages.TryGetValue(usageId, out EventInstrumentUsage? usage))
            {
                AddError(
                    "MIDORA1413",
                    "A Logical Track references a missing Event Instrument Usage.",
                    new(TrackId: track.Id, EventInstrumentUsageId: usageId),
                    diagnostics);
                continue;
            }
        }

        ValidateContiguousGroup(
            project,
            project.Tracks
                .Where(value => value.EventInstrumentUsageId.HasValue)
                .GroupBy(value => value.EventInstrumentUsageId!.Value)
                .Where(value => value.Count() > 1)
                .Select(value => (value.Key, value.Select(track => track.Id).ToHashSet())),
            "MIDORA1415",
            "Logical Tracks sharing one Event Instrument Usage must be contiguous in the global Arrangement order.",
            diagnostics,
            groupIsMidiRoot: false);

        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            SourceReference rootSource = new(MidiChannelRootId: root.Id);
            PureMidiTrack[] members = project.PureMidiTracks
                .Where(value => value.MidiChannelRootId == root.Id)
                .ToArray();
            if (members.Length == 0)
            {
                AddError(
                    "MIDORA1420",
                    "A MIDI Channel Root must have at least one Pure MIDI Track member.",
                    rootSource,
                    diagnostics);
            }
            if (string.IsNullOrWhiteSpace(root.Name)
                || root.Name != root.Name.Trim()
                || !Enum.IsDefined(root.RoutingMode)
                || !Enum.IsDefined(root.ChannelMode)
                || root.FixedZeroBasedPort > 15
                || root.FixedZeroBasedChannel > 15)
            {
                AddError(
                    "MIDORA1421",
                    "A MIDI Channel Root has an invalid name, routing mode, channel mode, Port, or Channel.",
                    rootSource,
                    diagnostics);
            }
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            if (!roots.ContainsKey(track.MidiChannelRootId))
            {
                AddError(
                    "MIDORA1422",
                    "A Pure MIDI Track references a missing MIDI Channel Root.",
                    new(MidiChannelRootId: track.MidiChannelRootId, PureMidiTrackId: track.Id),
                    diagnostics);
            }
        }
        foreach (IGrouping<(byte Port, byte Channel), MidiChannelRoot> collision in project.MidiChannelRoots
            .Where(value => value.RoutingMode == MidiChannelRootRoutingMode.Fixed)
            .GroupBy(value => (value.FixedZeroBasedPort, value.FixedZeroBasedChannel))
            .Where(value => value.Count() > 1))
        {
            foreach (MidiChannelRoot root in collision)
            {
                AddError(
                    "MIDORA1423",
                    "Fixed MIDI Channel Roots cannot own the same Port.Channel.",
                    new(MidiChannelRootId: root.Id),
                    diagnostics);
            }
        }
        ValidateContiguousGroup(
            project,
            project.MidiChannelRoots
                .Where(value => value.RoutingMode == MidiChannelRootRoutingMode.Auto)
                .Select(value => (
                    value.Id,
                    project.PureMidiTracks
                        .Where(track => track.MidiChannelRootId == value.Id)
                        .Select(track => track.Id)
                        .ToHashSet()))
                .Where(value => value.Item2.Count > 1),
            "MIDORA1424",
            "Pure MIDI Tracks sharing one Auto Root must be contiguous in the global Arrangement order.",
            diagnostics,
            groupIsMidiRoot: true);
    }

    private static void ValidateContiguousGroup(
        MidoraProject project,
        IEnumerable<(MidoraId GroupId, HashSet<MidoraId> TrackIds)> groups,
        string code,
        string message,
        List<CompilerDiagnostic> diagnostics,
        bool groupIsMidiRoot)
    {
        Dictionary<MidoraId, int> positions = project.ArrangementTracks
            .Select((value, index) => (value.TrackId, index))
            .GroupBy(value => value.TrackId)
            .ToDictionary(value => value.Key, value => value.First().index);
        foreach ((MidoraId groupId, HashSet<MidoraId> trackIds) in groups)
        {
            int[] members = trackIds
                .Where(positions.ContainsKey)
                .Select(value => positions[value])
                .Order()
                .ToArray();
            if (members.Length > 1 && members[^1] - members[0] + 1 != members.Length)
            {
                AddError(
                    code,
                    message,
                    groupIsMidiRoot
                        ? new(MidiChannelRootId: groupId)
                        : new(EventInstrumentUsageId: groupId),
                    diagnostics);
            }
        }
    }

    private static Dictionary<MidoraId, LogicalParameterDefinition> ValidateParameterDefinitions(
        IReadOnlyList<LogicalParameterDefinition> definitions,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        Dictionary<MidoraId, LogicalParameterDefinition> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (LogicalParameterDefinition parameter in definitions)
        {
            SourceReference parameterSource = source with { LogicalParameterId = parameter.Id };
            if (!result.TryAdd(parameter.Id, parameter) || string.IsNullOrWhiteSpace(parameter.Name)
                || parameter.Name != parameter.Name.Trim() || !names.Add(parameter.Name))
            {
                AddError("MIDORA1101", "Logical Parameter IDs and trimmed names must be non-empty and unique within the Event Instrument.", parameterSource, diagnostics);
            }
            if (!Enum.IsDefined(parameter.Type)
                || !double.IsFinite(parameter.Minimum) || !double.IsFinite(parameter.Maximum)
                || !double.IsFinite(parameter.DisplayMinimum) || !double.IsFinite(parameter.DisplayMaximum)
                || !double.IsFinite(parameter.DefaultValue) || parameter.Maximum < parameter.Minimum
                || parameter.DisplayMaximum < parameter.DisplayMinimum
                || parameter.DefaultValue < parameter.Minimum || parameter.DefaultValue > parameter.Maximum)
            {
                AddError("MIDORA1102", $"Logical Parameter '{parameter.Name}' has an invalid type, valid range, display range, or default value.", parameterSource, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Integer
                && (parameter.Minimum != Math.Truncate(parameter.Minimum)
                    || parameter.Maximum != Math.Truncate(parameter.Maximum)
                    || parameter.DefaultValue != Math.Truncate(parameter.DefaultValue)))
            {
                AddError("MIDORA1103", $"Integer parameter '{parameter.Name}' must use integer valid-range bounds and an integer default value.", parameterSource, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Enum && parameter.EnumItems.Count == 0)
            {
                AddError("MIDORA1104", $"Enum parameter '{parameter.Name}' must contain at least one enum item.", parameterSource, diagnostics);
            }
            if (parameter.Type == LogicalParameterType.Enum)
            {
                HashSet<string> enumNames = new(StringComparer.OrdinalIgnoreCase);
                HashSet<int> enumValues = [];
                for (int i = 0; i < parameter.EnumItems.Count; i++)
                {
                    LogicalParameterEnumItem item = parameter.EnumItems[i];
                    int effectiveValue = parameter.UsesExplicitEnumValues ? item.Value : i;
                    if (string.IsNullOrWhiteSpace(item.Name) || item.Name != item.Name.Trim()
                        || !enumNames.Add(item.Name) || !enumValues.Add(effectiveValue)
                        || effectiveValue < parameter.Minimum || effectiveValue > parameter.Maximum)
                    {
                        AddError("MIDORA1105", $"Enum parameter '{parameter.Name}' contains an invalid, duplicated, or out-of-range item name or value.", parameterSource, diagnostics);
                    }
                }
                if (parameter.DefaultValue < int.MinValue || parameter.DefaultValue > int.MaxValue
                    || parameter.DefaultValue != Math.Truncate(parameter.DefaultValue)
                    || !enumValues.Contains((int)parameter.DefaultValue))
                {
                    AddError("MIDORA1106", $"Enum parameter '{parameter.Name}' has a default value that is not a valid enum item.", parameterSource, diagnostics);
                }
            }
        }
        return result;
    }

    private static void ValidateTracks(
        MidoraProject project,
        CompilationRequest request,
        IReadOnlyDictionary<MidoraId, EventInstrument> instruments,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        HashSet<MidoraId> allTrackIds = [];
        HashSet<MidoraId> participatingTrackIds = [];
        HashSet<MidoraId> damagedInstrumentIds = project.DamagedEventInstruments
            .Select(value => value.Id)
            .ToHashSet();
        foreach (LogicalTrack track in project.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceReference trackSource = new(TrackId: track.Id);
            allTrackIds.Add(track.Id);
            if (request.IncludedTrackIds is not null
                && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            if (!participatingTrackIds.Add(track.Id))
            {
                AddError("MIDORA1301", "Logical Track IDs must be unique; names may be empty or duplicated.", trackSource, diagnostics);
            }
            EventInstrument? boundInstrument = null;
            bool boundInstrumentIsDamaged = false;
            MidoraId? resolvedInstrumentId = ResolveEventInstrumentId(project, track);
            if (resolvedInstrumentId.HasValue
                && !instruments.TryGetValue(resolvedInstrumentId.Value, out boundInstrument))
            {
                boundInstrumentIsDamaged = damagedInstrumentIds.Contains(resolvedInstrumentId.Value);
                diagnostics.Add(boundInstrumentIsDamaged
                    ? new("MIDORA1305", DiagnosticSeverity.Error,
                        "The Event Instrument bound to the Logical Track is damaged; the Track is excluded from compilation.", trackSource with
                        {
                            EventInstrumentId = resolvedInstrumentId.Value,
                            EventInstrumentUsageId = track.EventInstrumentUsageId ?? default
                        })
                    : new("MIDORA1303", DiagnosticSeverity.Info,
                        "The Logical Track has a broken Event Instrument reference; it is treated as unbound for this compilation.", trackSource));
            }
            if (request.Purpose == CompilationPurpose.SegmentPreview
                && boundInstrument is null
                && !boundInstrumentIsDamaged)
            {
                AddError("MIDORA1306",
                    "Segment Preview requires the Logical Track to be bound to an available Event Instrument.",
                    trackSource,
                    diagnostics);
            }
            if (boundInstrument is null && !boundInstrumentIsDamaged
                && track.Segments.Any(segment => segment.Notes.Count != 0 || segment.ParameterLanes.Count != 0))
            {
                diagnostics.Add(new("MIDORA1304", DiagnosticSeverity.Info,
                    "A non-empty Logical Track without an Event Instrument binding produces no compiled output.", trackSource));
            }
            Dictionary<MidoraId, LogicalParameterDefinition> parameters = [];
            if (boundInstrument is not null)
            {
                foreach (LogicalParameterDefinition parameter in boundInstrument.LogicalParameters)
                {
                    parameters.TryAdd(parameter.Id, parameter);
                }
            }
            long? previousEndTick = null;
            foreach (Segment segment in track.Segments.OrderBy(segment => segment.ProjectStartTick).ThenBy(segment => segment.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceReference segmentSource = trackSource with { SegmentId = segment.Id, Tick = segment.ProjectStartTick };
                bool projectRangeRepresentable = segment.LengthTicks > 0
                    && segment.ProjectStartTick >= 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks;
                bool contentRangeRepresentable = segment.LengthTicks > 0
                    && segment.ContentOffsetTick >= 0
                    && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;
                if (!projectRangeRepresentable || !contentRangeRepresentable)
                {
                    AddError("MIDORA1310", "The Segment position, length, or Content Offset is invalid, or its time range exceeds Int64.", segmentSource, diagnostics);
                }
                if (projectRangeRepresentable && previousEndTick > segment.ProjectStartTick)
                {
                    AddError("MIDORA1311", "Segments on the same Logical Track must not overlap.", segmentSource, diagnostics);
                }
                if (projectRangeRepresentable)
                {
                    previousEndTick = segment.ProjectStartTick + segment.LengthTicks;
                }
                HashSet<MidoraId> laneIds = [];
                foreach (LogicalParameterLane lane in segment.ParameterLanes)
                {
                    if (!laneIds.Add(lane.ParameterId))
                    {
                        AddError("MIDORA1312", "A Segment contains duplicate Lanes for the same Logical Parameter.", segmentSource, diagnostics);
                    }
                    else if (boundInstrument is not null && !parameters.ContainsKey(lane.ParameterId))
                    {
                        diagnostics.Add(new("MIDORA1314", DiagnosticSeverity.Warning,
                            "A Segment parameter Lane has an unavailable Logical Parameter reference; its data is preserved but excluded from compilation.", segmentSource));
                    }
                    long prior = -1;
                    parameters.TryGetValue(lane.ParameterId, out LogicalParameterDefinition? definition);
                    foreach (CurvePointSnapshotValue point in lane.Points.CreateQuerySnapshot().EnumerateAll().OrderBy(point => point.Tick))
                    {
                        if (point.Tick < 0 || point.Tick == prior || !double.IsFinite(point.Value)
                            || !Enum.IsDefined(point.Interpolation))
                        {
                            AddError("MIDORA1313", "A parameter Lane point has an invalid tick, value, or interpolation, or is duplicated at the same tick.", segmentSource with { Tick = point.Tick }, diagnostics);
                        }
                        if (definition is not null)
                        {
                            if (point.Value < definition.Minimum || point.Value > definition.Maximum)
                            {
                                AddError("MIDORA1315", "A parameter Lane point is outside the Logical Parameter valid range.", segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                            if (point.Interpolation != CurveInterpolation.Step)
                            {
                                AddError("MIDORA1316", "Logical Parameter points only permit discrete Step changes.", segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                            if (definition.Type == LogicalParameterType.Integer
                                && point.Value != Math.Truncate(point.Value))
                            {
                                AddError("MIDORA1317", "An Integer Logical Parameter point must be an integer.",
                                    segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                            if (definition.Type == LogicalParameterType.Enum
                                && (point.Value < int.MinValue || point.Value > int.MaxValue
                                    || point.Value != Math.Truncate(point.Value)
                                    || !GetEnumValues(definition).Contains((int)point.Value)))
                            {
                                AddError("MIDORA1318", "An Enum Logical Parameter point must reference a valid enum value.",
                                    segmentSource with { Tick = point.Tick }, diagnostics);
                            }
                        }
                        prior = point.Tick;
                    }
                }
                foreach (LogicalNoteSnapshotValue note in segment.Notes.CreateQuerySnapshot().EnumerateAll())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SourceReference noteSource = segmentSource with
                    {
                        LogicalNoteId = note.Id,
                        EventInstrumentId = resolvedInstrumentId ?? default,
                        EventInstrumentUsageId = track.EventInstrumentUsageId ?? default,
                        Tick = note.StartTick
                    };
                    if (note.StartTick < 0 || note.LengthTicks <= 0
                        || note.Note is < 0 or > 127 || note.Velocity is < 1 or > 127
                        || note.StartTick > long.MaxValue - Math.Max(note.LengthTicks, 0))
                    {
                        AddError("MIDORA1320", "The Logical Note position, length, note, or velocity is invalid, or its time range exceeds Int64.", noteSource, diagnostics);
                    }
                    if (boundInstrument is not null
                        && boundInstrument.PreRollTicks is >= 0
                        && boundInstrument.PreRollTicks <= boundInstrument.TemplateLengthTicks
                        && contentRangeRepresentable
                        && note.StartTick >= segment.ContentOffsetTick
                        && note.StartTick < segment.ContentOffsetTick + segment.LengthTicks
                        && note.StartTick - segment.ContentOffsetTick < boundInstrument.PreRollTicks)
                    {
                        AddError(
                            "MIDORA1321",
                            "The Logical Note does not leave enough active Segment content before its anchor for the Event Instrument Pre-Roll.",
                            noteSource,
                            diagnostics);
                    }
                }
            }
        }
        if (request.IncludedTrackIds is not null
            && request.IncludedTrackIds.Any(id => !allTrackIds.Contains(id)
                && project.PureMidiTracks.All(track => track.Id != id)))
        {
            AddError("MIDORA1302", "The compilation request references a missing Logical Track.", new(), diagnostics);
        }
    }

    private static HashSet<int> GetEnumValues(LogicalParameterDefinition definition)
    {
        HashSet<int> values = [];
        for (int i = 0; i < definition.EnumItems.Count; i++)
        {
            values.Add(definition.UsesExplicitEnumValues ? definition.EnumItems[i].Value : i);
        }
        return values;
    }

    private static void ValidateStableIds(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        HashSet<MidoraId> ids = [];
        HashSet<MidoraId> participatingInstrumentIds = GetParticipatingInstrumentIds(project, request);
        foreach (TempoChange value in project.Conductor.Tempos) Add(value.Id, new(Tick: value.Tick));
        foreach (TimeSignatureChange value in project.Conductor.TimeSignatures) Add(value.Id, new(Tick: value.Tick));
        foreach (KeySignatureChange value in project.Conductor.KeySignatures) Add(value.Id, new(Tick: value.Tick));
        foreach (ProjectMarker marker in project.Conductor.Markers) Add(marker.Id, new(Tick: marker.Tick));
        if (project.Conductor.EndMarker is not null)
        {
            Add(project.Conductor.EndMarker.Id, new(Tick: project.Conductor.EndMarker.Tick));
        }
        foreach (EventInstrument instrument in project.EventInstruments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.IncludedTrackIds is not null
                && !participatingInstrumentIds.Contains(instrument.Id))
            {
                continue;
            }
            SourceReference instrumentSource = new(EventInstrumentId: instrument.Id);
            Add(instrument.Id, instrumentSource);
            foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
            {
                Add(parameter.Id, instrumentSource);
                foreach (LogicalParameterEnumItem item in parameter.EnumItems) Add(item.Id, instrumentSource);
            }
            foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
            {
                Add(mapping.Id, instrumentSource);
                Add(mapping.Steps.Id, instrumentSource);
                foreach (ValueMappingStep step in mapping.Steps) Add(step.Id, instrumentSource);
            }
            foreach (InstrumentEnvelope envelope in instrument.Envelopes) Add(envelope.Id, instrumentSource);
            foreach (CSharpMappingFunction function in instrument.MappingFunctions) Add(function.Id, instrumentSource);
            foreach (SubVoice voice in instrument.SubVoices)
            {
                SourceReference voiceSource = instrumentSource with { SubVoiceId = voice.Id };
                Add(voice.Id, voiceSource);
                foreach (SubVoiceEventMapping mapping in voice.EventMappings)
                {
                    Add(mapping.Steps.Id, voiceSource);
                    foreach (ValueMappingStep step in mapping.Steps) Add(step.Id, voiceSource);
                }
                foreach (TemplateEventSnapshotValue value in voice.Events.CreateQuerySnapshot().EnumerateAll())
                {
                    SourceReference eventSource = voiceSource with { SourceEventId = value.Id, Tick = value.Tick };
                    Add(value.Id, eventSource);
                }
                foreach (ValueCurve curve in voice.Curves)
                {
                    Add(curve.Id, voiceSource);
                    foreach (CurvePointSnapshotValue point in curve.Points.CreateQuerySnapshot().EnumerateAll()) Add(point.Id, voiceSource with { Tick = point.Tick });
                }
            }
        }
        foreach (DamagedProjectObject damagedInstrument in project.DamagedEventInstruments)
        {
            if (request.IncludedTrackIds is not null
                && !participatingInstrumentIds.Contains(damagedInstrument.Id))
            {
                continue;
            }
            Add(damagedInstrument.Id, new(EventInstrumentId: damagedInstrument.Id));
        }
        foreach (LogicalTrack track in project.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.IncludedTrackIds is not null
                && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            SourceReference trackSource = new(TrackId: track.Id);
            Add(track.Id, trackSource);
            foreach (Segment segment in track.Segments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceReference segmentSource = trackSource with { SegmentId = segment.Id, Tick = segment.ProjectStartTick };
                Add(segment.Id, segmentSource);
                foreach (LogicalNoteSnapshotValue note in segment.Notes.CreateQuerySnapshot().EnumerateAll())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Add(note.Id, segmentSource with { LogicalNoteId = note.Id });
                }
                foreach (LogicalParameterLane lane in segment.ParameterLanes)
                {
                    Add(lane.Id, segmentSource);
                    foreach (CurvePointSnapshotValue point in lane.Points.CreateQuerySnapshot().EnumerateAll()) Add(point.Id, segmentSource with { Tick = point.Tick });
                }
            }
        }
        foreach (DamagedProjectObject damagedTrack in project.DamagedLogicalTracks)
        {
            if (request.IncludedTrackIds is not null
                && !request.IncludedTrackIds.Contains(damagedTrack.Id))
            {
                continue;
            }
            Add(damagedTrack.Id, new(TrackId: damagedTrack.Id));
        }
        foreach (MidiChannelRoot root in project.MidiChannelRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(root.Id, new(MidiChannelRootId: root.Id));
        }
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.IncludedTrackIds is not null
                && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            SourceReference trackSource = new(
                MidiChannelRootId: track.MidiChannelRootId,
                PureMidiTrackId: track.Id);
            Add(track.Id, trackSource);
            foreach (MidiSegment segment in track.Segments)
            {
                SourceReference segmentSource = trackSource with
                {
                    SegmentId = segment.Id,
                    MidiSegmentId = segment.Id,
                    Tick = segment.ProjectStartTick
                };
                Add(segment.Id, segmentSource);
                foreach (DirectMidiNoteValue note in segment.Notes.EditedValues)
                {
                    Add(note.Id, segmentSource with
                    {
                        DirectMidiObjectId = note.Id,
                        Tick = note.StartTick,
                        Origin = SourceOrigin.DirectMidiNote
                    });
                }
                foreach (DirectMidiChannelEventValue directEvent in
                    segment.ChannelEvents.EditedValues)
                {
                    Add(directEvent.Id, segmentSource with
                    {
                        DirectMidiObjectId = directEvent.Id,
                        Tick = directEvent.Tick,
                        Origin = SourceOrigin.DirectMidiChannelEvent
                    });
                }
                foreach (OpaqueMidiEventValue opaque in segment.OpaqueEvents.EditedValues)
                {
                    Add(opaque.Id, segmentSource with
                    {
                        DirectMidiObjectId = opaque.Id,
                        Tick = opaque.Tick,
                        Origin = SourceOrigin.OpaqueMidiEvent
                    });
                }
            }
        }
        foreach (DamagedProjectObject damagedRoot in project.DamagedMidiChannelRoots)
        {
            Add(damagedRoot.Id, new(MidiChannelRootId: damagedRoot.Id));
        }
        foreach (DamagedProjectObject damagedTrack in project.DamagedPureMidiTracks)
        {
            Add(damagedTrack.Id, new(PureMidiTrackId: damagedTrack.Id));
        }

        void Add(MidoraId id, SourceReference source)
        {
            if (id == default || id.Value >= project.NextStableId || !ids.Add(id))
            {
                AddError("MIDORA1003",
                    "The Project contains empty, duplicated, or out-of-range internal object data.",
                    source, diagnostics);
            }
        }
    }

    private static void ValidateState(MidiInitialState state, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        if (state.BankMsb is < 0 or > 127 || state.BankLsb is < 0 or > 127 || state.Program is < 0 or > 127
            || state.PitchBend is < -8192 or > 8191 || state.PitchBendRangeSemitones is < 0 or > 127
            || state.PitchBendRangeCents is < 0 or > 99)
        {
            AddError("MIDORA1250", "The Initial State contains an invalid MIDI value.", source, diagnostics);
        }
        foreach ((int controller, int value) in state.Controllers)
        {
            if (controller is < 0 or > 119 || controller is 91 or 93 || value is < 0 or > 127)
            {
                AddError("MIDORA1251", "Initial State CC numbers must be in 0–119, values in 0–127, and CC91/CC93 are prohibited.", source, diagnostics);
            }
        }
        foreach ((int parameter, int value) in state.RegisteredParameters.Concat(state.NonRegisteredParameters))
        {
            if (parameter is < 0 or > 16383 || value is < 0 or > 16383)
            {
                AddError("MIDORA1252", "Initial State RPN/NRPN parameter and data values must be 14-bit.", source, diagnostics);
            }
        }
    }

    private static void ValidateTarget(MidiValueTarget target, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        bool invalid = !Enum.IsDefined(target.Kind) || target.Kind switch
        {
            MidiValueKind.ControlChange => target.Number is < 0 or > 119 || target.Number is 91 or 93,
            MidiValueKind.RegisteredParameter or MidiValueKind.NonRegisteredParameter => target.Number is < 0 or > 16383,
            _ => target.Number != 0
        };
        if (invalid)
        {
            AddError("MIDORA1260", "The Mapping/Curve target is invalid or uses prohibited CC91/CC93.", source, diagnostics);
        }
    }

    private static void ValidateTargetSettings(
        MidiIntegerTargetSettings settings,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        if (!Enum.IsDefined(settings.Rounding) || !Enum.IsDefined(settings.Overflow))
        {
            AddError("MIDORA1277", "An integer target parameter has an invalid rounding or final-overflow policy.", source, diagnostics);
        }
    }

    private static void ValidateMappings(MappingChain steps, SourceReference source, List<CompilerDiagnostic> diagnostics)
    {
        foreach (ValueMappingStep step in ActiveSteps(steps))
        {
            SourceReference stepSource = source with
            {
                MappingStepId = step.Id,
                MappingFunctionId = step.MappingFunctionId ?? default,
                LogicalParameterId = step.LogicalParameterId ?? source.LogicalParameterId,
                EnvelopeId = step.EnvelopeId ?? default
            };
            if (!Enum.IsDefined(step.Source) || !Enum.IsDefined(step.Operation)
                || !Enum.IsDefined(step.InputOverflow) || !Enum.IsDefined(step.DivideByZero)
                || !double.IsFinite(step.Constant) || !double.IsFinite(step.SourceMinimum)
                || !double.IsFinite(step.SourceMaximum) || !double.IsFinite(step.TargetMinimum)
                || !double.IsFinite(step.TargetMaximum) || step.SourceMaximum < step.SourceMinimum
                || step.TargetMaximum < step.TargetMinimum)
            {
                AddError("MIDORA1270", "A Mapping step has an invalid enum configuration or numeric range.", stepSource, diagnostics);
            }
            if (step.Operation == MappingOperation.CustomCSharp && !step.MappingFunctionId.HasValue)
            {
                AddError("MIDORA1271", "A Mapping Function step is missing its Mapping Function reference.", stepSource, diagnostics);
            }
        }
    }

    private static IEnumerable<ValueMappingStep> EnumerateMappingSteps(EventInstrument instrument) =>
        instrument.ParameterMappings.SelectMany(value => ActiveSteps(value.Steps))
            .Concat(instrument.SubVoices
                .SelectMany(value => value.EventMappings)
                .SelectMany(value => ActiveSteps(value.Steps)));

    private static bool IsValidEventMappingTarget(TemplateEventMappingTarget target) =>
        TemplateEventMappingTarget.IsSupported(target)
        && target.EventKind switch
        {
            TemplateEventKind.ControlChange =>
                target.EventNumber is >= 0 and <= 119 and not 91 and not 93,
            TemplateEventKind.RegisteredParameter or TemplateEventKind.NonRegisteredParameter =>
                target.EventNumber is >= 0 and <= 16_383,
            _ => true
        };

    private static IEnumerable<ValueMappingStep> ActiveSteps(MappingChain chain) =>
        chain.IsEnabled ? chain.Where(value => value.IsEnabled) : [];

    private static bool HasActiveSteps(MappingChain chain) => chain.IsEnabled && chain.Any(value => value.IsEnabled);

    private static bool DeclaresNoteOnlyContext(
        IEnumerable<ValueMappingStep> steps,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions) =>
        steps.Any(step => step.Operation == MappingOperation.CustomCSharp
            && step.MappingFunctionId.HasValue
            && functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function)
            && (function.DeclaredContextFields.Contains(nameof(MappingContextV2.TemplateNote))
                || function.DeclaredContextFields.Contains(nameof(MappingContextV2.TemplateVelocity))));

    private static void ValidateMappingReferences(
        IEnumerable<ValueMappingStep> steps,
        IReadOnlyDictionary<MidoraId, LogicalParameterDefinition> parameters,
        IReadOnlySet<MidoraId> envelopes,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        SourceReference source,
        List<CompilerDiagnostic> diagnostics)
    {
        foreach (ValueMappingStep step in steps)
        {
            SourceReference stepSource = source with
            {
                MappingStepId = step.Id,
                MappingFunctionId = step.MappingFunctionId ?? default,
                LogicalParameterId = step.LogicalParameterId ?? source.LogicalParameterId,
                EnvelopeId = step.EnvelopeId ?? default
            };
            if (step.Operation != MappingOperation.CustomCSharp
                && step.Source == MappingSource.LogicalParameter
                && (!step.LogicalParameterId.HasValue || !parameters.ContainsKey(step.LogicalParameterId.Value)))
            {
                AddError("MIDORA1232", "A Mapping Step references an unavailable Logical Parameter.", stepSource, diagnostics);
            }
            if (step.Operation != MappingOperation.CustomCSharp
                && step.Source == MappingSource.Envelope
                && (!step.EnvelopeId.HasValue || !envelopes.Contains(step.EnvelopeId.Value)))
            {
                AddError("MIDORA1233", "A Mapping Step references an unavailable Envelope Preset.", stepSource, diagnostics);
            }
            if (step.Operation == MappingOperation.CustomCSharp
                && (!step.MappingFunctionId.HasValue || !functions.ContainsKey(step.MappingFunctionId.Value)))
            {
                AddError("MIDORA1234", "A Mapping Step references an unavailable Mapping Function.", stepSource, diagnostics);
            }
        }
    }

    private static void AddError(string code, string message, SourceReference source, List<CompilerDiagnostic> diagnostics) =>
        diagnostics.Add(new(code, DiagnosticSeverity.Error, message, source));

    private static bool IsPerNoteStep(ValueMappingStep step) => step.Source is
        MappingSource.TriggerNote or MappingSource.TriggerVelocity or MappingSource.GateLength or MappingSource.PitchDelta;

    private static bool RequiresPerNoteIsolation(
        ValueMappingStep step,
        IReadOnlyDictionary<MidoraId, CSharpMappingFunction> functions,
        TemplateEventMappingTarget? target = null)
    {
        bool directSourceRequiresIsolation = step.Operation != MappingOperation.CustomCSharp
            && IsPerNoteStep(step)
            && !(target.HasValue
                && IsIsolationFreeTriggerVelocityToNoteVelocity(step, target.Value));
        bool functionRequiresIsolation = step.Operation == MappingOperation.CustomCSharp
            && step.MappingFunctionId.HasValue
            && functions.TryGetValue(step.MappingFunctionId.Value, out CSharpMappingFunction? function)
            && function.DeclaredContextFields.Any(IsPerNoteContextField);
        return directSourceRequiresIsolation || functionRequiresIsolation;
    }

    private static void ValidatePureMidiTracks(
        MidoraProject project,
        CompilationRequest request,
        List<CompilerDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        HashSet<MidoraId> allTrackIds = project.Tracks.Select(value => value.Id).ToHashSet();
        foreach (PureMidiTrack track in project.PureMidiTracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            allTrackIds.Add(track.Id);
            if (request.IncludedTrackIds is not null && !request.IncludedTrackIds.Contains(track.Id))
            {
                continue;
            }
            SourceReference trackSource = new(
                MidiChannelRootId: track.MidiChannelRootId,
                PureMidiTrackId: track.Id);
            if (string.IsNullOrWhiteSpace(track.Name) || track.Name != track.Name.Trim())
            {
                AddError(
                    "MIDORA1430",
                    "A Pure MIDI Track name must be non-empty after trimming.",
                    trackSource,
                    diagnostics);
            }
            long? previousEndTick = null;
            foreach (MidiSegment segment in track.Segments
                .OrderBy(value => value.ProjectStartTick)
                .ThenBy(value => value.Id))
            {
                SourceReference segmentSource = trackSource with
                {
                    MidiSegmentId = segment.Id,
                    SegmentId = segment.Id,
                    Tick = segment.ProjectStartTick
                };
                bool projectRangeRepresentable = segment.LengthTicks > 0
                    && segment.ProjectStartTick >= 0
                    && segment.ProjectStartTick <= long.MaxValue - segment.LengthTicks;
                bool contentRangeRepresentable = segment.LengthTicks > 0
                    && segment.ContentOffsetTick >= 0
                    && segment.ContentOffsetTick <= long.MaxValue - segment.LengthTicks;
                if (!projectRangeRepresentable || !contentRangeRepresentable)
                {
                    AddError(
                        "MIDORA1431",
                        "The MIDI Segment position, length, or Content Offset is invalid, or its range exceeds Int64.",
                        segmentSource,
                        diagnostics);
                }
                if (projectRangeRepresentable && previousEndTick > segment.ProjectStartTick)
                {
                    AddError(
                        "MIDORA1432",
                        "MIDI Segments on the same Pure MIDI Track must not overlap.",
                        segmentSource,
                        diagnostics);
                }
                if (projectRangeRepresentable)
                {
                    previousEndTick = segment.ProjectStartTick + segment.LengthTicks;
                }
                foreach (DirectMidiNoteValue note in segment.Notes.EditedValues)
                {
                    if (note.StartTick < 0
                        || note.LengthTicks <= 0
                        || note.StartTick > long.MaxValue - Math.Max(0, note.LengthTicks)
                        || note.Key is < 0 or > 127
                        || note.NoteOnVelocity is < 1 or > 127
                        || note.NoteOffVelocity is < 0 or > 127
                        || note.NoteOnOrder < 0
                        || note.NoteOffOrder < 0)
                    {
                        AddError(
                            "MIDORA1433",
                            "A Direct MIDI Note has an invalid tick, gate, key, velocity, or explicit order.",
                            segmentSource with
                            {
                                DirectMidiObjectId = note.Id,
                                Tick = note.StartTick,
                                Origin = SourceOrigin.DirectMidiNote
                            },
                            diagnostics);
                    }
                }
                foreach (DirectMidiChannelEventValue directEvent in
                    segment.ChannelEvents.EditedValues)
                {
                    bool oneByte = directEvent.Kind is DirectMidiChannelEventKind.ProgramChange
                        or DirectMidiChannelEventKind.ChannelPressure;
                    if (directEvent.Tick < 0
                        || !Enum.IsDefined(directEvent.Kind)
                        || directEvent.Data1 is < 0 or > 127
                        || directEvent.Data2 is < 0 or > 127
                        || oneByte && directEvent.Data2 != 0
                        || directEvent.Order < 0)
                    {
                        AddError(
                            "MIDORA1434",
                            "A Direct MIDI Channel Event has an invalid tick, kind, data byte, or explicit order.",
                            segmentSource with
                            {
                                DirectMidiObjectId = directEvent.Id,
                                Tick = directEvent.Tick,
                                Origin = SourceOrigin.DirectMidiChannelEvent
                            },
                            diagnostics);
                    }
                }
                foreach (OpaqueMidiEventValue opaque in segment.OpaqueEvents.EditedValues)
                {
                    if (opaque.Tick < 0
                        || !Enum.IsDefined(opaque.Kind)
                        || opaque.Kind == OpaqueMidiEventKind.Meta
                            && opaque.MetaType == 0x2f
                        || opaque.Order < 0)
                    {
                        AddError(
                            "MIDORA1435",
                            "An opaque imported MIDI event has an invalid tick, kind, payload, Meta type, or explicit order.",
                            segmentSource with
                            {
                                DirectMidiObjectId = opaque.Id,
                                Tick = opaque.Tick,
                                Origin = SourceOrigin.OpaqueMidiEvent
                            },
                            diagnostics);
                    }
                }
            }
        }
        if (request.IncludedTrackIds is not null
            && request.IncludedTrackIds.Any(id => !allTrackIds.Contains(id)))
        {
            AddError(
                "MIDORA1436",
                "The compilation request references a missing Logical or Pure MIDI Track.",
                new(),
                diagnostics);
        }
    }

    private static bool IsIsolationFreeTriggerVelocityToNoteVelocity(
        ValueMappingStep step,
        TemplateEventMappingTarget target) =>
        step.Source == MappingSource.TriggerVelocity
        && target.EventKind == TemplateEventKind.Note
        && target.Parameter == TemplateEventMappingParameter.Value;

    private static MidoraId? ResolveEventInstrumentId(
        MidoraProject project,
        LogicalTrack track) => project.ResolveEventInstrumentDefinitionId(track);

    private static bool IsPerNoteContextField(string field) => field is
        nameof(MappingContextV2.TriggerNote) or nameof(MappingContextV2.TriggerVelocity)
        or nameof(MappingContextV2.EffectiveRootNote) or nameof(MappingContextV2.PitchDelta)
        or nameof(MappingContextV2.GateLength) or nameof(MappingContextV2.SegmentLocalTick);
}
