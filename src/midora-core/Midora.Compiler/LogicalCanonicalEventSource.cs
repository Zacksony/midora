using System.Collections;
using Midora.Common;
using Midora.Midi;

namespace Midora.Compiler;

/// <summary>Immutable final Logical events. Physical descending order permits bounded last-wins folding.</summary>
internal sealed class LogicalCanonicalEventSource : ILogicalCanonicalEventSource
{
    internal const int InlineEventLimit = 4096;
    private readonly CompactCanonicalStore _values;
    private readonly Page[] _pages;

    internal LogicalCanonicalEventSource(CompactCanonicalStore values,
        CancellationToken cancellationToken, LogicalCanonicalPageIndex? preparedIndex = null)
    {
        _values = values;
        long metadataBytes = CompilerStorageBudget.MetadataArrayBytes<Page>(values.PageCount);
        values.ReserveOwnedMetadata(metadataBytes);
        try
        {
            // Allocate once, not a growing list followed by a second full index array.
            _pages = new Page[values.PageCount];
            if (preparedIndex is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (preparedIndex.Pages.Count != _pages.Length || preparedIndex.EventCount != values.Count)
                    throw new InvalidOperationException("Logical compiler page index is incomplete.");
                for (int i = 0; i < _pages.Length; i++)
                    _pages[i] = preparedIndex.Pages[_pages.Length - 1 - i];
                NoteOnEventCount = preparedIndex.NoteOnEventCount;
                return;
            }
            int pageIndex = _pages.Length - 1;
            long index = 0, first = 0, maximum = 0, minimum = 0, noteOns = 0;
            UnitMask units = default;
            foreach (CanonicalMidiEvent value in values.EnumerateForPublication(cancellationToken))
            {
                if (index == first) maximum = value.Tick;
                minimum = value.Tick;
                units = units.With(value.ZeroBasedPort * 16 + value.ZeroBasedChannel);
                if (value.Message.MessageType == MidiMessageType.NoteOn && value.Message.Byte2 != 0) noteOns++;
                index++;
                if (index - first != CompilerValueStore<CompactCanonicalEvent>.PageCapacity) continue;
                _pages[pageIndex--] = new(first, checked((int)(index - first)), minimum, maximum, units);
                first = index; units = default;
            }
            if (index != first) _pages[pageIndex--] = new(first, checked((int)(index - first)), minimum, maximum, units);
            if (pageIndex != -1) throw new InvalidOperationException("Logical compiler page index is incomplete.");
            NoteOnEventCount = noteOns;
            values.ReleaseFullyResidentBacking();
        }
        catch { values.ReleaseOwnedMetadata(metadataBytes); throw; }
    }

    public long EventCount => _values.Count;
    public long NoteOnEventCount { get; }
    // Promotion is fused with the one required musical-fingerprint pass; no extra
    // scan to build indexes or to warm pages before consumers see the result.
    internal IEnumerable<CanonicalMidiEvent> EnumerateForPublication(CancellationToken token)
    {
        foreach (CanonicalMidiEvent value in _values.EnumerateRange(0, _values.Count,
            reverse: true, token, retainReadPages: true)) yield return value;
        _values.ReleaseFullyResidentBacking();
    }
    public IEnumerable<CanonicalMidiEvent> Enumerate(long startTick, long endTick, bool includeEnd,
        CancellationToken cancellationToken = default) => EnumerateCore(startTick, endTick, includeEnd, -1, cancellationToken);
    public IEnumerable<CanonicalMidiEvent> EnumerateUnit(byte zeroBasedPort, byte zeroBasedChannel,
        long startTick, long endTick, bool includeEnd, CancellationToken cancellationToken = default) =>
        EnumerateCore(startTick, endTick, includeEnd, zeroBasedPort * 16 + zeroBasedChannel, cancellationToken);

    private IEnumerable<CanonicalMidiEvent> EnumerateCore(long startTick, long endTick, bool includeEnd,
        int unit, CancellationToken cancellationToken)
    {
        int left = 0, right = _pages.Length;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            if (_pages[middle].MaximumTick < startTick) left = middle + 1;
            else right = middle;
        }
        for (int pageIndex = left; pageIndex < _pages.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Page page = _pages[pageIndex];
            if (page.MinimumTick > endTick || !includeEnd && page.MinimumTick == endTick) yield break;
            if (unit >= 0 && !page.Units.Contains(unit)) { pageIndex++; continue; }
            int runEnd = pageIndex + 1;
            while (runEnd < _pages.Length && (unit < 0 || _pages[runEnd].Units.Contains(unit))
                && (_pages[runEnd].MinimumTick < endTick || includeEnd && _pages[runEnd].MinimumTick == endTick))
                runEnd++;
            long runStart = _pages[runEnd - 1].Start;
            long runCount = page.Start + page.Count - runStart;
            pageIndex = runEnd;
            foreach (CanonicalMidiEvent value in _values.EnumerateRange(runStart, runCount,
                reverse: true, cancellationToken))
            {
                if (value.Tick < startTick) continue;
                if (value.Tick > endTick || !includeEnd && value.Tick == endTick) yield break;
                if (unit < 0 || value.ZeroBasedPort * 16 + value.ZeroBasedChannel == unit) yield return value;
            }
        }
    }

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 64)) return;
        collector.Array(_pages);
        _values.CollectRetainedStorage(collector);
    }

    internal readonly record struct Page(long Start, int Count, long MinimumTick, long MaximumTick, UnitMask Units);
    internal readonly record struct UnitMask(ulong A, ulong B, ulong C, ulong D)
    {
        public UnitMask With(int unit) => (unit >> 6) switch
        {
            0 => this with { A = A | (1UL << (unit & 63)) },
            1 => this with { B = B | (1UL << (unit & 63)) },
            2 => this with { C = C | (1UL << (unit & 63)) },
            _ => this with { D = D | (1UL << (unit & 63)) }
        };
        public bool Contains(int unit) => ((((unit >> 6) switch { 0 => A, 1 => B, 2 => C, _ => D })
            >> (unit & 63)) & 1) != 0;
    }
}

/// <summary>Bounded page summaries recorded as final winners are written, not read back.</summary>
internal sealed class LogicalCanonicalPageIndex(CompilerStorageBudget budget) : IDisposable
{
    public CompilerMetadataList<LogicalCanonicalEventSource.Page> Pages { get; } = new(budget);
    public long EventCount { get; private set; }
    public long NoteOnEventCount { get; private set; }
    private long _first, _minimum, _maximum;
    private LogicalCanonicalEventSource.UnitMask _units;
    public void Add(in CanonicalMidiEvent value)
        => AddCore(value.Tick, value.ZeroBasedPort, value.ZeroBasedChannel, value.Message);
    public void AddCompact(in CompactCanonicalEvent value)
        => AddCore(value.Tick, value.ZeroBasedPort, value.ZeroBasedChannel, value.Message);
    private void AddCore(long tick, byte port, byte channel, MidiMessage message)
    {
        if (EventCount == _first) _maximum = tick;
        _minimum = tick;
        _units = _units.With(port * 16 + channel);
        if (message.MessageType == MidiMessageType.NoteOn && message.Byte2 != 0) NoteOnEventCount++;
        EventCount++;
        if (EventCount - _first == CompilerValueStore<CompactCanonicalEvent>.PageCapacity) SealPage();
    }
    public void SealPage()
    {
        if (EventCount == _first) return;
        Pages.Add(new(_first, checked((int)(EventCount - _first)), _minimum, _maximum, _units));
        _first = EventCount; _units = default;
    }
    public void Dispose() => Pages.Dispose();
}

public sealed partial class MidoraCompiler
{
    public LogicalCompilationStorageTelemetry LastLogicalStorageTelemetry { get; private set; }
    private static IEnumerable<CanonicalMidiEvent> MergeCanonicalEvents(
        IEnumerable<CanonicalMidiEvent> first, IEnumerable<CanonicalMidiEvent> second)
    {
        using var a = first.GetEnumerator(); using var b = second.GetEnumerator();
        bool hasA = a.MoveNext(), hasB = b.MoveNext();
        while (hasA || hasB)
        {
            if (hasA && (!hasB || CanonicalComparer.Instance.Compare(a.Current, b.Current) <= 0))
            { yield return a.Current; hasA = a.MoveNext(); }
            else { yield return b.Current; hasB = b.MoveNext(); }
        }
    }

    private static CompactCanonicalStore FinalizeRangeEvents(
        IEnumerable<CompactCanonicalEvent> descendingMiddle, CompilerExternalSorter<CompactCanonicalEvent> boundaries,
        CanonicalSourceTable sources, CompilerStorageBudget budget, CancellationToken cancellationToken,
        LogicalCanonicalPageIndex pageIndex)
    {
        // Filtering preserves the input order. Only newly introduced boundary events need sorting;
        // re-sorting the complete middle would add a full external-sort pass after every edit.
        using CanonicalSourceTable.Reader reader = sources.OpenReader(cancellationToken);
        return FoldCompactDescendingEvents(MergeCompactEvents(descendingMiddle,
            boundaries.ReadSorted(cancellationToken), new CompactCanonicalComparer(reader, descending: true)),
            sources, budget, cancellationToken, pageIndex);
    }
}

/// <summary>Runtime measurements only; excluded from canonical identity and Project persistence.</summary>
public readonly record struct LogicalCompilationStorageTelemetry(
    double ExpansionMilliseconds, double MaterializationMilliseconds, double RangeMilliseconds,
    double PublicationMilliseconds, long PeakResidentBytes, long PeakWorkingBytes, long PeakSpillBytes,
    long RetainedResidentBytes, long RetainedSpillBytes,
    long PeakMetadataBytes = 0, long RetainedMetadataBytes = 0);

/// <summary>Adapter for existing emission helpers; reading or mutating the append-only sink is unsupported.</summary>
internal sealed class AppendOnlyCollection<T>(Action<T> append) : ICollection<T>
{
    public void Add(T item) => append(item);
    public bool IsReadOnly => false;
    public int Count => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(T item) => throw new NotSupportedException();
    public void CopyTo(T[] array, int arrayIndex) => throw new NotSupportedException();
    public bool Remove(T item) => throw new NotSupportedException();
    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
