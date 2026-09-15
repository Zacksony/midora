using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyMappingChain(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceMappingChainId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        MappingChain chain = ProjectDomainEditCommands.FindMappingChainForClipboard(
            instrument,
            sourceMappingChainId);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.MappingChain,
            chain.Count,
            chain.Count == 1 ? "1 Mapping Step" : $"{chain.Count} Mapping Steps",
            new MappingChainClipboardData(eventInstrumentId, SnapshotMappingChain(chain)));
    }

    public static ProjectObjectClipboardPayload CopySubVoiceTimelineEvents(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceSubVoiceId,
        IReadOnlyCollection<MidoraId> templateEventIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(templateEventIds);
        if (templateEventIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one SubVoice timeline event must be copied.",
                nameof(templateEventIds));
        }
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        SubVoice voice = instrument.SubVoices
            .SingleOrDefault(value => value.Id == sourceSubVoiceId)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceSubVoiceId));
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(
            templateEventIds,
            nameof(templateEventIds));
        BoundedEditRecordStore<TemplateEventSnapshotValue> selected = ClipboardCaptureScope.Sort(
            EnumerateClipboardSelection(document.Project, voice.Events.CreateQuerySnapshot(), requested),
            Comparer<TemplateEventSnapshotValue>.Create((left, right) =>
            {
                int order = left.Tick.CompareTo(right.Tick);
                return order != 0 ? order : left.Id.CompareTo(right.Id);
            }), reportSelectionProgress: true);
        if (selected.Count != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Template Event must belong to the source SubVoice.",
                nameof(templateEventIds));
        }
        long earliest = selected[0].Tick;
        IReadOnlyList<TemplateEventClipboardSnapshot> snapshots = new ProjectedClipboardList<TemplateEventSnapshotValue, TemplateEventClipboardSnapshot>(selected,
            value => new(value.Kind, checked(value.Tick - earliest), value.LengthTicks,
                value.Number, value.Value, value.SecondaryValue, value.HasBankMsb, value.HasBankLsb, value.FollowPitchDelta));
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.SubVoiceTimelineEvents,
            snapshots.Count,
            snapshots.Count == 1
                ? "1 SubVoice Timeline Event"
                : $"{snapshots.Count} SubVoice Timeline Events",
            new SubVoiceTimelineEventsClipboardData(eventInstrumentId, snapshots));
    }

    public static ProjectObjectClipboardPayload CopyValueCurveContent(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceSubVoiceId,
        MidoraId sourceCurveId,
        IReadOnlyCollection<MidoraId> pointIds)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pointIds);
        if (pointIds.Count == 0)
        {
            throw new ArgumentException(
                "At least one Value Curve point must be copied.",
                nameof(pointIds));
        }
        EventInstrument instrument = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        SubVoice voice = instrument.SubVoices
            .SingleOrDefault(value => value.Id == sourceSubVoiceId)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceSubVoiceId));
        ValueCurve curve = voice.Curves
            .SingleOrDefault(value => value.Id == sourceCurveId)
            ?? throw new ArgumentOutOfRangeException(nameof(sourceCurveId));
        IReadOnlySet<MidoraId> requested = ValidateDistinctIds(pointIds, nameof(pointIds));
        IReadOnlyList<CurvePointClipboardSnapshot> points = SnapshotTimelinePointValues(
            EnumerateClipboardSelection(document.Project, curve.Points.CreateQuerySnapshot(), requested));
        if (points.Count != requested.Count)
        {
            throw new ArgumentException(
                "Every copied Value Curve point must belong to the source Curve.",
                nameof(pointIds));
        }
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.ValueCurveContent,
            points.Count,
            points.Count == 1 ? "1 Value Curve Point" : $"{points.Count} Value Curve Points",
            new ValueCurveContentClipboardData(eventInstrumentId, curve.Target, points));
    }

    public static IProjectEditCommand CreatePasteSubVoiceTimelineEventsCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        long editCursorTick,
        MidiValueTarget? target = null)
    {
        SubVoiceTimelineEventsClipboardData data =
            RequirePayload<SubVoiceTimelineEventsClipboardData>(
                targetDocument,
                payload,
                ProjectObjectClipboardKind.SubVoiceTimelineEvents);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteSubVoiceTimelineEventsClipboard(
            data,
            targetEventInstrumentId,
            targetSubVoiceId,
            editCursorTick), new(payload.Kind, targetEventInstrumentId, targetSubVoiceId, MidiTarget: target));
    }

    public static IProjectEditCommand CreatePasteValueCurveContentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        MidoraId targetCurveId,
        long editCursorTick)
    {
        ValueCurveContentClipboardData data = RequirePayload<ValueCurveContentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.ValueCurveContent);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteValueCurveContentClipboard(
            data,
            targetEventInstrumentId,
            targetSubVoiceId,
            targetCurveId,
            editCursorTick));
    }

    public static IProjectEditCommand CreatePasteMappingChainCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingChainId,
        bool nonEmptyReplacementConfirmed)
    {
        MappingChainClipboardData data = RequirePayload<MappingChainClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.MappingChain);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteMappingChainClipboard(
            data,
            targetEventInstrumentId,
            targetMappingChainId,
            nonEmptyReplacementConfirmed));
    }

    private static TemplateEventClipboardSnapshot SnapshotTemplateEvent(
        TemplateEvent value,
        long tickOffset) =>
        new(
            value.Kind,
            tickOffset,
            value.LengthTicks,
            value.Number,
            value.Value,
            value.SecondaryValue,
            value.HasBankMsb,
            value.HasBankLsb,
            value.FollowPitchDelta);

    private static MappingChainClipboardSnapshot SnapshotMappingChain(MappingChain chain)
    {
        ClipboardCaptureScope.ReserveMetadata(chain.Count);
        return new(
            chain.IsEnabled,
            chain.Select(value => new MappingStepClipboardSnapshot(
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
                value.DivideByZero)).ToArray());
    }
}

internal sealed record SubVoiceTimelineEventsClipboardData(
    MidoraId SourceEventInstrumentId,
    IReadOnlyList<TemplateEventClipboardSnapshot> Events) : ProjectObjectClipboardData;

internal sealed record ValueCurveContentClipboardData(
    MidoraId SourceEventInstrumentId,
    MidiValueTarget Target,
    IReadOnlyList<CurvePointClipboardSnapshot> Points) : ProjectObjectClipboardData;

internal sealed record MappingChainClipboardData(
    MidoraId SourceEventInstrumentId,
    MappingChainClipboardSnapshot Chain) : ProjectObjectClipboardData;

internal readonly record struct TemplateEventClipboardSnapshot(
    TemplateEventKind Kind,
    long TickOffset,
    long LengthTicks,
    int Number,
    int Value,
    int SecondaryValue,
    bool HasBankMsb,
    bool HasBankLsb,
    bool FollowPitchDelta);

internal sealed record MappingChainClipboardSnapshot(
    bool IsEnabled,
    MappingStepClipboardSnapshot[] Steps);

internal sealed record MappingStepClipboardSnapshot(
    bool IsEnabled,
    MappingSource Source,
    MappingOperation Operation,
    MidoraId? LogicalParameterId,
    MidoraId? EnvelopeId,
    MidoraId? MappingFunctionId,
    double Constant,
    double SourceMinimum,
    double SourceMaximum,
    double TargetMinimum,
    double TargetMaximum,
    MappingInputOverflow InputOverflow,
    DivideByZeroPolicy DivideByZero);

internal readonly record struct IntegerTargetSettingsClipboardSnapshot(
    MappingRounding Rounding,
    MappingOverflow Overflow);

public static partial class ProjectDomainEditCommands
{
    internal static MappingChain FindMappingChainForClipboard(
        EventInstrument instrument,
        MidoraId mappingChainId) =>
        FindMappingChain(instrument, mappingChainId);

    internal static IProjectEditCommand PasteMappingChainClipboard(
        MappingChainClipboardData snapshot,
        MidoraId targetEventInstrumentId,
        MidoraId targetMappingChainId,
        bool nonEmptyReplacementConfirmed) =>
        Command("Paste mapping chain", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "Mapping Chains can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            MappingChain target = FindMappingChain(instrument, targetMappingChainId);
            ValidateMappingChainClipboard(snapshot.Chain);
            if (target.Count != 0 && !nonEmptyReplacementConfirmed)
            {
                throw new InvalidOperationException(
                    "Replacing a non-empty Mapping Chain requires explicit confirmation.");
            }
            MappingChain? replacement = null;
            return Prepared(
                hasChanges: true,
                EventInstrumentChange(targetEventInstrumentId),
                owner =>
                {
                    if (replacement is null)
                    {
                        replacement = new MappingChain(owner);
                        ApplyMappingChainClipboard(owner, replacement, snapshot.Chain);
                    }
                    ReplaceMappingChain(instrument, target, replacement);
                },
                _ => ReplaceMappingChain(
                    instrument,
                    replacement ?? throw new InvalidOperationException(
                        "The pasted Mapping Chain does not exist before Apply."),
                    target));
        });

    internal static IProjectEditCommand PasteSubVoiceTimelineEventsClipboard(
        SubVoiceTimelineEventsClipboardData snapshot,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        long editCursorTick) =>
        Command("Paste subvoice timeline events", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0 || snapshot.Events.Count == 0)
            {
                throw new ArgumentOutOfRangeException(
                    editCursorTick < 0 ? nameof(editCursorTick) : nameof(snapshot));
            }
            if (snapshot.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "SubVoice timeline events can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, targetSubVoiceId);
            return AppendBoundedTemplateEventPoints(instrument.Id, voice.Id, snapshot.Events.Select(value =>
            {
                TemplateEventValue candidate = PrepareTemplateEventClipboardValue(value, editCursorTick).Value;
                return new TemplateEventSnapshotValue(default, candidate.Kind, candidate.Tick, candidate.LengthTicks,
                    candidate.Number, candidate.Value, candidate.SecondaryValue, candidate.HasBankMsb,
                    candidate.HasBankLsb, candidate.FollowPitchDelta);
            })).Prepare(project);
        });

    internal static IProjectEditCommand PasteValueCurveContentClipboard(
        ValueCurveContentClipboardData snapshot,
        MidoraId targetEventInstrumentId,
        MidoraId targetSubVoiceId,
        MidoraId targetCurveId,
        long editCursorTick) =>
        Command("Paste value curve points", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (editCursorTick < 0 || snapshot.Points.Count == 0)
            {
                throw new ArgumentOutOfRangeException(
                    editCursorTick < 0 ? nameof(editCursorTick) : nameof(snapshot));
            }
            if (snapshot.SourceEventInstrumentId != targetEventInstrumentId)
            {
                throw new InvalidOperationException(
                    "Value Curve content can only be pasted within the same Event Instrument.");
            }
            EventInstrument instrument = FindEventInstrument(project, targetEventInstrumentId);
            SubVoice voice = FindSubVoice(instrument, targetSubVoiceId);
            ValueCurve curve = FindValueCurve(voice, targetCurveId);
            if (curve.Target != snapshot.Target)
            {
                throw new InvalidOperationException(
                    "Value Curve content requires an exact MIDI target.");
            }
            return PrepareBoundedValueCurveClipboard(project, instrument, voice, curve, snapshot.Points, editCursorTick);
        });

    private static TemplateEventClipboardValue PrepareTemplateEventClipboardValue(
        TemplateEventClipboardSnapshot snapshot,
        long editCursorTick)
    {
        TemplateEventValue value = new(
            snapshot.Kind,
            checked(editCursorTick + snapshot.TickOffset),
            snapshot.LengthTicks,
            snapshot.Number,
            snapshot.Value,
            snapshot.SecondaryValue,
            snapshot.HasBankMsb,
            snapshot.HasBankLsb,
            snapshot.FollowPitchDelta);
        ValidateTemplateEventCreation(value);
        return new(value);
    }

    private static void ValidateMappingChainClipboard(MappingChainClipboardSnapshot chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        foreach (MappingStepClipboardSnapshot step in chain.Steps)
        {
            ValidateMappingStepValue(ToMappingStepValue(step));
        }
    }

    private static void ApplyMappingChainClipboard(
        MidoraProject project,
        MappingChain target,
        MappingChainClipboardSnapshot snapshot)
    {
        target.IsEnabled = snapshot.IsEnabled;
        foreach (MappingStepClipboardSnapshot value in snapshot.Steps)
        {
            ValueMappingStep step = new(project) { IsEnabled = value.IsEnabled };
            SetMappingStep(step, ToMappingStepValue(value));
            target.Add(step);
        }
    }

    private static MappingStepValue ToMappingStepValue(MappingStepClipboardSnapshot value) =>
        new(
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

    private readonly record struct TemplateEventClipboardValue(TemplateEventValue Value);
}
