using System.Collections.Immutable;

namespace Midora.Domain;

internal sealed partial class PagedTimelineObjectList<T, TValue> where T : class
{
    private void PromoteOrdinaryStoreIfNeeded()
    {
        if (_shared is not null || _materialize is null || _count < DefaultPageCapacity
            || _duplicateReferenceCounts?.Count > 0) return;

        // The legacy mutable store is bounded to the initial page. Preserve
        // externally held object identities, but do not retain a second complete
        // object graph beside the immutable values used by snapshots/consumers.
        PublishPendingValueChanges();
        _publishedSequence = null;
        EnsurePublishedState();
        var ordinals = new OrdinalDirectory(_publishedSequence!, _getId);
        ordinals.Prepare();
        _sharedOrdinalDirectory = ordinals;
        _publishedBaseById = new SourceValueDictionary(_publishedSequence!, ordinals, _getId);
        _publishedIdDelta = PersistentTimelineIdDeltaMap<TValue>.Empty;
        _shared = new(this, _materialize);
        int ordinal = 0;
        foreach (Page page in _pages)
            foreach (T item in page.Items) _shared.RegisterExisting(item, ordinal++);
        _pages.Clear();
        _livePages.Clear();
        _entries.Clear();
        _entries.TrimExcess();
        _dirtyPages.Clear();
        _pageIndices.Clear();
        _pageStarts = [0];
        _pageDirectoryDirty = false;
        _publishedSnapshot = null;
    }

    private void AppendPublishedValues(IReadOnlyList<TValue> values)
    {
        var mutation = _publishedSequence!.InsertRange(_publishedSequence.Count, values);
        if (!_publishedIdDelta.IsEmpty || !_spatialBaseDelta.IsEmpty || !_spatialValueOverlay.IsEmpty
            || _sharedOrdinalDirectory is not OrdinalDirectory ordinals
            || !ordinals.TryAppend(values, _publishedSequence.Count, out var nextOrdinals))
        {
            ApplyPublishedMutation(mutation);
            return;
        }

        // Ordinary construction is a value-only append. Extend compact address
        // and spatial trees with the new leaves; do not store every appended
        // value again in the generic COW ID delta or rebuild the old ID index.
        List<PagedTimelineValuePage<TValue>> removed = new(mutation.RemovedLeaves.Count);
        foreach (var leaf in mutation.RemovedLeaves)
        {
            var page = _publishedLeafPages[leaf];
            _publishedLeafPages = _publishedLeafPages.Remove(leaf);
            removed.Add(page);
            RemoveDiscoveryKeys(page);
        }
        List<PagedTimelineValuePage<TValue>> added = new(mutation.AddedLeaves.Count);
        foreach (var leaf in mutation.AddedLeaves)
        {
            var page = CreatePublishedPage(leaf);
            _publishedLeafPages = _publishedLeafPages.Add(leaf, page);
            added.Add(page);
            AddDiscoveryKeys(page);
        }
        _spatialIndex = _spatialIndex.ReplacePages(removed, added);
        _publishedSequence = mutation.Sequence;
        _sharedOrdinalDirectory = nextOrdinals;
        _publishedBaseById = new SourceValueDictionary(mutation.Sequence, nextOrdinals, _getId);
        _publishedSnapshot = null;
    }
}
