using System.Collections;

namespace Midora.Compiler;

public sealed partial class MidoraCompiler
{
    // Compact on emission, rather than retaining the expanded SourceReference on
    // every event until the entire voice has finished. Tiny repeated voices keep
    // their interned arrays; larger voices use the shared bounded value store.
    private sealed class RawEventBuffer : ICollection<RawMidiEvent>, IReadOnlyList<RawMidiEvent>, IDisposable
    {
        private const int InlineRecordLimit = 4096;
        private readonly RawEventPatternPool _pool;
        private readonly SourceReference _context;
        private readonly long _sequence;
        private readonly CancellationToken _cancellationToken;
        private List<CompactRawEvent>? _inline = new(32);
        private CompilerValueStore<CompactRawEvent>? _store;
        private RawEventSequence? _frozen;
        private HashCode _hash;
        private bool _disposed;

        public RawEventBuffer(RawEventPatternPool pool, SourceReference context,
            long sequence, CancellationToken cancellationToken)
        {
            _pool = pool;
            _context = context;
            _sequence = sequence;
            _cancellationToken = cancellationToken;
        }

        public int Count { get; private set; }
        public bool HasSoundingNotes { get; private set; }
        public CompilerStorageBudget Storage => _pool.Storage;
        public CancellationToken CancellationToken => _cancellationToken;
        public bool IsReadOnly => _frozen is not null;
        public RawMidiEvent this[int index]
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                if (_frozen is not null) return _frozen[index];
                return Restore(_store is null ? _inline![index] : _store[index]);
            }
        }

        public bool Matches(RawEventPatternPool pool, SourceReference context, long sequence) =>
            ReferenceEquals(pool, _pool) && context == _context && sequence == _sequence;

        public void Add(RawMidiEvent value)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frozen is not null) throw new InvalidOperationException("The raw event buffer is sealed.");
            if ((Count & 255) == 0) _cancellationToken.ThrowIfCancellationRequested();
            if (Count == int.MaxValue)
                throw new InvalidOperationException("One expanded logical voice exceeds the supported event count.");
            CompactRawEvent record = _pool.Compact(value, _context, _sequence);
            _hash.Add(record);
            if (_store is null && Count == InlineRecordLimit)
            {
                _store = new(_pool.Storage, _cancellationToken);
                foreach (CompactRawEvent prior in _inline!) _store.Add(prior);
                _inline = null;
            }
            if (_store is null) _inline!.Add(record);
            else _store.Add(record);
            Count++;
            HasSoundingNotes |= value.Kind == RawMessageKind.NoteOn && value.Data2 != 0;
        }

        public RawEventSequence Freeze()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frozen is not null) return _frozen;
            _cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<CompactRawEvent> records;
            if (_store is null)
            {
                records = _pool.Intern(_inline!.ToArray(), _hash.ToHashCode(), _cancellationToken);
                _inline = null;
            }
            else
            {
                _store.Seal();
                records = _pool.Intern(_store, Count, _hash.ToHashCode(), _cancellationToken);
                _store = null; // immutable sequence now owns the backing store
            }
            return _frozen = new(_pool, records, _context, _sequence);
        }

        private RawMidiEvent Restore(CompactRawEvent value) => new(
            unchecked(value.Tick + _context.Tick), value.Kind, value.Data1, value.Data2,
            value.Role, unchecked(value.Sequence + _sequence), value.Target,
            value.GroupIsRelative ? unchecked(value.Group + _sequence) : value.Group,
            _pool.RestoreSource(value.Source, _context, unchecked(value.SourceTick + _context.Tick)));

        public IEnumerator<RawMidiEvent> GetEnumerator()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frozen is not null)
            {
                foreach (RawMidiEvent value in _frozen) yield return value;
                yield break;
            }
            if (_store is not null)
            {
                foreach (CompactRawEvent value in _store.Enumerate(false, _cancellationToken)) yield return Restore(value);
            }
            else
            {
                foreach (CompactRawEvent value in _inline!) yield return Restore(value);
            }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Contains(RawMidiEvent item) => this.Any(value => value == item);
        public void CopyTo(RawMidiEvent[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (arrayIndex < 0 || arrayIndex > array.Length - Count) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            foreach (RawMidiEvent value in this) array[arrayIndex++] = value;
        }
        public bool Remove(RawMidiEvent item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inline = null;
            _store?.Dispose();
            _store = null;
        }
    }

    private sealed class RawCompactStoreList(CompilerValueStore<CompactRawEvent> store, long start, int count)
        : IReadOnlyList<CompactRawEvent>
    {
        public int Count => count;
        public CompactRawEvent this[int index] => (uint)index < (uint)count
            ? store[start + index] : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<CompactRawEvent> GetEnumerator() => store.EnumerateRange(start, count).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
