using Midora.Common;
using Midora.Midi;

namespace Midora.Compiler;

// Compiler-owned, content-keyed acceleration for the uncommon raw Note stream.
// Stores counts BEFORE a tick, never source objects, decoded pages or file handles.
internal sealed class PureMidiEndpointPrefixCache
{
    internal const int MaximumCheckpoints = 4096;
    private const int MaximumEntryCheckpoints = 1024;
    private const int CheckpointStride = 16384;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _clock;
    internal int CheckpointCount => _entries.Values.Sum(value => value.Checkpoints.Length);

    public long[] GetCounts(string key, long start, long end,
        Func<long, IEnumerable<CanonicalMidiRenderEventPage>> read, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _entries.TryGetValue(key, out Entry? entry);
        Checkpoint[] prior = entry?.Checkpoints ?? [];
        int low = 0, high = prior.Length;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (prior[middle].Tick <= end) low = middle + 1; else high = middle;
        }
        Checkpoint? checkpoint = low == 0 ? null : prior[low - 1];
        long from = checkpoint?.Tick ?? start;
        long[] counts = checkpoint is null ? new long[128] : (long[])checkpoint.Counts.Clone();
        List<Checkpoint> added = [];
        long previousTick = -1;
        int scanned = 0;
        foreach (CanonicalMidiRenderEventPage page in read(from))
            foreach (CanonicalMidiRenderEvent value in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                if (value.Tick != previousTick && (scanned >= CheckpointStride || value.Tick == end))
                {
                    if (added.Count == MaximumEntryCheckpoints) added.RemoveAt(0);
                    added.Add(new(value.Tick, (long[])counts.Clone()));
                    scanned = 0;
                }
                previousTick = value.Tick;
                byte note = value.Message.Byte1;
                if (value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 != 0) counts[note]++;
                else counts[note] = Math.Max(0, counts[note] - 1);
                scanned++;
            }
        // With no event exactly at end, the final state is also the next prefix.
        if (previousTick != end) added.Add(new(end, (long[])counts.Clone()));
        ct.ThrowIfCancellationRequested();
        Checkpoint[] published = prior.Concat(added).GroupBy(value => value.Tick).Select(value => value.Last())
            .OrderBy(value => value.Tick).TakeLast(MaximumEntryCheckpoints).ToArray();
        _entries[key] = new(published, ++_clock);
        while (CheckpointCount > MaximumCheckpoints)
            _entries.Remove(_entries.MinBy(value => value.Value.LastUse).Key);
        return counts;
    }

    public void Clear() => _entries.Clear();

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 48)) return;
        collector.Dictionary(_entries);
        foreach ((string key, Entry entry) in _entries)
        {
            collector.Text(key); collector.Add(entry, 40); collector.Array(entry.Checkpoints);
            foreach (Checkpoint checkpoint in entry.Checkpoints)
            { collector.Add(checkpoint, 40); collector.Array(checkpoint.Counts); }
        }
    }

    private sealed record Checkpoint(long Tick, long[] Counts);
    private sealed record Entry(Checkpoint[] Checkpoints, long LastUse);
}
