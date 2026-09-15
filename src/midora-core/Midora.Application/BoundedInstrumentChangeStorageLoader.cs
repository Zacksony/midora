using Midora.Domain;

namespace Midora.Application;

public sealed class BoundedInstrumentChangeStorageLoader : IInstrumentChangeStorageLoader
{
    public static BoundedInstrumentChangeStorageLoader Instance { get; } = new();
    private BoundedInstrumentChangeStorageLoader() { }
    public void PrepareSource(TemplateEventQuerySnapshot source, CancellationToken token) => InstrumentChangeSelectionQuery.PrepareSource(source, token);
    public void Load(MidoraProject project, IEnumerable<InstrumentChangeOwnerRecord> records, CancellationToken token)
    {
        using var scope = BulkEditPreparationContext.Enter(token, project: project);
        using var resources = new BoundedEditResourceLease(scope.Resources);
        using var sorted = BoundedEditSort.Sort(records, Comparer<InstrumentChangeOwnerRecord>.Create(static (a, b) =>
        { int c = a.OwnerId.CompareTo(b.OwnerId); return c != 0 ? c : a.Change.Id.CompareTo(b.Change.Id); }), scope.Resources, token);
        int at = 0;
        var midi = project.PureMidiTracks.SelectMany(static t => t.Segments).ToDictionary(static s => s.Id);
        var voices = project.EventInstruments.SelectMany(static i => i.SubVoices).ToDictionary(static v => v.Id);
        while (at < sorted.Count)
        {
            token.ThrowIfCancellationRequested(); var first = sorted[at];
            var root = new InstrumentChangeSet(BoundedInstrumentChangeStorage.Create(ReadOwner()));
            if (first.DirectMidi) midi[first.OwnerId].InstrumentChanges = root;
            else voices[first.OwnerId].InstrumentChanges = root;
            IEnumerable<InstrumentChange> ReadOwner()
            {
                while (at < sorted.Count && sorted[at].OwnerId == first.OwnerId)
                {
                    var row = sorted[at++];
                    if (row.DirectMidi != first.DirectMidi) throw new InvalidDataException("Instrument Change owner type mismatch.");
                    yield return row.Change;
                }
            }
        }
        using var published = resources.Complete(token);
        published.MarkPublished();
    }
}
