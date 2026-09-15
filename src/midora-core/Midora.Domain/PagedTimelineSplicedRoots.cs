namespace Midora.Domain;

internal sealed partial class PagedTimelineObjectList<T, TValue> where T : class
{
    // Prefix metadata is 4 bytes per 128 splices (about 3 MiB even at 100M
    // records). Never scan a whole 4096-record disk page for each ID lookup.
    private const int SplicePrefixStride = 128;
    internal void AdoptSplicedSnapshot(PagedTimelineValueSnapshot<TValue> snapshot,
        IImmutableTimelineValueSource<TimelineValueSplice> splices,
        IIndexedImmutableTimelineValueSource<TValue> values, Func<TValue, T> materialize,
        CancellationToken token)
    {
        if (_count != 0 || _batchDepth != 0) throw new InvalidOperationException("Only an empty collection can adopt a spliced root.");
        if (snapshot.EditableRoot is not EditableRoot source) throw new InvalidOperationException("The timeline snapshot has no editable root.");
        if (splices.PageCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(splices));
        List<PersistentTimelineSequence<TValue>.Leaf>? leaves = null;
        var pages = source.LeafPages.ToBuilder();
        HashSet<PagedTimelineValuePage<TValue>> removed = [];
        List<PagedTimelineValuePage<TValue>> added = [];
        var discovery = source.DiscoveryCounts.ToBuilder();
        int[] deltas = new int[(splices.Count + SplicePrefixStride - 1) / SplicePrefixStride + 1];
        int index = 0, nextValue = 0, delta = 0;
        IImmutableTimelineValueSource<TValue>? pendingInput = null;
        int pendingFirst = 0, pendingCount = 0;
        var sequence = source.Sequence.TransformLeafRuns((start, leaf) =>
        {
            token.ThrowIfCancellationRequested();
            bool touched = index < splices.Count && splices[index].Ordinal < start + leaf.Count;
            if (!touched && leaf.Replacements.Count == 0)
            {
                return null;
            }
            leaves = [];
            var oldPage = pages[leaf];
            pages.Remove(leaf); removed.Add(oldPage);
            foreach (var (key, occurrences) in oldPage.DiscoveryCounts)
            {
                int count = discovery[key];
                if (count == occurrences) discovery.Remove(key); else discovery[key] = checked(count - occurrences);
            }
            var original = new LeafValueSource(leaf);
            int local = 0;
            while (local < leaf.Count)
            {
                token.ThrowIfCancellationRequested();
                if (index < splices.Count && splices[index].Ordinal < start + local)
                    throw new InvalidOperationException("Splices must be unique and sorted by source ordinal.");
                if (index < splices.Count && splices[index].Ordinal == start + local)
                {
                    var splice = splices[index];
                    if (splice.Count < 0 || splice.FirstValue != nextValue || splice.Count > values.Count - nextValue)
                        throw new InvalidOperationException("Splice value ranges must be contiguous and within their immutable source.");
                    if (index % SplicePrefixStride == 0) deltas[index / SplicePrefixStride] = delta;
                    AddSlices(values, nextValue, splice.Count);
                    nextValue = checked(nextValue + splice.Count);
                    delta = checked(delta + splice.Count - 1);
                    index++; local++;
                }
                else
                {
                    int end = index < splices.Count ? Math.Min(leaf.Count, splices[index].Ordinal - start) : leaf.Count;
                    AddSlices(original, local, end - local);
                    local = end;
                }
            }
            FlushSlice();
            return leaves;
        }, token);
        if (index != splices.Count || nextValue != values.Count)
            throw new InvalidOperationException("The splice source contains an unmatched range or source ordinal.");
        deltas[^1] = delta;
        var spatial = source.SpatialIndex.ReplacePages(removed.ToArray(), added);
        ITimelineOrdinalLookup ordinals;
        IDisposable? createdAddress = null;
        if (source.Ordinals.StructuralDepth >= 8 && values is IImmutableTimelineOrdinalIndexBuilder builder)
        {
            var indexSource = builder.CreateOrdinalIndex(sequence.Enumerate().Select((v, i) => new TimelineIdOrdinal(_getId(v), i)), token);
            createdAddress = indexSource as IDisposable;
            ordinals = new ExternalOrdinals(indexSource);
        }
        else ordinals = new SplicedOrdinals(source.Ordinals, splices, new IndexedSourceOrdinals(values), deltas);
        try { token.ThrowIfCancellationRequested(); }
        catch { createdAddress?.Dispose(); throw; }
        _publishedSequence = sequence;
        _publishedLeafPages = pages.ToImmutable();
        _publishedBaseById = new SourceValueDictionary(sequence, ordinals, _getId);
        _spatialIndex = spatial;
        _discoveryKeyPageCounts = discovery.ToImmutable();
        _sharedOrdinalDirectory = ordinals;
        _count = sequence.Count;
        _generation = checked(snapshot.Generation + 1);
        _publishedSnapshot = null;
        _shared = new(this, materialize);

        void AddSlices(IImmutableTimelineValueSource<TValue> input, int first, int count)
        {
            while (count > 0)
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(pendingInput, input) || pendingFirst + pendingCount != first) FlushSlice();
                if (pendingInput is null) { pendingInput = input; pendingFirst = first; }
                int take = Math.Min(PersistentTimelineSequence<TValue>.LeafCapacity - pendingCount, count);
                pendingCount += take;
                first += take; count -= take;
                if (pendingCount == PersistentTimelineSequence<TValue>.LeafCapacity) FlushSlice();
            }
        }
        void FlushSlice()
        {
            if (pendingInput is null) return;
            // Keep addresses only for this fragment, not providers referenced
            // by replaced values elsewhere in its original leaf.
            IImmutableTimelineValueSource<TValue> input = pendingInput is LeafValueSource original
                ? original.Slice(pendingFirst, pendingCount) : pendingInput;
            var leaf = new PersistentTimelineSequence<TValue>.Leaf(
                new TimelineValueBuffer<TValue>(input, ReferenceEquals(input, pendingInput) ? pendingFirst : 0, pendingCount), _getFingerprint);
            var page = CreatePublishedPage(leaf);
            leaves!.Add(leaf); pages.Add(leaf, page); added.Add(page);
            foreach (var (key, occurrences) in page.DiscoveryCounts)
                discovery[key] = discovery.TryGetValue(key, out int prior) ? checked(prior + occurrences) : occurrences;
            pendingInput = null; pendingCount = 0;
        }
    }

    private sealed class SplicedOrdinals(ITimelineOrdinalLookup original,
        IImmutableTimelineValueSource<TimelineValueSplice> splices,
        ITimelineOrdinalLookup values, int[] deltas) : ITimelineOrdinalLookup
    {
        public int StructuralDepth => original.StructuralDepth + 1;
        public void Prepare(CancellationToken token = default) => original.Prepare(token);
        public void Prepare(IImmutableTimelineOrdinalIndexBuilder builder, CancellationToken token = default) => original.Prepare(builder, token);
        public int Find(MidoraId id)
        {
            int valueOrdinal = values.Find(id);
            if (valueOrdinal >= 0)
            {
                int low = 0, high = splices.Count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    var splice = splices[middle];
                    if (splice.FirstValue + splice.Count <= valueOrdinal) low = middle + 1; else high = middle;
                }
                if (low >= splices.Count) return -1;
                var found = splices[low];
                return checked(found.Ordinal + DeltaBefore(low) + valueOrdinal - found.FirstValue);
            }
            int ordinal = original.Find(id);
            if (ordinal < 0) return -1;
            int left = 0, right = splices.Count;
            while (left < right)
            {
                int middle = left + (right - left) / 2;
                if (splices[middle].Ordinal < ordinal) left = middle + 1; else right = middle;
            }
            if (left < splices.Count && splices[left].Ordinal == ordinal) return -1;
            return checked(ordinal + DeltaBefore(left));
        }
        private int DeltaBefore(int index)
        {
            int page = index / SplicePrefixStride, delta = deltas[page];
            for (int i = page * SplicePrefixStride; i < index; i++) delta = checked(delta + splices[i].Count - 1);
            return delta;
        }
    }
}
