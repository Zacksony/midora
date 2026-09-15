using Midora.Domain;

namespace Midora.Application;

internal enum PresentationCloneKind { LogicalTrack, MidiTrack, Instrument, SubVoice }

/// <summary>Application-only companion metadata; never part of a musical edit or canonical result.</summary>
internal sealed class ProjectPresentationCloneCommand(IProjectEditCommand inner,
    PresentationCloneKind kind, MidoraId source, MidoraId parent = default) : ICancellableProjectEditCommand
{
    public string Name => inner.Name;
    public IPreparedProjectEdit Prepare(MidoraProject project) => inner.Prepare(project);
    public IPreparedProjectEdit Prepare(MidoraProject project, CancellationToken token) =>
        inner is ICancellableProjectEditCommand cancellable ? cancellable.Prepare(project, token) : inner.Prepare(project);
    internal PresentationCloneCapture Capture(MidoraProject project) => new(kind, source, parent,
        PresentationCloneCapture.Ids(project, kind, parent).ToHashSet(),
        kind == PresentationCloneKind.Instrument
            ? project.EventInstruments.Single(i => i.Id == source).SubVoices.Select(v => v.Id).ToArray() : []);
}

internal sealed record PresentationCloneCapture(PresentationCloneKind Kind, MidoraId Source,
    MidoraId Parent, HashSet<MidoraId> OriginalIds, MidoraId[] SourceVoices)
{
    internal static IEnumerable<MidoraId> Ids(MidoraProject project, PresentationCloneKind kind, MidoraId parent) => kind switch
    {
        PresentationCloneKind.LogicalTrack => project.Tracks.Select(t => t.Id),
        PresentationCloneKind.MidiTrack => project.PureMidiTracks.Select(t => t.Id),
        PresentationCloneKind.Instrument => project.EventInstruments.Select(t => t.Id),
        _ => project.EventInstruments.Single(i => i.Id == parent).SubVoices.Select(t => t.Id)
    };
    internal IReadOnlyDictionary<MidoraId, MidoraId> Resolve(MidoraProject project)
    {
        var added = Ids(project, Kind, Parent).Where(id => !OriginalIds.Contains(id)).Take(2).ToArray();
        if (added.Length != 1) return new Dictionary<MidoraId, MidoraId>();
        Dictionary<MidoraId, MidoraId> map = new() { [Source] = added[0] };
        if (Kind == PresentationCloneKind.Instrument)
        {
            var voices = project.EventInstruments.Single(i => i.Id == added[0]).SubVoices;
            if (voices.Count != SourceVoices.Length) return new Dictionary<MidoraId, MidoraId>();
            for (int i = 0; i < voices.Count; i++) map[SourceVoices[i]] = voices[i].Id;
        }
        return map;
    }
}
