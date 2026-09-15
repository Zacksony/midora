using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Application;

internal static class TemplateEventTargetQueryIndex
{
    private static readonly ConditionalWeakTable<TemplateEventCollection, Cache> Caches = new();

    public static IReadOnlyList<(int Index, TemplateEvent Value)> Query(
        TemplateEventCollection events,
        MidiValueTarget target)
    {
        ArgumentNullException.ThrowIfNull(events);
        Cache cache = Caches.GetValue(events, static _ => new Cache());
        TemplateEventQuerySnapshot snapshot = events.CreateQuerySnapshot();
        long discoveryKey = TemplateEventMidiTargets.EncodeDiscoveryKey(target);
        lock (cache.Sync)
        {
            if (cache.Generation != snapshot.Generation)
            {
                cache.ByTarget = Build(snapshot);
                cache.Generation = snapshot.Generation;
            }
            if (!cache.ByTarget.TryGetValue(discoveryKey, out MidoraId[]? ids))
                return [];
            return events.ResolveByIdsWithIndicesInCollectionOrder(ids);
        }
    }

    private static Dictionary<long, MidoraId[]> Build(TemplateEventQuerySnapshot snapshot)
    {
        if (snapshot.DiscoveryKeys.Count == 0) return [];

        Dictionary<long, List<MidoraId>> mutable = snapshot.DiscoveryKeys
            .ToDictionary(static key => key, static _ => new List<MidoraId>());
        foreach (TemplateEventSnapshotValue value in snapshot.EnumerateAll())
        {
            foreach (long key in TemplateEventMidiTargets.EnumerateDiscoveryKeys(value))
            {
                if (mutable.TryGetValue(key, out List<MidoraId>? ids)) ids.Add(value.Id);
            }
        }
        return mutable.ToDictionary(
            static value => value.Key,
            static value => value.Value.ToArray());
    }

    private sealed class Cache
    {
        public object Sync { get; } = new();
        public long Generation { get; set; } = -1;
        public Dictionary<long, MidoraId[]> ByTarget { get; set; } = [];
    }
}
