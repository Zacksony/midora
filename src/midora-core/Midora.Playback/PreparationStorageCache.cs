using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using Midora.Common;

namespace Midora.Playback;

public readonly record struct PreparationStorageSnapshot(
    long BudgetBytes, long CachedBytes, long ActiveBytes, long SharedCacheAndActiveBytes,
    long TotalRetainedBytes, int CachedEntries, int ActiveLeases, long Evictions, long UncachedAdmissions,
    long BaselineBytes, long SharedCacheAndBaselineBytes);

/// <summary>
/// One session-wide LRU for all preparation products. Eviction drops ownership,
/// never disposes or mutates an immutable product borrowed by a consumer.
/// All operations run on preparation/control threads, not audio workers.
/// </summary>
internal sealed class PreparationStorageCache(long budgetBytes, int maximumEntries)
{
    internal const long DefaultBudgetBytes = 128L * 1024 * 1024;
    internal const int DefaultMaximumEntries = 256;
    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private Dictionary<object, Charge> _charges = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<IRetainedStorageSource, Description> _descriptions = new();
    private long _cachedBytes;
    private long _evictions;
    private long _uncachedAdmissions;
    private int _activeLeases;

    internal int ChargeCapacity
    {
        get { lock (_sync) return _charges.EnsureCapacity(0); }
    }

    public PreparationStorageSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                long active = 0, shared = 0, total = 0, baseline = 0, sharedBaseline = 0;
                foreach (Charge charge in _charges.Values)
                {
                    total += charge.Bytes;
                    if (charge.Active != 0) active += charge.Bytes;
                    if (charge.Active != 0 && charge.Cached != 0) shared += charge.Bytes;
                    if (charge.Baseline != 0) baseline += charge.Bytes;
                    if (charge.Baseline != 0 && charge.Cached != 0) sharedBaseline += charge.Bytes;
                }
                return new(budgetBytes, _cachedBytes, active, shared, total,
                    _entries.Count, _activeLeases, _evictions, _uncachedAdmissions, baseline, sharedBaseline);
            }
        }
    }

    public bool TryGet<T>(int family, object key, [NotNullWhen(true)] out T? value) where T : class
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(new(family, key), out LinkedListNode<Entry>? node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                value = (T)node.Value.Value;
                return true;
            }
            value = null;
            return false;
        }
    }

    public void Add(int family, object key, IRetainedStorageSource value)
    {
        Description description = Describe(value);
        lock (_sync)
        {
            CacheKey cacheKey = new(family, key);
            if (_entries.ContainsKey(cacheKey)) return;
            // A legal large product remains usable, but is not pinned by history.
            // Includes descriptor arrays, weak-description/cache dictionaries,
            // per-storage charge objects and hash-table spare capacity. Charging
            // this per product is conservative when several products share it.
            long entryBytes = 256L + description.Parts.Length * 128L;
            long cacheOwnedBytes = description.Parts.Sum(part =>
                _charges.TryGetValue(part.Identity, out Charge? charge) && charge.Baseline != 0 ? 0 : part.Bytes);
            if (cacheOwnedBytes + entryBytes > budgetBytes || maximumEntries == 0)
            {
                _uncachedAdmissions++;
                return;
            }
            Entry entry = new(cacheKey, value, description, entryBytes);
            foreach (RetainedStoragePart part in description.Parts) Reference(part, active: false);
            Reference(new(entry, entryBytes), active: false);
            _entries.Add(cacheKey, _lru.AddLast(entry));
            Trim();
            ReleaseChargeCapacityAtLowWatermark();
        }
    }

    public void Clear(int family)
    {
        lock (_sync)
        {
            LinkedListNode<Entry>? node = _lru.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (node.Value.Key.Family == family) Remove(node);
                node = next;
            }
            ReleaseChargeCapacityAtLowWatermark();
        }
    }

    public IDisposable Retain(IRetainedStorageSource value, bool baseline = false)
    {
        Description description = Describe(value);
        lock (_sync)
        {
            foreach (RetainedStoragePart part in description.Parts) Reference(part, active: true, baseline);
            if (!baseline) _activeLeases++;
            return new Lease(this, description, baseline);
        }
    }

    private Description Describe(IRetainedStorageSource value) => _descriptions.GetValue(value, static source =>
    {
        RetainedStorageCollector collector = new();
        source.CollectRetainedStorage(collector);
        RetainedStoragePart[] parts = collector.ToArray();
        return new(parts, parts.Sum(part => part.Bytes));
    });

    private void Remove(LinkedListNode<Entry> node)
    {
        Entry entry = node.Value;
        _lru.Remove(node);
        _entries.Remove(entry.Key);
        foreach (RetainedStoragePart part in entry.Description.Parts) Dereference(part.Identity, active: false);
        Dereference(entry, active: false);
    }

    private void Reference(RetainedStoragePart part, bool active, bool baseline = false)
    {
        if (!_charges.TryGetValue(part.Identity, out Charge? charge))
        {
            charge = new(part.Bytes);
            _charges.Add(part.Identity, charge);
        }
        long previous = charge.CacheOwnedBytes;
        if (baseline) charge.Baseline++;
        else if (active) charge.Active++;
        else charge.Cached++;
        _cachedBytes += charge.CacheOwnedBytes - previous;
    }

    private void Dereference(object identity, bool active, bool baseline = false)
    {
        Charge charge = _charges[identity];
        long previous = charge.CacheOwnedBytes;
        if (baseline) charge.Baseline--;
        else if (active) charge.Active--;
        else charge.Cached--;
        _cachedBytes += charge.CacheOwnedBytes - previous;
        if (charge.Active == 0 && charge.Cached == 0 && charge.Baseline == 0) _charges.Remove(identity);
    }

    private void Release(Description description, bool baseline)
    {
        lock (_sync)
        {
            foreach (RetainedStoragePart part in description.Parts) Dereference(part.Identity, active: true, baseline);
            if (!baseline) _activeLeases--;
            // Old current-canonical storage can become cache-owned when a new
            // compile is published. Enforce the budget at that ownership change.
            if (baseline) Trim();
            ReleaseChargeCapacityAtLowWatermark();
        }
    }

    private void ReleaseChargeCapacityAtLowWatermark()
    {
        // Remove clears references, but Dictionary retains its backing arrays.
        // Reclaim their high-water capacity only once after a control-thread
        // batch, never per storage part or from a consumer's audio callback.
        int capacity = _charges.EnsureCapacity(0);
        if (_charges.Count == 0)
        {
            if (capacity != 0) _charges = new(ReferenceEqualityComparer.Instance);
        }
        else if (capacity >= 1024 && _charges.Count <= capacity / 4)
        {
            // Fourfold hysteresis keeps ordinary lease replacement and LRU
            // churn from repeatedly rebuilding a normally occupied table.
            _charges.TrimExcess();
        }
    }

    private void Trim()
    {
        while ((_cachedBytes > budgetBytes || _entries.Count > maximumEntries) && _lru.First is { } oldest)
        {
            Remove(oldest);
            _evictions++;
        }
    }

    private readonly record struct CacheKey(int Family, object Key);
    private sealed record Description(RetainedStoragePart[] Parts, long TotalBytes);
    private sealed record Entry(CacheKey Key, IRetainedStorageSource Value, Description Description, long Bytes);
    private sealed class Charge(long bytes)
    {
        public long Bytes { get; } = bytes;
        public int Cached;
        public int Active;
        public int Baseline;
        public long CacheOwnedBytes => Cached != 0 && Baseline == 0 ? Bytes : 0;
    }
    private sealed class Lease(PreparationStorageCache owner, Description description, bool baseline) : IDisposable
    {
        private PreparationStorageCache? _owner = owner;
        private Description? _description = description;
        public void Dispose()
        {
            PreparationStorageCache? cache = Interlocked.Exchange(ref _owner, null);
            if (cache is not null) cache.Release(Interlocked.Exchange(ref _description, null)!, baseline);
        }
    }
}

internal sealed class PreparationStorageMap<TKey, TValue>(PreparationStorageCache cache, int family)
    where TKey : notnull where TValue : class, IRetainedStorageSource
{
    public bool TryGetValue(TKey key, [NotNullWhen(true)] out TValue? value) => cache.TryGet(family, key, out value);
    public void Add(TKey key, TValue value) => cache.Add(family, key, value);
    public void TryAdd(TKey key, TValue value) => cache.Add(family, key, value);
    public void Clear() => cache.Clear(family);
}
