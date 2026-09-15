using Midora.Domain;
using Midora.Common;
using System.Collections.Concurrent;

namespace Midora.Compiler;

public sealed partial class CanonicalCompiledResult
{
    public T GetOrCreateConsumerMetadata<T>(Func<T> create) where T : class, IRetainedStorageSource =>
        _consumerCacheIdentity.GetOrCreate(create);

    public IEnumerable<CanonicalMidiRenderEventPage> QueryPureMidiRenderEventPages(
        long startTick, long endTick, bool includeStateAtStart = false,
        CancellationToken cancellationToken = default)
    {
        if (startTick < StartTick || endTick > EndTick || endTick < startTick)
            throw new ArgumentOutOfRangeException(nameof(startTick));
        return _pagedRenderSource?.QueryRenderPages(startTick, endTick, includeStateAtStart, cancellationToken) ?? [];
    }

    public IEnumerable<CanonicalMidiEvent> EnumerateResidentAndLogicalEvents(
        CancellationToken cancellationToken = default) =>
        EnumerateResidentAndLogicalEvents(StartTick, EndTick, true, cancellationToken);

    public IEnumerable<CanonicalMidiEvent> EnumerateResidentAndLogicalEvents(
        long startTick, long endTick, bool includeEnd,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<CanonicalMidiEvent> memory = _events.Where(value => value.Tick >= startTick
            && (value.Tick < endTick || includeEnd && value.Tick == endTick));
        foreach (CanonicalMidiEvent value in Merge(memory,
            _logicalEventSource?.Enumerate(startTick, endTick, includeEnd, cancellationToken) ?? [], Compare))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    public IEnumerable<CanonicalMidiEvent> EnumerateLogicalEvents(
        long startTick, long endTick, bool includeEnd,
        CancellationToken cancellationToken = default) =>
        EnumerateResidentAndLogicalEvents(startTick, endTick, includeEnd, cancellationToken)
            .Where(value => value.Source.PureMidiTrackId == default && value.Source.MidiChannelRootId == default);

    public IEnumerable<CanonicalMidiEvent> EnumerateResidentAndLogicalUnitEvents(
        byte zeroBasedPort, byte zeroBasedChannel, CancellationToken cancellationToken = default) =>
        Merge(_events.Where(value => value.ZeroBasedPort == zeroBasedPort && value.ZeroBasedChannel == zeroBasedChannel),
            _logicalEventSource?.EnumerateUnit(zeroBasedPort, zeroBasedChannel, StartTick, EndTick,
                includeEnd: true, cancellationToken) ?? [], Compare);

    private IEnumerable<CanonicalMidiRenderEventPage> MergePagedRenderEvents(
        long startTick, long endTick, bool includeStateAtStart,
        IReadOnlySet<MidoraId>? demandedSources, CancellationToken cancellationToken)
    {
        IEnumerable<CanonicalMidiRenderEventPage> pure = demandedSources is not null
            && _pagedRenderSource is ICanonicalDemandFilteredMidiRenderPageSource filtered
                ? filtered.QueryRenderPages(startTick, endTick, includeStateAtStart, demandedSources, cancellationToken)
                : _pagedRenderSource?.QueryRenderPages(startTick, endTick, includeStateAtStart, cancellationToken) ?? [];
        IEnumerable<CanonicalMidiRenderEvent> logical =
            (_logicalEventSource?.Enumerate(startTick, endTick, endTick == EndTick, cancellationToken) ?? [])
            .Where(value => demandedSources is null || demandedSources.Contains(value.Source.TrackId))
            .Select(value => new CanonicalMidiRenderEvent(value.Tick, value.ZeroBasedPort, value.Message,
                value.Source.TrackId, value.Source.TrackId, value.Role, value.StableOrder,
                value.SmfTrackOrder, value.SmfEventOrder, value.Source.DirectMidiObjectId));
        List<CanonicalMidiRenderEvent> page = new(CanonicalMidiRenderEventPage.MaximumRecordCount);
        foreach (CanonicalMidiRenderEvent value in Merge(logical, pure.SelectMany(p => p.Items), CompareRender))
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Add(value);
            if (page.Count != CanonicalMidiRenderEventPage.MaximumRecordCount) continue;
            yield return new(page.ToArray()); page.Clear();
        }
        if (page.Count != 0) yield return new(page.ToArray());
    }

    private static int CompareRender(CanonicalMidiRenderEvent x, CanonicalMidiRenderEvent y)
    {
        int c = x.Tick.CompareTo(y.Tick); if (c != 0) return c;
        c = x.Role.CompareTo(y.Role); if (c != 0) return c;
        c = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort); if (c != 0) return c;
        c = x.Message.ChannelNumber.CompareTo(y.Message.ChannelNumber); if (c != 0) return c;
        c = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder); if (c != 0) return c;
        if (x.Role == CanonicalEventRole.DirectMidi && y.Role == CanonicalEventRole.DirectMidi)
        { c = x.SmfEventOrder.CompareTo(y.SmfEventOrder); if (c != 0) return c; }
        c = x.StableOrder.CompareTo(y.StableOrder); if (c != 0) return c;
        c = x.TrackId.CompareTo(y.TrackId); if (c != 0) return c;
        c = x.StableObjectId.CompareTo(y.StableObjectId); if (c != 0) return c;
        return x.Message.PackedValue.CompareTo(y.Message.PackedValue);
    }
}

internal sealed class CanonicalConsumerMetadataCache : IRetainedStorageSource
{
    private readonly ConcurrentDictionary<Type, Lazy<IRetainedStorageSource>> _values = new();

    public T GetOrCreate<T>(Func<T> create) where T : class, IRetainedStorageSource
    {
        Lazy<IRetainedStorageSource> value = _values.GetOrAdd(typeof(T),
            _ => new Lazy<IRetainedStorageSource>(() => create(), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return (T)value.Value; }
        catch
        {
            _values.TryRemove(new KeyValuePair<Type, Lazy<IRetainedStorageSource>>(typeof(T), value));
            throw;
        }
    }

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 128 + _values.Count * 128L)) return;
        foreach (Lazy<IRetainedStorageSource> value in _values.Values)
            if (value.IsValueCreated) value.Value.CollectRetainedStorage(collector);
    }
}
