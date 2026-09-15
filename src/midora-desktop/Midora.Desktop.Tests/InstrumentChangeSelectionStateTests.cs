using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class InstrumentChangeSelectionStateTests
{
    [Fact]
    public void CreateUndoRedoRestoresSinglePointSelectionWithoutRetainingAnotherOwner()
    {
        var a = new InstrumentChange(new(10), new(11), new(12), new(13));
        var b = new InstrumentChange(new(20), new(21), new(22), new(23));
        var first = InstrumentChangeSet.Empty.Add(a, true);
        var second = first.Add(b, true);
        var state = new InstrumentChangeSelectionState();
        state.Enter(new(1), first); state.Select(a.Id);
        state.Enter(new(1), second); state.Select(b.Id);
        state.Enter(new(1), first); Assert.Equal(a.Id, state.SelectedId);
        state.Enter(new(1), second); Assert.Equal(b.Id, state.SelectedId);
        state.Select(null);
        state.Enter(new(1), first); Assert.Equal(a.Id, state.SelectedId);
        state.Enter(new(1), second); Assert.Null(state.SelectedId);
        state.Enter(new(2), second); Assert.Null(state.SelectedId);
    }

    [Fact]
    public void DissolveAndUndoRestoreSelectionButDoNotSelectAnUnrelatedReplacement()
    {
        var a = new InstrumentChange(new(10), new(11), new(12), new(13));
        var original = InstrumentChangeSet.Empty.Add(a, true);
        var dissolved = original.Remove(a.Id);
        var state = new InstrumentChangeSelectionState();
        state.Enter(new(1), original); state.Select(a.Id);
        state.Enter(new(1), dissolved); Assert.Null(state.SelectedId);
        state.Enter(new(1), original); Assert.Equal(a.Id, state.SelectedId);
        state.Enter(new(1), InstrumentChangeSet.Empty.Add(a with { Id = new(20) }, true));
        Assert.Null(state.SelectedId);
    }
}
