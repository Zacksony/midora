using System.Runtime.CompilerServices;

namespace Midora.Domain;

/// <summary>
/// Shares immutable paged-source ID resolutions between revision snapshots and
/// their live collection. Overlay and exclusion decisions remain revision-local.
/// </summary>
internal sealed class PureMidiSourceIdResolutionCache<TMatch>(
    Func<TMatch, MidoraId> getId,
    Func<TMatch, int> getIndex)
{
    // This cache exists to accelerate repeated, small interactive lookups.  A
    // multi-million-object selection is a bulk operation: retaining every
    // source match here duplicates the collection's materialized/edit overlay
    // and keeps that duplicate alive until the Project closes.  Keep the hot
    // interactive working set bounded and stream large requests uncached.
    private const int MaximumRetainedIdCount = 65_536;
    // Includes dictionary/set slots, buckets and result metadata, not just the
    // value struct. Opaque uses scalar addresses and never stores its payload.
    internal const long MaximumRetainedBytes = 8L * 1024 * 1024;
    private const int RetainedIdBytes = 128;
    private readonly object _sync = new();
    private readonly Dictionary<MidoraId, TMatch> _matches = [];
    private readonly HashSet<MidoraId> _absent = [];

    internal int RetainedCount { get { lock (_sync) return _matches.Count + _absent.Count; } }
    internal long RetainedBytes
    {
        get
        {
            lock (_sync)
            {
                // Dictionary entry = hash/next + stable ID + TMatch, aligned
                // to 8 bytes on the fixed x64 target, plus its bucket slot.
                // EnsureCapacity(0) reads allocated capacity without growing it.
                long entry = ((16L + Unsafe.SizeOf<TMatch>() + 7) & ~7L) + sizeof(int);
                return 256L + _matches.EnsureCapacity(0) * entry + _absent.EnsureCapacity(0) * 20L;
            }
        }
    }

    public IReadOnlyList<TMatch> Resolve(
        IReadOnlySet<MidoraId> ids,
        Func<IReadOnlySet<MidoraId>, IEnumerable<TMatch>> query)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(query);
        if (ids.Count == 0) return [];

        bool resolveWithoutRetention;
        lock (_sync)
        {
            long retainedCount = (long)_matches.Count + _absent.Count;
            HashSet<MidoraId>? missing = null;
            resolveWithoutRetention = ids.Count > MaximumRetainedIdCount;
            if (!resolveWithoutRetention)
            {
                missing = ids
                    .Where(id => !_matches.ContainsKey(id) && !_absent.Contains(id))
                    .ToHashSet();
                resolveWithoutRetention = retainedCount + missing.Count
                    > MaximumRetainedIdCount
                    || (retainedCount + missing.Count) * RetainedIdBytes > MaximumRetainedBytes;
            }
            if (!resolveWithoutRetention)
            {
                if (missing!.Count != 0)
                {
                    HashSet<MidoraId> found = [];
                    foreach (TMatch match in query(missing))
                    {
                        MidoraId id = getId(match);
                        if (!missing.Contains(id)) continue;
                        _matches[id] = match;
                        found.Add(id);
                    }
                    missing.ExceptWith(found);
                    _absent.UnionWith(missing);
                }

                return ids
                    .Where(_matches.ContainsKey)
                    .Select(id => _matches[id])
                    .OrderBy(getIndex)
                    .ToArray();
            }
        }

        System.Diagnostics.Debug.Assert(resolveWithoutRetention);
        // Do not perform the source query while holding the shared cache lock.
        // Bulk callers already retain their own compact result or materialize
        // an edit overlay, so caching it again has no useful lifetime benefit.
        return query(ids)
            .Where(match => ids.Contains(getId(match)))
            .OrderBy(getIndex)
            .ToArray();
    }
}
