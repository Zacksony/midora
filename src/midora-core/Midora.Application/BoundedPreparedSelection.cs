using Midora.Domain;

namespace Midora.Application;

internal static class BoundedPreparedSelection
{
    private readonly record struct OrderedId(MidoraId Id, int Ordinal);

    internal static PreparedTimelineSelection Combine(MidoraProject originalProject, MidoraProject finalProject, ProjectChangeSet changes,
        IReadOnlyList<PreparedTimelineSelection> selections, List<IDisposable> owned)
    {
        var context = BulkEditPreparationContext.Current
            ?? throw new InvalidOperationException("Combining a detached selection requires a preparation context.");
        var before = BuildResolvers(originalProject);
        var after = BuildResolvers(finalProject);
        return new(Freeze(selections.SelectMany(static s => s.OriginalSelectionIds), before.Resolvers, before.SegmentIds),
            Freeze(selections.SelectMany(static s => s.ResultSelectionIds), after.Resolvers, after.SegmentIds));

        (List<Func<IReadOnlySet<MidoraId>, IEnumerable<MidoraId>>> Resolvers, HashSet<MidoraId> SegmentIds)
            BuildResolvers(MidoraProject project)
        {
            var resolvers = new List<Func<IReadOnlySet<MidoraId>, IEnumerable<MidoraId>>>();
            var segmentIds = new HashSet<MidoraId>();
            foreach (var track in project.Tracks.Where(t => changes.AffectsEverything || changes.TrackIds.Contains(t.Id)))
                foreach (var segment in track.Segments)
                {
                    segmentIds.Add(segment.Id);
                    var notes = segment.Notes.CreateQuerySnapshot();
                    notes.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
                    resolvers.Add(ids => ids.Where(id => notes.TryFindOrdinalById(id, out _)));
                    foreach (var lane in segment.ParameterLanes)
                    {
                        var points = lane.Points.CreateQuerySnapshot();
                        points.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
                        resolvers.Add(ids => ids.Where(id => points.TryFindOrdinalById(id, out _)));
                    }
                }
            foreach (var track in project.PureMidiTracks.Where(t => changes.AffectsEverything || changes.PureMidiTrackIds.Contains(t.Id)))
                foreach (var segment in track.Segments)
                {
                    segmentIds.Add(segment.Id);
                    var notes = segment.Notes.CreateObjectSource();
                    var events = segment.ChannelEvents.CreateObjectSource();
                    var opaque = segment.OpaqueEvents.CreateObjectSource();
                    resolvers.Add(ids => notes.QueryByIds(ids).Select(static value => value.Value.Id));
                    resolvers.Add(ids => events.QueryByIds(ids).Select(static value => value.Value.Id));
                    resolvers.Add(ids => opaque.QueryAddressesByIds(ids).Select(static value => value.Id));
                }
            foreach (var instrument in project.EventInstruments.Where(i => changes.AffectsEverything || changes.EventInstrumentIds.Contains(i.Id)))
                foreach (var voice in instrument.SubVoices)
                {
                    var events = voice.Events.CreateQuerySnapshot();
                    events.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
                    resolvers.Add(ids => ids.Where(id => events.TryFindOrdinalById(id, out _)));
                    foreach (var curve in voice.Curves)
                    {
                        var points = curve.Points.CreateQuerySnapshot();
                        points.PrepareOrdinalLookup(BoundedTimelineOrdinalIndexBuilder.Instance, context.Token);
                        resolvers.Add(ids => ids.Where(id => points.TryFindOrdinalById(id, out _)));
                    }
                }
            return (resolvers, segmentIds);
        }

        BoundedImmutableValueSource<MidoraId> Freeze(IEnumerable<MidoraId> ids,
            List<Func<IReadOnlySet<MidoraId>, IEnumerable<MidoraId>>> resolvers, HashSet<MidoraId> segmentIds)
        {
            using var sorted = BoundedEditSort.Sort(ids.Select(static (id, ordinal) => new OrderedId(id, ordinal)),
                Comparer<OrderedId>.Create(static (a, b) => a.Id != b.Id ? a.Id.CompareTo(b.Id) : a.Ordinal.CompareTo(b.Ordinal)), context.Resources, context.Token);
            using var retained = new BoundedEditRecordStore<OrderedId>(context.Resources);
            using var working = context.Resources.ReserveWorking(4096L * 96);
            var batch = new List<OrderedId>(4096);
            HashSet<MidoraId> remaining = [], found = [];
            MidoraId previous = default;
            foreach (var row in sorted.ReadValues(context.Token))
            {
                if (row.Id == previous) continue;
                previous = row.Id;
                batch.Add(row);
                if (batch.Count == 4096) Flush();
            }
            Flush();
            retained.Seal();
            using var formal = BoundedEditSort.Sort(retained, Comparer<OrderedId>.Create(static (a, b) => a.Ordinal.CompareTo(b.Ordinal)), context.Resources, context.Token);
            var result = new BoundedEditRecordStore<MidoraId>(context.Resources);
            try
            {
                result.AddRange(formal.Select(static r => r.Id), context.Token); result.Seal(); result.SpillResidentPages(context.Token);
                var provider = new BoundedImmutableValueSource<MidoraId>(result); owned.Add(provider); return provider;
            }
            catch { result.Dispose(); throw; }
            void Flush()
            {
                if (batch.Count == 0) return;
                context.Token.ThrowIfCancellationRequested();
                remaining.Clear(); found.Clear();
                foreach (var row in batch)
                    if (segmentIds.Contains(row.Id)) found.Add(row.Id); else remaining.Add(row.Id);
                foreach (var resolve in resolvers)
                {
                    if (remaining.Count == 0) break;
                    foreach (var id in resolve(remaining)) found.Add(id);
                    remaining.ExceptWith(found);
                }
                foreach (var row in batch) if (found.Contains(row.Id)) retained.Add(row, context.Token);
                batch.Clear();
            }
        }
    }
}
