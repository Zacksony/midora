using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static DetachedStableIdReservation ReserveDetachedStableIds(
        MidoraProject project,
        int count)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return new(null, null, Array.Empty<MidoraId>());

        long expectedNextStableId = project.NextStableId;
        long replacementNextStableId;
        try
        {
            replacementNextStableId = checked(expectedNextStableId + count);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "The Project stable ID counter is exhausted.",
                exception);
        }
        if (replacementNextStableId > long.MaxValue)
            throw new InvalidOperationException("The Project stable ID counter is exhausted.");

        MidoraId[] ids = new MidoraId[count];
        for (int index = 0; index < ids.Length; index++)
            ids[index] = MidoraId.FromSequence(checked(expectedNextStableId + index));
        return new(expectedNextStableId, replacementNextStableId, ids);
    }

    private static IPreparedProjectEdit WithSelectionPublication(
        IPreparedProjectEdit rootSwap,
        IReadOnlyList<MidoraId> originalSelection,
        IReadOnlyList<MidoraId> resultSelection,
        SelectionPublisher publishResult)
    {
        ArgumentNullException.ThrowIfNull(rootSwap);
        ArgumentNullException.ThrowIfNull(originalSelection);
        ArgumentNullException.ThrowIfNull(resultSelection);
        ArgumentNullException.ThrowIfNull(publishResult);

        IReadOnlyList<MidoraId> frozenOriginal =
            CompactMidoraIdList.Freeze(originalSelection);
        IReadOnlyList<MidoraId> frozenResult =
            CompactMidoraIdList.Freeze(resultSelection);
        return new SelectionPublishingPreparedEdit(
            rootSwap,
            new PreparedTimelineSelection(frozenOriginal, frozenResult),
            publishResult);
    }

    private sealed class SelectionPublishingPreparedEdit(
        IPreparedProjectEdit source,
        PreparedTimelineSelection preparedSelection,
        SelectionPublisher publishResult)
        : IPreparedTimelineSelectionEdit, IPreparedProjectEditPublicationGate, IDisposable
    {
        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;
        public PreparedTimelineSelection PreparedSelection { get; } = preparedSelection;

        public void ValidateForPublication(MidoraProject project)
        {
            if (source is IPreparedProjectEditPublicationGate gate)
                gate.ValidateForPublication(project);
        }

        public void Apply(MidoraProject project)
        {
            source.Apply(project);
            publishResult(PreparedSelection.ResultSelectionIds);
        }

        public void Undo(MidoraProject project)
        {
            source.Undo(project);
            publishResult(PreparedSelection.OriginalSelectionIds);
        }

        public void Dispose()
        {
            if (source is IDisposable disposable) disposable.Dispose();
        }
    }

    private static DirectMidiNote CreateDirectNote(
        MidoraProject project,
        MidoraId preservedId,
        DirectNoteValue value)
    {
        DirectMidiNote result = new(project, preservedId);
        ApplyDirectNote(result, value);
        return result;
    }

    private readonly record struct DetachedStableIdReservation(
        long? ExpectedNextStableId,
        long? ReplacementNextStableId,
        IReadOnlyList<MidoraId> Ids);
}
