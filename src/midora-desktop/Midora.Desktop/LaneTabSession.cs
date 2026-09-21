using Midora.Domain;

namespace Midora.Desktop;

internal readonly record struct LaneTabKey(int Type, MidoraId Parameter = default, int Kind = 0, int Number = 0)
{
    internal static readonly LaneTabKey Velocity = new(0);
    internal static readonly LaneTabKey Instrument = new(1);
    internal static readonly LaneTabKey Opaque = new(4);
    internal static LaneTabKey From(ParameterLaneOption option) => option.IsOpaqueMidiLane ? Opaque
        : option.DirectMidiTarget is { } midi ? new(3, Kind: (int)midi.Kind, Number: midi.Data1)
        : new(2, option.ParameterId);
    internal static LaneTabKey From(MidiValueTarget target) => new(5, Kind: (int)target.Kind, Number: target.Number);
}

internal sealed record LaneTabDescriptor(LaneTabKey Key, string Name, int? Count, string Owners = "", string? CountError = null)
{
    public string CountText => Count is { } value ? value.ToString("N0") : CountError ?? "Counting…";
    public string Description => string.IsNullOrEmpty(Owners) ? CountText : $"{CountText} · {Owners}";
}

/// <summary>Owner-local, non-Project state. Only target descriptors and normalized
/// axes are retained; no event records, source snapshots, or hidden canvases.</summary>
internal sealed class LaneTabSession
{
    private readonly Action<LaneTabMemory>? _changed;
    private readonly HashSet<LaneTabKey> _hidden = [];
    private readonly List<LaneTabKey> _order = [];
    private readonly Dictionary<LaneTabKey, (double Minimum, double Maximum)> _axes = [];
    internal LaneTabKey Active { get; private set; } = LaneTabKey.Velocity;
    internal LaneTabKey EffectiveActive => Descriptors.Any(x => x.Key == Active) ? Active : LaneTabKey.Velocity;
    internal IReadOnlyList<LaneTabDescriptor> Descriptors { get; private set; } = [];
    internal LaneTabSession(LaneTabMemory? memory = null, Action<LaneTabMemory>? changed = null)
    {
        _changed = changed;
        if (memory is null) return;
        Active = memory.Active;
        foreach (var target in memory.Targets)
        {
            if (target.Key.Type >= 2) _order.Add(target.Key);
            if (target.Hidden) _hidden.Add(target.Key);
            _axes[target.Key] = (target.Minimum, target.Maximum);
        }
    }
    internal LaneTabMemory Capture() => new(Active, [.. _axes.Keys.Where(x => x.Type < 2)
        .OrderBy(x => x.Type).Concat(_order).Select(key =>
        { var axis = Axis(key); return new LaneTargetView(key, _hidden.Contains(key), axis.Minimum, axis.Maximum); })]);
    private void Changed() => _changed?.Invoke(Capture());

    internal void Sync(IReadOnlyList<LaneTabDescriptor> descriptors, bool authoritative = true)
    {
        Descriptors = descriptors;
        var available = descriptors.Select(static value => value.Key).ToHashSet();
        bool changed = false;
        if (authoritative)
        {
            changed |= _order.RemoveAll(key => !available.Contains(key)) != 0;
            changed |= _hidden.RemoveWhere(key => !available.Contains(key)) != 0;
            foreach (var key in _axes.Keys.Where(key => !available.Contains(key)).ToArray()) { _axes.Remove(key); changed = true; }
        }
        var known = _order.ToHashSet();
        foreach (var item in descriptors)
            if (item.Key.Type >= 2 && known.Add(item.Key)) { _order.Add(item.Key); changed = true; }
        if ((authoritative && !available.Contains(Active)) || _hidden.Contains(Active))
        { changed |= Active != LaneTabKey.Velocity; Active = LaneTabKey.Velocity; }
        if (changed) Changed();
    }
    internal IEnumerable<LaneTabDescriptor> Visible()
    {
        var byKey = Descriptors.ToDictionary(static value => value.Key);
        foreach (var descriptor in Descriptors.Where(static value => value.Key.Type < 2)) yield return descriptor;
        foreach (var key in _order)
            if (!_hidden.Contains(key) && byKey.TryGetValue(key, out var descriptor)) yield return descriptor;
    }
    internal bool IsVisible(LaneTabKey key) => !_hidden.Contains(key);
    internal void Show(LaneTabKey key)
    {
        if (!Descriptors.Any(value => value.Key == key)) return;
        bool changed = _hidden.Remove(key) | (Active != key);
        Active = key; if (changed) Changed();
    }
    internal void Hide(LaneTabKey key)
    { if (key.Type < 2) return; bool changed = _hidden.Add(key); if (Active == key) { Active = LaneTabKey.Velocity; changed = true; } if (changed) Changed(); }
    internal void Move(LaneTabKey key, LaneTabKey before, bool after = false)
    {
        if (key.Type < 2 || before.Type < 2 || key == before || !_order.Contains(before)) return;
        if (_order.Remove(key)) { _order.Insert(_order.IndexOf(before) + (after ? 1 : 0), key); Changed(); }
    }
    internal void SaveAxis(LaneTabKey key, (double Minimum, double Maximum) range)
    {
        if (!double.IsFinite(range.Minimum) || !double.IsFinite(range.Maximum)
            || range.Minimum < 0 || range.Maximum > 1 || range.Maximum <= range.Minimum || Axis(key) == range) return;
        _axes[key] = range; Changed();
    }
    internal (double Minimum, double Maximum) Axis(LaneTabKey key) => _axes.GetValueOrDefault(key, (0, 1));
}
