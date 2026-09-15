using System.Collections;
using Midora.Common;
using Midora.Domain;

namespace Midora.Compiler;

/// <summary>
/// An immutable logical diagnostic sequence. Repeated overlap diagnostics share
/// compact sources and ranges; Count and enumeration still include every pair.
/// </summary>
public sealed class CompilerDiagnosticList : ICompilerDiagnosticSequence, IRetainedStorageSource
{
    private readonly DiagnosticSource[] _sources;
    private readonly DiagnosticRange[] _ranges;
    private readonly long[] _severityCounts;

    private CompilerDiagnosticList(DiagnosticSource[] sources, DiagnosticRange[] ranges)
    {
        _sources = sources;
        _ranges = ranges;
        _severityCounts = new long[4];
        foreach (DiagnosticRange range in ranges)
        {
            DiagnosticSource source = sources[range.SourceIndex];
            for (int severity = 0; severity < _severityCounts.Length; severity++)
                _severityCounts[severity] = DiagnosticCapacityExceededException.Add(_severityCounts[severity],
                    source.CountSeverity(range.Start, range.Count, (DiagnosticSeverity)severity));
        }
    }

    public static CompilerDiagnosticList Empty { get; } = new([], []);
    public long Count => _ranges.Length == 0 ? 0 : _ranges[^1].EndExclusive;
    public long SourceRecordCount => _sources.Sum(static source => (long)source.Count);
    public int RangeCount => _ranges.Length;

    public CompilerDiagnostic this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int low = 0;
            int high = _ranges.Length - 1;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_ranges[middle].EndExclusive <= index) low = middle + 1;
                else high = middle;
            }
            DiagnosticRange range = _ranges[low];
            return _sources[range.SourceIndex].Get(
                checked(range.Start + (int)(index - (range.EndExclusive - range.Count))));
        }
    }

    public CompilerDiagnostic this[int index] => this[(long)index];
    public CompilerDiagnostic this[Index index] => this[index.IsFromEnd ? Count - index.Value : index.Value];

    public long CountSeverity(DiagnosticSeverity severity) => _severityCounts[(int)severity];

    public static long CountSeverity(IEnumerable<CompilerDiagnostic> diagnostics, DiagnosticSeverity severity) =>
        diagnostics is CompilerDiagnosticList compact
            ? compact.CountSeverity(severity)
            : diagnostics.LongCount(value => value.Severity == severity);

    internal static CompilerDiagnosticList FromFrozen(IEnumerable<CompilerDiagnostic> diagnostics)
    {
        if (diagnostics is CompilerDiagnosticList compact) return compact;
        CompilerDiagnostic[] values = diagnostics as CompilerDiagnostic[] ?? diagnostics.ToArray();
        if (values.Length == 0) return Empty;
        return new([new ArrayDiagnosticSource(values)], [new(0, 0, values.Length, values.Length)]);
    }

    /// <summary>Freezes ordinary diagnostics around an already frozen insertion.</summary>
    internal static CompilerDiagnosticList Insert(
        IReadOnlyList<CompilerDiagnostic> ordinary,
        int insertionIndex,
        CompilerDiagnosticList inserted)
    {
        if ((uint)insertionIndex > (uint)ordinary.Count)
            throw new ArgumentOutOfRangeException(nameof(insertionIndex));
        if (inserted.Count == 0) return FromFrozen(ordinary);
        if (ordinary.Count == 0) return inserted;
        Builder builder = new();
        int ordinarySource = builder.AddSource(new ArrayDiagnosticSource(ordinary.ToArray()));
        builder.AddRange(ordinarySource, 0, insertionIndex);
        int sourceOffset = builder.SourceCount;
        foreach (DiagnosticSource source in inserted._sources) builder.AddSource(source);
        foreach (DiagnosticRange range in inserted._ranges)
            builder.AddRange(sourceOffset + range.SourceIndex, range.Start, range.Count);
        builder.AddRange(ordinarySource, insertionIndex, ordinary.Count - insertionIndex);
        return builder.Build();
    }

    /// <summary>
    /// Evaluates each physical source once, then preserves all matching logical
    /// repetitions and their exact order. Does not enumerate overlap pairs.
    /// </summary>
    public CompilerDiagnosticList Filter(
        Func<CompilerDiagnostic, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        cancellationToken.ThrowIfCancellationRequested();
        if (Count == 0) return this;
        DiagnosticSource[] sources = new DiagnosticSource[_sources.Length];
        int[][] selections = new int[_sources.Length][];
        for (int sourceIndex = 0; sourceIndex < _sources.Length; sourceIndex++)
        {
            DiagnosticSource source = _sources[sourceIndex];
            List<int> selected = [];
            for (int index = 0; index < source.Count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (predicate(source.Get(index))) selected.Add(index);
            }
            if (selected.Count == source.Count)
            {
                sources[sourceIndex] = source;
                selections[sourceIndex] = [];
            }
            else
            {
                int[] indexes = selected.ToArray();
                selections[sourceIndex] = indexes;
                sources[sourceIndex] = new SelectedDiagnosticSource(source, indexes);
            }
        }
        Builder builder = new();
        foreach (DiagnosticSource source in sources) builder.AddSource(source);
        for (int index = 0; index < _ranges.Length; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            DiagnosticRange range = _ranges[index];
            if (ReferenceEquals(sources[range.SourceIndex], _sources[range.SourceIndex]))
                builder.AddRange(range.SourceIndex, range.Start, range.Count);
            else
            {
                int[] selected = selections[range.SourceIndex];
                int start = LowerBound(selected, range.Start);
                int end = LowerBound(selected, range.Start + range.Count);
                builder.AddRange(range.SourceIndex, start, end - start);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return builder.Build();
    }

    private static int LowerBound(int[] values, int value)
    {
        int low = 0;
        int high = values.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (values[middle] < value) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    public IEnumerator<CompilerDiagnostic> GetEnumerator()
    {
        foreach (DiagnosticRange range in _ranges)
        {
            DiagnosticSource source = _sources[range.SourceIndex];
            int end = range.Start + range.Count;
            for (int index = range.Start; index < end; index++) yield return source.Get(index);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void CollectRetainedStorage(RetainedStorageCollector collector)
    {
        if (!collector.Add(this, 48)) return;
        collector.Array(_ranges);
        collector.Array(_severityCounts);
        if (collector.Array(_sources))
            foreach (DiagnosticSource source in _sources) source.CollectRetainedStorage(collector);
    }

    internal readonly record struct OverlapDiagnosticSourceValue(
        MidoraId TrackId, MidoraId SegmentId, MidoraId InstanceId,
        MidoraId InstrumentId, long StartTick);

    private readonly record struct DiagnosticRange(int SourceIndex, int Start, int Count, long EndExclusive);

    internal sealed class Builder
    {
        private readonly List<DiagnosticSource> _sources = [];
        private readonly List<DiagnosticRange> _ranges = [];
        private long _count;
        public int SourceCount => _sources.Count;
        internal int AddSource(DiagnosticSource source)
        {
            _sources.Add(source);
            return _sources.Count - 1;
        }
        public int AddOverlapSource(OverlapDiagnosticSourceValue[] source, DiagnosticSeverity severity) =>
            AddSource(new OverlapDiagnosticSource(source, severity));
        public void AddRange(int sourceIndex, int start, int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(start);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if ((uint)sourceIndex >= (uint)_sources.Count
                || start > _sources[sourceIndex].Count - count)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex));
            if (count == 0) return;
            long nextCount = DiagnosticCapacityExceededException.Add(_count, count);
            _ranges.Add(new(sourceIndex, start, count, nextCount));
            _count = nextCount;
        }
        public CompilerDiagnosticList Build() => _count == 0 ? Empty : new(_sources.ToArray(), _ranges.ToArray());
    }

    internal abstract class DiagnosticSource : IRetainedStorageSource
    {
        public abstract int Count { get; }
        public virtual DiagnosticSeverity? UniformSeverity => null;
        public abstract CompilerDiagnostic Get(int index);
        public int CountSeverity(int start, int count, DiagnosticSeverity severity)
        {
            if (UniformSeverity is { } uniform) return uniform == severity ? count : 0;
            int result = 0;
            int end = start + count;
            for (int index = start; index < end; index++)
                if (Get(index).Severity == severity) result++;
            return result;
        }
        public abstract void CollectRetainedStorage(RetainedStorageCollector collector);
    }

    private sealed class ArrayDiagnosticSource(CompilerDiagnostic[] values) : DiagnosticSource
    {
        public override int Count => values.Length;
        public override CompilerDiagnostic Get(int index) => values[index];
        public override void CollectRetainedStorage(RetainedStorageCollector collector)
        {
            if (!collector.Add(this, 32) || !collector.Array(values)) return;
            foreach (CompilerDiagnostic diagnostic in values)
            {
                collector.Add(diagnostic, 208);
                collector.Text(diagnostic.Code);
                collector.Text(diagnostic.Message);
            }
        }
    }

    private sealed class OverlapDiagnosticSource(
        OverlapDiagnosticSourceValue[] values,
        DiagnosticSeverity severity) : DiagnosticSource
    {
        public override int Count => values.Length;
        public override DiagnosticSeverity? UniformSeverity => severity;
        public override CompilerDiagnostic Get(int index)
        {
            OverlapDiagnosticSourceValue value = values[index];
            return new("MIDORA2201", severity,
                "An Event Instrument Instance has a policy-constrained overlap.",
                new(value.TrackId, value.SegmentId, value.InstanceId,
                    value.InstrumentId, Tick: value.StartTick));
        }
        public override void CollectRetainedStorage(RetainedStorageCollector collector)
        {
            if (collector.Add(this, 32)) collector.Array(values);
        }
    }

    private sealed class SelectedDiagnosticSource(DiagnosticSource source, int[] indexes) : DiagnosticSource
    {
        public override int Count => indexes.Length;
        public override DiagnosticSeverity? UniformSeverity => source.UniformSeverity;
        public override CompilerDiagnostic Get(int index) => source.Get(indexes[index]);
        public override void CollectRetainedStorage(RetainedStorageCollector collector)
        {
            if (!collector.Add(this, 32)) return;
            collector.Array(indexes);
            source.CollectRetainedStorage(collector);
        }
    }
}
