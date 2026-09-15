using System.Collections;
using System.Runtime.CompilerServices;
using Midora.Compiler;

namespace Midora.Desktop;

/// <summary>Indexable WPF ItemsSource; row objects exist only for requested indexes.</summary>
internal sealed class VirtualDiagnosticRows : IReadOnlyList<DiagnosticRow>, IList
{
    private const int CacheCapacity = 256;
    public const int PageSize = 4_096;
    private static readonly ConditionalWeakTable<DiagnosticRow, RowOrdinal> RowOrdinals = new();
    private readonly object _identity = new();
    private readonly ICompilerDiagnosticSequence? _compilerDiagnostics;
    private readonly IReadOnlyList<DiagnosticRow>? _rows;
    private readonly bool _isCurrent;
    private readonly int[] _cacheIndexes = Enumerable.Repeat(-1, CacheCapacity).ToArray();
    private readonly DiagnosticRow?[] _cache = new DiagnosticRow[CacheCapacity];

    public VirtualDiagnosticRows(IEnumerable<CompilerDiagnostic> diagnostics, bool isCurrent)
        : this(CompilerDiagnosticSequence.Wrap(diagnostics), isCurrent, 0) { }

    private VirtualDiagnosticRows(ICompilerDiagnosticSequence diagnostics, bool isCurrent, long startOrdinal)
    {
        _compilerDiagnostics = diagnostics;
        _isCurrent = isCurrent;
        StartOrdinal = startOrdinal;
    }

    public VirtualDiagnosticRows(IReadOnlyList<DiagnosticRow> rows) => _rows = rows;
    public static VirtualDiagnosticRows Empty { get; } = new(Array.Empty<DiagnosticRow>());
    public long TotalCount => _compilerDiagnostics?.Count ?? _rows!.Count;
    public bool IsPaged => TotalCount > int.MaxValue;
    public long StartOrdinal { get; }
    public long PageIndex => StartOrdinal / PageSize;
    public long PageCount => IsPaged ? (TotalCount - 1) / PageSize + 1 : 1;
    public int Count => IsPaged ? (int)Math.Min(PageSize, TotalCount - StartOrdinal) : (int)TotalCount;
    public long SourceRecordCount => _compilerDiagnostics is CompilerDiagnosticList compact
        ? compact.SourceRecordCount : TotalCount;
    public bool? UniformIsCurrent => _compilerDiagnostics is not null ? _isCurrent : null;
    internal int CachedRowCount => _cache.Count(static row => row is not null);

    public DiagnosticRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_rows is not null) return _rows[index];
            int slot = index % CacheCapacity;
            if (_cacheIndexes[slot] == index) return _cache[slot]!;
            DiagnosticRow row = DiagnosticProjection.FromCompiler(_compilerDiagnostics![StartOrdinal + index], _isCurrent);
            RowOrdinals.Add(row, new(_identity, index));
            _cacheIndexes[slot] = index;
            _cache[slot] = row;
            return row;
        }
    }

    public VirtualDiagnosticRows GetPage(long pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return pageIndex == PageIndex ? this
            : new VirtualDiagnosticRows(_compilerDiagnostics!, _isCurrent, checked(pageIndex * PageSize));
    }

    public VirtualDiagnosticRows Filter(Func<DiagnosticRow, bool> predicate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_compilerDiagnostics is CompilerDiagnosticList compact)
            return new(compact.Filter(value => predicate(DiagnosticProjection.FromCompiler(value, _isCurrent)),
                cancellationToken), _isCurrent);
        if (_compilerDiagnostics is not null)
        {
            List<CompilerDiagnostic> selected = [];
            for (long index = 0; index < _compilerDiagnostics.Count; index++)
            {
                if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                CompilerDiagnostic diagnostic = _compilerDiagnostics[index];
                if (predicate(DiagnosticProjection.FromCompiler(diagnostic, _isCurrent))) selected.Add(diagnostic);
            }
            return new(selected.ToArray(), _isCurrent);
        }
        List<DiagnosticRow> rows = [];
        for (int index = 0; index < _rows!.Count; index++)
        {
            if ((index & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            DiagnosticRow row = _rows[index];
            if (predicate(row)) rows.Add(row);
        }
        return new(rows.ToArray());
    }

    public int IndexOf(object? value)
    {
        if (value is not DiagnosticRow row) return -1;
        if (RowOrdinals.TryGetValue(row, out RowOrdinal? ordinal))
            return ReferenceEquals(ordinal.Identity, _identity) ? ordinal.Index : -1;
        // Compiler rows are revision-bound view identities. A previous view's
        // selection must not trigger an O(logical pair count) WPF IndexOf scan.
        if (_compilerDiagnostics is not null) return -1;
        for (int index = 0; index < Count; index++)
            if (Equals(this[index], row)) return index;
        return -1;
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator<DiagnosticRow> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(Array array, int index)
    {
        for (int ordinal = 0; ordinal < Count; ordinal++) array.SetValue(this[ordinal], index + ordinal);
    }
    bool IList.IsReadOnly => true;
    bool IList.IsFixedSize => true;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;
    object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    private sealed record RowOrdinal(object Identity, int Index);
}
