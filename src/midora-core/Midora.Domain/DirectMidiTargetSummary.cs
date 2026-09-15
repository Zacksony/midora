using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Midora.Domain;

public readonly record struct DirectMidiLaneKey(DirectMidiChannelEventKind Kind, int Number)
{
    public static DirectMidiLaneKey From(DirectMidiChannelEventValue value) => new(value.Kind,
        value.Kind is DirectMidiChannelEventKind.ControlChange or DirectMidiChannelEventKind.PolyphonicKeyPressure
            or DirectMidiChannelEventKind.NoteOn or DirectMidiChannelEventKind.NoteOff ? value.Data1 : 0);
}

public interface IDirectMidiTargetSummarySource
{
    IReadOnlyDictionary<DirectMidiLaneKey, int> GetTargetCounts(CancellationToken cancellationToken);
}

/// <summary>Only target counters are retained. Missing legacy summaries are scanned
/// once per immutable source, never into a second array of event records.</summary>
internal static class DirectMidiTargetSummary
{
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1);
        internal IReadOnlyDictionary<DirectMidiLaneKey, int>? Counts;
    }
    private static readonly ConditionalWeakTable<IPureMidiSegmentContentSource, Entry> Cache = new();

    internal static IReadOnlyDictionary<DirectMidiLaneKey, int> Read(
        IPureMidiSegmentContentSource source, CancellationToken token)
    {
        if (source is IDirectMidiTargetSummarySource summary) return summary.GetTargetCounts(token);
        var entry = Cache.GetOrCreateValue(source);
        entry.Gate.Wait(token);
        try
        {
            if (entry.Counts is not null) return entry.Counts;
            var counts = ImmutableDictionary.CreateBuilder<DirectMidiLaneKey, int>();
            foreach (var value in source.QueryChannelEvents(0, long.MaxValue))
            {
                token.ThrowIfCancellationRequested();
                Add(counts, DirectMidiLaneKey.From(value), 1);
            }
            token.ThrowIfCancellationRequested();
            return entry.Counts = counts.ToImmutable();
        }
        finally { entry.Gate.Release(); }
    }

    internal static void Add(ImmutableDictionary<DirectMidiLaneKey, int>.Builder counts, DirectMidiLaneKey key, int delta)
    {
        int result = checked(counts.GetValueOrDefault(key) + delta);
        if (result == 0) counts.Remove(key); else counts[key] = result;
    }
}
