using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardPayload CopyEventInstrument(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId)
    {
        using ClipboardCaptureScope capture = ClipboardCaptureScope.Enter();
        ArgumentNullException.ThrowIfNull(document);
        EventInstrument source = document.Project.EventInstruments
            .SingleOrDefault(value => value.Id == eventInstrumentId)
            ?? throw new ArgumentOutOfRangeException(nameof(eventInstrumentId));
        ReserveInstrumentClipboardMetadata(source);
        MidoraProject snapshotProject = new(document.Project.TicksPerQuarterNote);
        EventInstrument snapshot = ProjectCompilationSnapshot.CloneInstrument(snapshotProject, source,
            BulkEditPreparationContext.Current!.Token);
        return new(
            document.ClipboardSessionIdentity,
            ProjectObjectClipboardKind.EventInstrument,
            1,
            $"Event Instrument: {source.Name}",
            new EventInstrumentClipboardData(snapshot));
    }

    public static IProjectEditCommand CreatePasteEventInstrumentCommand(
        ProjectDocumentSession targetDocument,
        ProjectObjectClipboardPayload payload,
        int? insertionIndex = null)
    {
        EventInstrumentClipboardData data = RequirePayload<EventInstrumentClipboardData>(
            targetDocument,
            payload,
            ProjectObjectClipboardKind.EventInstrument);
        return KeepClipboardAlive(payload, ProjectDomainEditCommands.PasteEventInstrumentClipboard(
            data.Snapshot,
            insertionIndex));
    }

    private static void ReserveInstrumentClipboardMetadata(EventInstrument instrument)
    {
        ClipboardCaptureScope.ReserveMetadata(checked(1L + instrument.LogicalParameters.Count
            + instrument.MappingFunctions.Count + instrument.Envelopes.Count + instrument.ParameterMappings.Count));
        ClipboardCaptureScope.ReserveMetadata(instrument.SubVoices.Count, 2048);
        ReserveState(instrument.InitialState);
        foreach (LogicalParameterDefinition parameter in instrument.LogicalParameters)
            ClipboardCaptureScope.ReserveMetadata(parameter.EnumItems.Count, 128);
        foreach (CSharpMappingFunction function in instrument.MappingFunctions)
            ClipboardCaptureScope.ReserveMetadata(function.DeclaredContextFields.Count, 128);
        foreach (LogicalParameterMapping mapping in instrument.ParameterMappings)
            ClipboardCaptureScope.ReserveMetadata(mapping.Steps.Count);
        foreach (SubVoice voice in instrument.SubVoices)
        {
            ReserveState(voice.InitialState);
            ClipboardCaptureScope.ReserveMetadata(checked((long)voice.EventMappings.Count + voice.Curves.Count));
            foreach (SubVoiceEventMapping mapping in voice.EventMappings)
                ClipboardCaptureScope.ReserveMetadata(mapping.Steps.Count);
        }
        static void ReserveState(MidiInitialState state) => ClipboardCaptureScope.ReserveMetadata(
            checked((long)state.Controllers.Count + state.RegisteredParameters.Count
                + state.NonRegisteredParameters.Count), 128);
    }
}

internal sealed record EventInstrumentClipboardData(
    EventInstrument Snapshot) : ProjectObjectClipboardData;

public static partial class ProjectDomainEditCommands
{
    internal static IProjectEditCommand PasteEventInstrumentClipboard(
        EventInstrument snapshot,
        int? insertionIndex) =>
        Command("Paste event instrument", project =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            int index = insertionIndex ?? project.EventInstruments.Count;
            ValidateInsertionIndex(index, project.EventInstruments.Count, nameof(insertionIndex));
            EventInstrument? copy = null;
            return Prepared(
                hasChanges: true,
                EverythingChange(),
                owner =>
                {
                    if (copy is null)
                    {
                        copy = CopyBoundedEventInstrument(
                            owner,
                            snapshot,
                            name: null);
                        // CopyInto registers the new definition immediately. Paste owns the
                        // final insertion position, so remove that provisional append before
                        // inserting the same object at the requested index.
                        RemoveRequired(
                            owner.EventInstruments,
                            copy,
                            "provisionally appended Event Instrument copy");
                    }
                    else
                    {
                        EnsureEventInstrumentIdAvailable(owner, copy.Id);
                    }
                    InsertAt(
                        owner.EventInstruments,
                        index,
                        copy,
                        "pasted Event Instrument");
                },
                owner =>
                {
                    EventInstrument value = copy ?? throw new InvalidOperationException(
                        "The pasted Event Instrument does not exist before Apply.");
                    RemoveRequired(owner.EventInstruments, value, "pasted Event Instrument");
                });
        });
}
