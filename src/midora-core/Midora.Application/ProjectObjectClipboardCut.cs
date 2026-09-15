using Midora.Domain;

namespace Midora.Application;

public sealed record ProjectObjectClipboardCutPreparation(
    ProjectObjectClipboardPayload Payload,
    IProjectEditCommand DeleteAfterSuccessfulClipboardWrite);

public static partial class ProjectObjectClipboard
{
    public static ProjectObjectClipboardCutPreparation PrepareCutEventInstrument(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId) =>
        PrepareCut(
            document,
            CopyEventInstrument(document, eventInstrumentId),
            ProjectDomainEditCommands.DeleteEventInstrument(
                eventInstrumentId,
                referencedDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutLogicalTrack(
        ProjectDocumentSession document,
        MidoraId logicalTrackId) =>
        PrepareCut(
            document,
            CopyLogicalTrack(document, logicalTrackId),
            ProjectDomainEditCommands.DeleteLogicalTrack(
                logicalTrackId,
                nonEmptyDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutPureMidiTrack(
        ProjectDocumentSession document,
        MidoraId trackId) =>
        PrepareCut(
            document,
            CopyPureMidiTrack(document, trackId),
            ProjectDomainEditCommands.DeletePureMidiTrack(trackId, nonEmptyDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutMidiSegments(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId) =>
        PrepareCut(
            document,
            CopyMidiSegments(document, segmentIds, primarySegmentId),
            ProjectDomainEditCommands.DeleteMidiSegments(segmentIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutDirectMidiNotes(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> noteIds) =>
        PrepareCut(
            document,
            CopyDirectMidiNotes(document, segmentId, noteIds),
            ProjectDomainEditCommands.DeleteDirectMidiNotes(segmentId, noteIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutDirectMidiEvents(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        PrepareCut(
            document,
            CopyDirectMidiEvents(document, segmentId, eventIds),
            ProjectDomainEditCommands.DeleteDirectMidiEvents(segmentId, eventIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutOpaqueMidiEvents(
        ProjectDocumentSession document,
        MidoraId segmentId,
        IReadOnlyCollection<MidoraId> eventIds) =>
        PrepareCut(
            document,
            CopyOpaqueMidiEvents(document, segmentId, eventIds),
            ProjectDomainEditCommands.DeleteOpaqueMidiEvents(segmentId, eventIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutSegments(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> segmentIds,
        MidoraId primarySegmentId) =>
        PrepareCut(
            document,
            CopySegments(document, segmentIds, primarySegmentId),
            ProjectDomainEditCommands.DeleteSegments(segmentIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutLogicalNotes(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        IReadOnlyCollection<MidoraId> logicalNoteIds) =>
        PrepareCut(
            document,
            CopyLogicalNotes(document, sourceSegmentId, logicalNoteIds),
            ProjectDomainEditCommands.DeleteLogicalNotes(
                sourceSegmentId,
                logicalNoteIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutLogicalParameterLane(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        MidoraId laneId) =>
        PrepareCut(
            document,
            CopyLogicalParameterLane(document, sourceSegmentId, laneId),
            ProjectDomainEditCommands.DeleteLogicalParameterLane(
                sourceSegmentId,
                laneId,
                deletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutLogicalParameterLaneContent(
        ProjectDocumentSession document,
        MidoraId sourceSegmentId,
        MidoraId laneId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        PrepareCut(
            document,
            CopyLogicalParameterLaneContent(
                document,
                sourceSegmentId,
                laneId,
                pointIds),
            ProjectDomainEditCommands.DeleteLogicalParameterPoints(
                sourceSegmentId,
                laneId,
                pointIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutSubVoiceTimelineEvents(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceSubVoiceId,
        IReadOnlyCollection<MidoraId> templateEventIds) =>
        PrepareCut(
            document,
            CopySubVoiceTimelineEvents(
                document,
                eventInstrumentId,
                sourceSubVoiceId,
                templateEventIds),
            ProjectDomainEditCommands.DeleteTemplateEvents(
                eventInstrumentId,
                sourceSubVoiceId,
                templateEventIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutSubVoice(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId subVoiceId) =>
        PrepareCut(
            document,
            CopySubVoice(document, eventInstrumentId, subVoiceId),
            ProjectDomainEditCommands.DeleteSubVoice(
                eventInstrumentId,
                subVoiceId,
                nonEmptyDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutValueCurveContent(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceSubVoiceId,
        MidoraId sourceCurveId,
        IReadOnlyCollection<MidoraId> pointIds) =>
        PrepareCut(
            document,
            CopyValueCurveContent(
                document,
                eventInstrumentId,
                sourceSubVoiceId,
                sourceCurveId,
                pointIds),
            ProjectDomainEditCommands.DeleteValueCurvePoints(
                eventInstrumentId,
                sourceSubVoiceId,
                sourceCurveId,
                pointIds));

    public static ProjectObjectClipboardCutPreparation PrepareCutMappingChain(
        ProjectDocumentSession document,
        MidoraId eventInstrumentId,
        MidoraId sourceMappingChainId) =>
        PrepareCut(
            document,
            CopyMappingChain(document, eventInstrumentId, sourceMappingChainId),
            ProjectDomainEditCommands.DeleteMappingChain(
                eventInstrumentId,
                sourceMappingChainId,
                nonEmptyDeletionConfirmed: true));

    public static ProjectObjectClipboardCutPreparation PrepareCutConductorEvents(
        ProjectDocumentSession document,
        IReadOnlyCollection<MidoraId> eventIds) =>
        PrepareCut(
            document,
            CopyConductorEvents(document, eventIds),
            ProjectDomainEditCommands.DeleteConductorEvents(eventIds));

    private static ProjectObjectClipboardCutPreparation PrepareCut(
        ProjectDocumentSession document,
        ProjectObjectClipboardPayload payload,
        IProjectEditCommand deleteCommand)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(deleteCommand);
        try
        {
            using StagedProjectEdit validation = document.PrepareEdit(deleteCommand,
                BulkEditPreparationContext.Current?.Token ?? default);
            return new(payload, deleteCommand);
        }
        catch { payload.Dispose(); throw; }
    }
}
