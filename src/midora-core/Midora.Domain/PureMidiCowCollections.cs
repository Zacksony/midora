using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Midora.Domain;

/// <summary>
/// An editable facade over an immutable formal value root. Capturing or adopting
/// a root never recreates the mutable MIDI objects. Only an accessed slot gains
/// an owner-local facade, so its change sink cannot modify another revision.
/// </summary>
internal sealed class PureMidiCowList<TObject, TValue> : IReadOnlyList<TObject>
    where TObject : class
{
    private readonly Func<TValue, TObject> _materialize;
    private PersistentFormalValueSequence<TValue> _root = PersistentFormalValueSequence<TValue>.Empty;
    private readonly Dictionary<int, TObject> _items = [];

    public PureMidiCowList(Func<TValue, TObject> materialize) => _materialize = materialize;
    public int Count { get; private set; }

    public TObject this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (_items.TryGetValue(index, out TObject? value)) return value;
            value = _materialize(_root[index]);
            _items.Add(index, value);
            return value;
        }
        set
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            _items[index] = value;
        }
    }

    public void Adopt(PersistentFormalValueSequence<TValue> root)
    {
        if (Count != 0) throw new InvalidOperationException("A MIDI value facade must be empty before adopting a root.");
        _root = root;
        Count = root.Count;
    }

    public void Commit(PersistentFormalValueSequence<TValue> root)
    {
        if (Count != root.Count) throw new InvalidOperationException("MIDI value facade and formal root counts differ.");
        _root = root;
    }

    public void Add(TObject value) => _items.Add(Count++, value);
    public void AddRange(IEnumerable<TObject> values)
    {
        foreach (TObject value in values) Add(value);
    }

    public void Insert(int index, TObject value)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        // Compatibility for small IList edits. Detached batch commands publish
        // prepared roots rather than invoking this splice once per result item.
        for (int current = Count - 1; current >= index; current--)
            _items[current + 1] = this[current];
        Count++;
        _items[index] = value;
    }

    public void RemoveAt(int index) => RemoveRange(index, 1);
    public void RemoveRange(int index, int count)
    {
        if (index < 0 || count < 0 || index > Count - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        int newCount = Count - count;
        for (int current = index; current < newCount; current++)
            _items[current] = this[current + count];
        for (int current = newCount; current < Count; current++) _items.Remove(current);
        Count = newCount;
    }

    public void Clear()
    {
        _items.Clear();
        _root = PersistentFormalValueSequence<TValue>.Empty;
        Count = 0;
    }

    public IEnumerable<TObject> MaterializedItems => _items.Values;
    public IEnumerator<TObject> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Stable-ID lookup shares the formal ordinal tree, not mutable objects.</summary>
internal sealed class PureMidiCowIndexedLookup<TObject> where TObject : class
{
    private readonly Func<MidoraId, int?> _find;
    private readonly Func<int, TObject> _get;
    private readonly Dictionary<MidoraId, TObject> _assigned = [];

    public PureMidiCowIndexedLookup(Func<MidoraId, int?> find, Func<int, TObject> get)
    {
        _find = find;
        _get = get;
    }

    public TObject this[MidoraId id] { set => _assigned[id] = value; }
    public void Add(MidoraId id, TObject value) => _assigned.Add(id, value);
    public bool ContainsKey(MidoraId id) => _assigned.ContainsKey(id) || _find(id) is not null;
    public bool TryGetValue(MidoraId id, [NotNullWhen(true)] out TObject? value)
    {
        if (_assigned.TryGetValue(id, out value)) return true;
        if (_find(id) is not int ordinal) return false;
        value = _get(ordinal);
        return true;
    }
    public bool Remove(MidoraId id) => _assigned.Remove(id);
    public void Clear() => _assigned.Clear();
}

/// <summary>A replacement map whose immutable records are shared across revisions.</summary>
internal sealed class PureMidiCowDictionary<TObject, TValue> : IReadOnlyDictionary<MidoraId, TObject>
    where TObject : class
{
    private readonly Func<TValue, TObject> _materialize;
    private ImmutableDictionary<MidoraId, TValue> _root = ImmutableDictionary<MidoraId, TValue>.Empty;
    private readonly Dictionary<MidoraId, TObject> _items = [];
    private readonly HashSet<MidoraId> _removed = [];
    private int _count;

    public PureMidiCowDictionary(Func<TValue, TObject> materialize) => _materialize = materialize;
    public int Count => _count;
    public IEnumerable<MidoraId> Keys => _root.Keys.Where(id => !_removed.Contains(id))
        .Concat(_items.Keys.Where(id => !_root.ContainsKey(id)));
    public IEnumerable<TObject> Values => Keys.Select(id => this[id]);
    public IEnumerable<TObject> MaterializedItems => _items.Values;

    public TObject this[MidoraId id]
    {
        get => TryGetValue(id, out TObject? value) ? value! : throw new KeyNotFoundException();
        set
        {
            if (!ContainsKey(id)) _count++;
            _removed.Remove(id);
            _items[id] = value;
        }
    }

    public void Adopt(ImmutableDictionary<MidoraId, TValue> root)
    {
        if (Count != 0) throw new InvalidOperationException("A MIDI replacement facade must be empty before adopting a root.");
        _root = root;
        _count = root.Count;
    }

    public void Commit(ImmutableDictionary<MidoraId, TValue> root)
    {
        _root = root;
        _count = root.Count;
        _removed.Clear();
    }

    public bool ContainsKey(MidoraId id) => !_removed.Contains(id)
        && (_items.ContainsKey(id) || _root.ContainsKey(id));
    public bool TryGetValue(MidoraId id, [MaybeNullWhen(false)] out TObject value)
    {
        value = null;
        if (_removed.Contains(id)) return false;
        if (_items.TryGetValue(id, out value)) return true;
        if (!_root.TryGetValue(id, out TValue? stored)) return false;
        value = _materialize(stored);
        _items[id] = value;
        return true;
    }
    public void Add(MidoraId id, TObject value)
    {
        if (ContainsKey(id)) throw new ArgumentException("A MIDI replacement with the same ID already exists.");
        this[id] = value;
    }
    public bool Remove(MidoraId id)
    {
        bool exists = ContainsKey(id);
        if (exists) _count--;
        _items.Remove(id);
        if (_root.ContainsKey(id)) _removed.Add(id);
        return exists;
    }
    public void Clear()
    {
        _root = ImmutableDictionary<MidoraId, TValue>.Empty;
        _items.Clear();
        _removed.Clear();
        _count = 0;
    }
    public IEnumerator<KeyValuePair<MidoraId, TObject>> GetEnumerator()
    {
        foreach (MidoraId id in Keys) yield return new(id, this[id]);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
