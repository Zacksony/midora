using System.Runtime.InteropServices;
using Midora.Domain;

namespace Midora.Application;

/// <summary>A small, process-wide cache of immutable payload byte arrays. Keys do
/// not retain an old Project/root; both byte and entry counts have hard limits.</summary>
internal static class BoundedOpaquePayloadCache
{
    internal const long MaximumBytes = 8L * 1024 * 1024;
    internal const int MaximumEntries = 8192;
    private static readonly object Sync = new();
    private static readonly Dictionary<(long Source, int Ordinal), LinkedListNode<Entry>> Entries = [];
    private static readonly LinkedList<Entry> Recent = [];
    private static long _identity, _bytes;
    internal static (int Entries, long RetainedBytes) Snapshot
    { get { lock (Sync) return (Entries.Count, RetainedBytesUnderLock()); } }
    // x64 dictionary entry (32) + bucket (4), exact allocated capacity, plus
    // conservative object/array headers. Entry (64) and linked-list node (48)
    // storage is charged with each payload below. Empty buckets remain charged
    // after eviction rather than disappearing from the reported budget.
    private static long RetainedBytesUnderLock() => 512L + Entries.EnsureCapacity(0) * 36L + _bytes;
    public static long AllocateIdentity() => Interlocked.Increment(ref _identity);
    public static bool TryGet(long source, int ordinal, out ReadOnlyMemory<byte> bytes)
    {
        lock (Sync)
        {
            if (Entries.TryGetValue((source, ordinal), out var entry))
            { Recent.Remove(entry); Recent.AddFirst(entry); bytes = entry.Value.Bytes; return true; }
        }
        bytes = default; return false;
    }
    public static void Add(long source, int ordinal, ReadOnlyMemory<byte> bytes)
    {
        // Formal sources and our decoded PayloadBank return immutable arrays.
        // Share those bytes instead of allocating another full payload copy.
        // Unknown memory owners are normalized to an exact-size array so the
        // cache cannot silently pin a larger, unaccountable memory manager.
        long retainedBytes = OpaqueMidiPayloadMemory.GetRetainedAllocatedBytes(bytes) + 112L;
        if (retainedBytes + 512L > MaximumBytes) return;
        lock (Sync)
        {
            if (Entries.ContainsKey((source, ordinal))) return;
            Entries.EnsureCapacity(Math.Min(MaximumEntries, Entries.Count + 1));
            while (Entries.Count >= MaximumEntries || RetainedBytesUnderLock() + retainedBytes > MaximumBytes)
            {
                if (Entries.Count == 0) return;
                var last = Recent.Last!; Recent.RemoveLast(); Entries.Remove((last.Value.Source, last.Value.Ordinal));
                _bytes -= last.Value.RetainedBytes;
            }
            ReadOnlyMemory<byte> immutable = MemoryMarshal.TryGetArray(bytes, out _) ? bytes : bytes.ToArray();
            var entry = Recent.AddFirst(new Entry(source, ordinal, immutable, retainedBytes));
            Entries.Add((source, ordinal), entry); _bytes += retainedBytes;
        }
    }
    private sealed record Entry(long Source, int Ordinal, ReadOnlyMemory<byte> Bytes, long RetainedBytes);
}
