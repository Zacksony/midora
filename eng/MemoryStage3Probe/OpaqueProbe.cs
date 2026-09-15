using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Midora.Domain;

// Public content-source API is intentionally common to the frozen baseline
// and current implementation. New accounting properties are read optionally.
internal static class OpaqueProbe
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static object Run(int count, int payloadMiB)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(payloadMiB);
        List<object> iterations = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            Collect();
            iterations.Add(RunIteration(count, checked(payloadMiB * 1024 * 1024)));
        }
        return new { mode = "opaque", count, payloadMiB, assembly = typeof(MidiSegment).Assembly.Location, iterations };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object RunIteration(int count, int payloadBytes)
    {
        using var project = new MidoraProject(480);
        var source = new Source(count, payloadBytes);
        var segment = new MidiSegment(project);
        segment.AttachPagedContent(source);
        var query = segment.OpaqueEvents.CreateQuerySnapshot();
        var ids = Enumerable.Range(0, count).Select(source.IdAt).ToHashSet();
        Collect();
        long managedBefore = GC.GetTotalMemory(false);
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        long coldChecksum = Consume(query, ids);
        double coldMs = elapsed.Elapsed.TotalMilliseconds;
        long coldAllocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        Collect();
        long coldManaged = GC.GetTotalMemory(false) - managedBefore;
        long coldAlivePayload = CountAliveBytes(source);
        int coldReads = source.Reads, coldQueries = source.Queries;

        allocatedBefore = GC.GetTotalAllocatedBytes(true);
        elapsed.Restart();
        long hotChecksum = Consume(query, ids);
        double hotMs = elapsed.Elapsed.TotalMilliseconds;
        long hotAllocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        Collect();
        long hotManaged = GC.GetTotalMemory(false) - managedBefore;
        long hotAlivePayload = CountAliveBytes(source);
        if (hotChecksum != coldChecksum) throw new InvalidOperationException("Opaque bytes changed on a hot lookup.");
        object cache = query.GetType().GetField("_sourceIdCache", Flags)!.GetValue(query)!;
        object? retainedBytes = cache.GetType().GetProperty("RetainedBytes", Flags)?.GetValue(cache);
        object? retainedCount = cache.GetType().GetProperty("RetainedCount", Flags)?.GetValue(cache);
        if (retainedCount is null)
            retainedCount = (cache.GetType().GetField("_matches", Flags)?.GetValue(cache) as IDictionary)?.Count;
        GC.KeepAlive(query);
        GC.KeepAlive(segment);
        return new { coldMs, hotMs, coldAllocated, hotAllocated, coldManaged, hotManaged,
            coldAlivePayload, hotAlivePayload, coldReads, hotReads = source.Reads - coldReads,
            coldQueries, hotQueries = source.Queries - coldQueries, retainedBytes, retainedCount,
            checksum = coldChecksum };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Consume(OpaqueMidiEventQuerySnapshot query, IReadOnlySet<MidoraId> ids)
    {
        long checksum = 0;
        foreach (var value in query.ResolveByIds(ids))
            checksum = checked(checksum + value.Payload.Length + value.Payload.Span[0] + value.Payload.Span[^1]);
        return checksum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CountAliveBytes(Source source)
    {
        long result = 0;
        foreach (var weak in source.Payloads)
            if (weak.TryGetTarget(out var array)) result += array.LongLength;
        return result;
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class Source(int count, int bytes) : IPureMidiSegmentContentSource
    {
        public List<WeakReference<byte[]>> Payloads { get; } = [];
        public int Reads { get; private set; }
        public int Queries { get; private set; }
        public int NoteCount => 0;
        public int ChannelEventCount => 0;
        public int OpaqueEventCount => count;
        public string ContentFingerprint => $"opaque-probe-{count}-{bytes}";
        public MidoraId IdAt(int ordinal) => new(ordinal + 100_000);
        public OpaqueMidiEventValue GetOpaqueEvent(int index)
        {
            if ((uint)index >= (uint)count) throw new ArgumentOutOfRangeException(nameof(index));
            byte[] payload = new byte[bytes];
            payload[0] = (byte)index;
            payload[^1] = (byte)(index * 7);
            Reads++;
            Payloads.Add(new(payload));
            return new(IdAt(index), index, OpaqueMidiEventKind.Meta, 1, payload, index);
        }
        public int FindOpaqueEventIndex(MidoraId id) => id.Value >= 100_000 && id.Value < 100_000L + count
            ? (int)(id.Value - 100_000) : -1;
        public IEnumerable<OpaqueMidiEventSourceMatch> QueryOpaqueEventsByIds(IReadOnlySet<MidoraId> ids)
        {
            Queries++;
            foreach (var id in ids)
            {
                int index = FindOpaqueEventIndex(id);
                if (index >= 0) yield return new(index, GetOpaqueEvent(index));
            }
        }
        public IEnumerable<OpaqueMidiEventValue> QueryOpaqueEvents(long startTick, long endTick) =>
            Enumerable.Range(0, count).Where(index => index >= startTick && index < endTick).Select(GetOpaqueEvent);
        public DirectMidiNoteValue GetNote(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public DirectMidiChannelEventValue GetChannelEvent(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public int FindNoteIndex(MidoraId id) => -1;
        public int FindChannelEventIndex(MidoraId id) => -1;
        public IEnumerable<DirectMidiNoteValue> QueryNotes(long startTick, long endTick, int minimumKey = 0, int maximumKey = 127) => [];
        public IEnumerable<DirectMidiChannelEventValue> QueryChannelEvents(long startTick, long endTick) => [];
    }
}
