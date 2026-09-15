using System.Collections;
using Midora.Common;
using Midora.Domain;
using Midora.Midi;

namespace Midora.Compiler;

// All fields are recoverable. Neither intern ordinal nor
// physical page order participates in musical ordering or fingerprints.
internal readonly record struct CompactCanonicalEvent(
    long Tick, long StableOrder, long SemanticTargetKey, long SemanticGroup,
    long SourceTick, MidoraId LogicalNoteId, MidoraId SourceEventId,
    MidoraId ExportTrackId, long SmfEventOrder, long SourceIndex,
    int SmfTrackOrder, MidiMessage Message, byte ZeroBasedPort, byte ZeroBasedChannel,
    CanonicalEventRole Role)
{
    public CompactCanonicalSource Source => new(SourceIndex, LogicalNoteId, SourceEventId, SourceTick);
}

internal readonly record struct CompactCanonicalSource(
    long Index, MidoraId NoteId, MidoraId EventId, long Tick);

/// <summary>A bounded discovery index over independently owned immutable source pages.</summary>
internal sealed class CanonicalSourceTable : IDisposable, IRetainedStorageSource
{
    internal const int InternLimit = 4096;
    // Dictionary growth past 4,049 entries may hold both 4,049 and 8,419 slots.
    // SourceReference + hash/next/value is 176 bytes per entry, plus both bucket
    // arrays. Reserve conservative capacity slack, not just 4,096 live entries.
    internal const long InternWorkingBytes = InternLimit * 768L;
    private readonly CompilerValueStore<SourceReference> _values;
    private readonly CanonicalSourceTable? _parent;
    private readonly long _parentCount;
    private Dictionary<SourceReference, long>? _intern;
    private IDisposable? _internLease;
    private bool _sealed, _disposed;

    public CanonicalSourceTable(CompilerStorageBudget budget, CancellationToken token,
        CanonicalSourceTable? parent = null)
    {
        if (parent is not null)
        {
            ObjectDisposedException.ThrowIf(parent._disposed, parent);
            if (!parent._sealed)
                throw new InvalidOperationException("A source overlay requires an immutable parent.");
        }
        _parent = parent;
        _parentCount = parent?.Count ?? 0;
        _internLease = budget.ReserveWorking(InternWorkingBytes);
        try
        {
            _intern = [];
            _values = new(budget, token);
        }
        catch
        {
            _internLease.Dispose();
            _internLease = null;
            throw;
        }
    }
    public long Count => checked(_parentCount + _values.Count);
    public long ResidentBytes => _values.ResidentBytes;
    public long SpillBytes => _values.SpillBytes;
    public CompactCanonicalSource Capture(in SourceReference source) => new(
        Intern(source with { Tick = 0, LogicalNoteId = default, SourceEventId = default }),
        source.LogicalNoteId, source.SourceEventId, source.Tick);
    public long Intern(in SourceReference pattern)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) throw new InvalidOperationException("The source table is sealed.");
        if (_intern!.TryGetValue(pattern, out long index)) return index;
        index = Count;
        _values.Add(pattern);
        if (_intern.Count < InternLimit) _intern.Add(pattern, index);
        return index;
    }
    public CompactCanonicalEvent Compact(in CanonicalMidiEvent value)
    {
        CompactCanonicalSource source = Capture(value.Source);
        return new(value.Tick, value.StableOrder, value.SemanticTargetKey, value.SemanticGroup,
            source.Tick, source.NoteId, source.EventId, value.ExportTrackId, value.SmfEventOrder,
            source.Index, value.SmfTrackOrder, value.Message, value.ZeroBasedPort,
            value.ZeroBasedChannel, value.Role);
    }
    public void Seal()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed) return;
        _values.Seal();
        _intern = null;
        _internLease?.Dispose();
        _internLease = null;
        _sealed = true;
    }
    public Reader OpenReader(CancellationToken token = default) => new(this, token);
    public void Warm(CancellationToken token)
    {
        foreach (SourceReference ignored in _values.EnumerateForPublication(token)) { }
        _values.ReleaseFullyResidentBacking();
    }
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 80)) return;
        _values.CollectRetainedStorage(collector);
        _parent?.CollectRetainedStorage(collector);
        if (_intern is not null) collector.Dictionary(_intern);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _intern = null;
        _internLease?.Dispose(); _internLease = null;
        // A finalizable object can be finalized even if its constructor failed.
        _values?.Dispose();
        GC.SuppressFinalize(this);
    }
    ~CanonicalSourceTable() { try { Dispose(); } catch { } }

    internal sealed class Reader : IDisposable
    {
        // The table finalizer owns disposal of its pages. Holding only the value
        // store would allow the table to be finalized while this reader is live.
        private readonly CanonicalSourceTable _owner;
        private readonly long _parentCount;
        private readonly CompilerValueStore<SourceReference>.RandomReader _values;
        private readonly Reader? _parent;
        public Reader(CanonicalSourceTable owner, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);
            _owner = owner;
            _parentCount = owner._parentCount;
            _values = owner._values.OpenRandomReader(token);
            try { _parent = owner._parent?.OpenReader(token); }
            catch { _values.Dispose(); throw; }
        }
        public SourceReference Pattern(long index)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            SourceReference value = index < _parentCount
                ? _parent!.Pattern(index) : _values.Read(index - _parentCount);
            GC.KeepAlive(_owner);
            return value;
        }
        public SourceReference Restore(in CompactCanonicalSource value) => Pattern(value.Index) with
        { LogicalNoteId = value.NoteId, SourceEventId = value.EventId, Tick = value.Tick };
        public CanonicalMidiEvent Restore(in CompactCanonicalEvent value) => new(value.Tick,
            value.ZeroBasedPort, value.ZeroBasedChannel, value.Message, value.Role, value.StableOrder,
            value.SemanticTargetKey, value.SemanticGroup, Restore(value.Source), value.ExportTrackId,
            value.SmfTrackOrder, value.SmfEventOrder);
        public void Dispose()
        {
            try { _values.Dispose(); }
            finally { _parent?.Dispose(); }
        }
    }
}

internal sealed class CompactCanonicalStore : IEnumerable<CanonicalMidiEvent>, IDisposable, IRetainedStorageSource
{
    internal readonly CompilerValueStore<CompactCanonicalEvent> Values;
    internal readonly CanonicalSourceTable Sources;
    public CompactCanonicalStore(CompilerStorageBudget budget, CancellationToken token = default)
    {
        Values = new(budget, token);
        try { Sources = new(budget, token); }
        catch { Values.Dispose(); throw; }
    }
    public long Count => Values.Count;
    public int PageCount => Values.PageCount;
    public long ResidentBytes => Values.ResidentBytes + Sources.ResidentBytes;
    public long SpillBytes => Values.SpillBytes + Sources.SpillBytes;
    public void Add(CanonicalMidiEvent value) => Values.Add(Sources.Compact(value));
    public void AddCompact(CompactCanonicalEvent value) => Values.Add(value);
    public void Seal() { Values.Seal(); Sources.Seal(); }
    public void ReserveOwnedMetadata(long bytes) => Values.ReserveOwnedMetadata(bytes);
    public void ReleaseOwnedMetadata(long bytes) => Values.ReleaseOwnedMetadata(bytes);
    public void ReleaseFullyResidentBacking() => Values.ReleaseFullyResidentBacking();
    public IEnumerable<CanonicalMidiEvent> Enumerate(bool reverse = false, CancellationToken cancellationToken = default) =>
        EnumerateRange(0, Count, reverse, cancellationToken);
    public IEnumerable<CanonicalMidiEvent> EnumerateForPublication(CancellationToken token) =>
        EnumerateRange(0, Count, false, token, true);
    public IEnumerable<CanonicalMidiEvent> EnumerateRange(long start, long count, bool reverse = false,
        CancellationToken cancellationToken = default, bool retainReadPages = false)
    {
        if (retainReadPages) Sources.Warm(cancellationToken);
        using CanonicalSourceTable.Reader sources = Sources.OpenReader(cancellationToken);
        foreach (CompactCanonicalEvent value in Values.EnumerateRange(start, count, reverse,
            cancellationToken, retainReadPages)) yield return sources.Restore(in value);
    }
    public IEnumerator<CanonicalMidiEvent> GetEnumerator() => Enumerate().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 40)) return;
        Values.CollectRetainedStorage(collector); Sources.CollectRetainedStorage(collector);
    }
    public void Dispose()
    {
        try { Values.Dispose(); }
        finally { Sources.Dispose(); }
    }
}

public sealed partial class MidoraCompiler
{
    private sealed class CompactCanonicalComparer(CanonicalSourceTable.Reader sources, bool descending)
        : IComparer<CompactCanonicalEvent>
    {
        public int Compare(CompactCanonicalEvent left, CompactCanonicalEvent right) => descending
            ? CompareAscending(in right, in left) : CompareAscending(in left, in right);
        private int CompareAscending(in CompactCanonicalEvent x, in CompactCanonicalEvent y)
        {
            int value = x.Tick.CompareTo(y.Tick);
            if (value != 0) return value;
            value = ((int)x.Role).CompareTo((int)y.Role);
            if (value != 0) return value;
            value = x.ZeroBasedPort.CompareTo(y.ZeroBasedPort);
            if (value != 0) return value;
            value = x.ZeroBasedChannel.CompareTo(y.ZeroBasedChannel);
            if (value != 0) return value;
            value = x.SmfTrackOrder.CompareTo(y.SmfTrackOrder);
            if (value != 0) return value;
            if (x.Role == CanonicalEventRole.DirectMidi && y.Role == CanonicalEventRole.DirectMidi)
            {
                value = x.SmfEventOrder.CompareTo(y.SmfEventOrder);
                if (value != 0) return value;
                value = CanonicalMidiOrdering.DirectEndpointOrder(x.Message).CompareTo(
                    CanonicalMidiOrdering.DirectEndpointOrder(y.Message));
                if (value != 0) return value;
            }
            value = x.StableOrder.CompareTo(y.StableOrder);
            if (value != 0) return value;
            // Rare ties use the exact unchanged comparer, including its deliberate
            // omissions (Source.ExportTrackId / UsageId are not sort keys).
            return CanonicalComparer.Instance.Compare(sources.Restore(x), sources.Restore(y));
        }
    }

    private static CompactCanonicalStore FoldCompactDescendingEvents(
        IEnumerable<CompactCanonicalEvent> descending, CanonicalSourceTable sourceTable,
        CompilerStorageBudget budget, CancellationToken token, LogicalCanonicalPageIndex? pageIndex = null)
    {
        CompactCanonicalStore result = new(budget, token);
        try
        {
            using CanonicalSourceTable.Reader sources = sourceTable.OpenReader(token);
            using IDisposable remapLease = budget.ReserveWorking(4096L * 128);
            Dictionary<long, long> remap = [];
            long lastSource = -1, lastTarget = -1, currentTick = long.MinValue;
            Dictionary<(byte Port, byte Channel, long Target), long> groups = [];
            foreach (CompactCanonicalEvent value in descending)
            {
                if (value.Tick != currentTick) { groups.Clear(); currentTick = value.Tick; }
                if (value.SemanticTargetKey != long.MinValue && value.Role != CanonicalEventRole.DirectMidi)
                {
                    var key = (value.ZeroBasedPort, value.ZeroBasedChannel, value.SemanticTargetKey);
                    if (!groups.TryGetValue(key, out long selected)) groups.Add(key, value.SemanticGroup);
                    else if (selected != value.SemanticGroup) continue;
                }
                if (lastSource != value.SourceIndex)
                {
                    if (!remap.TryGetValue(value.SourceIndex, out lastTarget))
                    {
                        lastTarget = result.Sources.Intern(sources.Pattern(value.SourceIndex));
                        if (remap.Count < 4096) remap.Add(value.SourceIndex, lastTarget);
                    }
                    lastSource = value.SourceIndex;
                }
                CompactCanonicalEvent output = value with { SourceIndex = lastTarget };
                result.AddCompact(output);
                pageIndex?.AddCompact(in output);
            }
            result.Seal(); pageIndex?.SealPage(); return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static IEnumerable<CompactCanonicalEvent> MergeCompactEvents(
        IEnumerable<CompactCanonicalEvent> first, IEnumerable<CompactCanonicalEvent> second,
        IComparer<CompactCanonicalEvent> comparer)
    {
        using var a = first.GetEnumerator(); using var b = second.GetEnumerator();
        bool hasA = a.MoveNext(), hasB = b.MoveNext();
        while (hasA || hasB)
        {
            if (hasA && (!hasB || comparer.Compare(a.Current, b.Current) <= 0))
            { yield return a.Current; hasA = a.MoveNext(); }
            else { yield return b.Current; hasB = b.MoveNext(); }
        }
    }
}
