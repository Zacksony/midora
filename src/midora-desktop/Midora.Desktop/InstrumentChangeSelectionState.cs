using System.Runtime.CompilerServices;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>
/// Single-point selection for the A2a editor. Undo restores association roots,
/// so retain their selection without retaining those roots or their raw data.
/// This is session UI state, not a second source or a persisted selection.
/// </summary>
internal sealed class InstrumentChangeSelectionState
{
    private ConditionalWeakTable<InstrumentChangeSet, Bookmark> _history = new();
    private InstrumentChangeSet? _root;
    private MidoraId? _owner;
    private sealed class Bookmark { public MidoraId? Selected; }
    public MidoraId? SelectedId { get; private set; }

    public void Enter(MidoraId owner, InstrumentChangeSet root)
    {
        if (_owner != owner)
        {
            _owner = owner; _root = null; SelectedId = null;
            _history = new();
        }
        if (ReferenceEquals(root, _root)) return;
        _root = root;
        if (_history.TryGetValue(root, out var bookmark)) SelectedId = bookmark.Selected;
        else
        {
            if (SelectedId is { } id && !root.TryGet(id, out _)) SelectedId = null;
            _history.Add(root, new() { Selected = SelectedId });
        }
    }

    public void Select(MidoraId? id)
    {
        SelectedId = id;
        if (_root is { } root) _history.GetValue(root, static _ => new Bookmark()).Selected = id;
    }
}
