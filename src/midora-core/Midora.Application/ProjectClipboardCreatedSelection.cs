using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    // Legacy subtree paste allocates IDs when Apply runs on the private draft.
    // The surrounding SequentialProjectEditCommand applies that draft before
    // freezing its final root, so capture the top-level result at that point.
    private static IPreparedProjectEdit WithCreatedClipboardSelection(
        IPreparedProjectEdit source,
        Func<IReadOnlyList<MidoraId>> createdIds) =>
        new CreatedClipboardSelectionPrepared(source, createdIds);

    private sealed class CreatedClipboardSelectionPrepared(
        IPreparedProjectEdit source,
        Func<IReadOnlyList<MidoraId>> createdIds)
        : IPreparedTimelineSelectionEdit, IPreparedProjectEditPublicationGate, IDisposable
    {
        private PreparedTimelineSelection? _selection;
        public bool HasChanges => source.HasChanges;
        public ProjectChangeSet Changes => source.Changes;
        public PreparedTimelineSelection PreparedSelection => _selection
            ?? throw new InvalidOperationException(
                "Clipboard subtree selection is available only after applying the private draft.");

        public void Apply(MidoraProject project)
        {
            source.Apply(project);
            _selection ??= new([], CompactMidoraIdList.Freeze(createdIds()));
        }

        public void Undo(MidoraProject project) => source.Undo(project);

        public void ValidateForPublication(MidoraProject project)
        {
            if (source is IPreparedProjectEditPublicationGate gate)
                gate.ValidateForPublication(project);
        }

        public void Dispose()
        {
            if (source is IDisposable disposable) disposable.Dispose();
        }
    }
}
