using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class LaneTabSessionTests
{
    [Fact]
    public void HideReorderAndPassiveRefreshDoNotResurrectAClosedLane()
    {
        var state = new LaneTabSession();
        var a = new LaneTabKey(3, Kind: (int)DirectMidiChannelEventKind.ControlChange, Number: 7);
        var b = a with { Number = 11 };
        LaneTabDescriptor[] rows = [new(LaneTabKey.Velocity, "Vel.", 10), new(LaneTabKey.Instrument, "Inst.", 0), new(a, "Volume", 1), new(b, "Expression", 0, "Mapping")];
        state.Sync(rows); state.Show(a); state.Hide(a); state.Sync(rows);
        Assert.Equal(LaneTabKey.Velocity, state.Active);
        Assert.DoesNotContain(state.Visible(), row => row.Key == a);
        state.Show(a); state.Move(b, a);
        Assert.Equal(new[] { LaneTabKey.Velocity, LaneTabKey.Instrument, b, a }, state.Visible().Select(row => row.Key));
        state.Move(b, a, after: true);
        Assert.Equal(new[] { LaneTabKey.Velocity, LaneTabKey.Instrument, a, b }, state.Visible().Select(row => row.Key));
        state.Hide(LaneTabKey.Instrument); Assert.True(state.IsVisible(LaneTabKey.Instrument));
        state.SaveAxis(a, (.2, .6)); state.Show(b);
        Assert.Equal((.2, .6), state.Axis(a)); Assert.Equal((0d, 1d), state.Axis(b));
        state.Sync(rows.Where(row => row.Key != a).ToArray());
        Assert.DoesNotContain(state.Visible(), row => row.Key == a);
    }
}
