using Midora.Domain;

namespace Midora.Compiler.Tests;

public sealed class ExtremeTickGridTests
{
    [Theory]
    [InlineData(1, 1, 4)]
    [InlineData(192, 4, 4)]
    [InlineData(32767, 99, 1)]
    [InlineData(32752, 99, 64)]
    public void LastBarUsesWideEndWithoutChangingFormalTickDomain(int tpqn, int numerator, int denominator)
    {
        ProjectTimeSignatureMap map = new(tpqn, [new ProjectTimeSignaturePoint(new(1), 0, numerator, denominator)]);
        long length = (long)tpqn * 4 / denominator * numerator;
        foreach (long tick in new[] { 0L, length, long.MaxValue - length, long.MaxValue - 1, long.MaxValue })
        {
            ProjectBarBounds bar = map.GetBarBounds(tick);
            Assert.Equal(tick / length * length, bar.StartTick);
            Assert.Equal((Int128)bar.StartTick + length, bar.EndTick);
            Assert.Equal((ulong)(tick / length) + 1, bar.Bar);
            Assert.Equal(tick, map.GetTick(map.GetPosition(tick)));
            Assert.Equal(bar.EndTick <= long.MaxValue,
                ProjectTimelineGrid.TryGetNextGridTick(tick, 1, true, map, out _));
        }
        Assert.Throws<OverflowException>(() => map.GetBarContaining(long.MaxValue));
    }

    [Fact]
    public void SignatureChangeTruncatesAnOtherwiseUnrepresentableBarAndBeat()
    {
        long change = long.MaxValue - 3;
        ProjectTimeSignatureMap map = new(32767, [new ProjectTimeSignaturePoint(new(1), 0, 4, 4), new(new(2), change, 3, 4)]);
        Assert.Equal(change, map.GetBarContaining(change - 1).EndTick);
        Assert.Equal(change, map.GetBeatGridTickAtOrAfter(change - 1));
        Assert.Equal(change, map.SnapToNearestBeatGrid(change - 1));
        Assert.Equal(change, ProjectTimelineGrid.GetGridTickAtOrAfter(change - 1, 1, true, map));
        Assert.True(map.GetBarBounds(change).EndTick > long.MaxValue);
    }

    [Fact]
    public void AnUnselectedUnrepresentableUpperCandidateDoesNotRejectLowerSnap()
    {
        ProjectTimeSignatureMap map = new(192, [new ProjectTimeSignaturePoint(new(1), 0, 4, 4)]);
        long start = map.GetBarBounds(long.MaxValue).StartTick;
        Assert.Equal(start, ProjectTimelineGrid.SnapAbsolute(start + 1, 1, true, map, 0));
        Assert.Equal(start, map.SnapToNearestBeatGrid(start + 1));
        Assert.Throws<OverflowException>(() => ProjectTimelineGrid.SnapAbsolute(long.MaxValue, 1, true, map, 1));
        Assert.False(ProjectTimelineGrid.TryGetGridTickAtOrAfter(long.MaxValue, 192, false, null, out _));
        Assert.Throws<OverflowException>(() => ProjectTimelineGrid.SnapRangeFromAnchor(long.MaxValue, long.MaxValue, 1, false, null));
    }

    [Fact]
    public void FixedAndSignedSnapMatchWideIntegerOracle()
    {
        Random random = new(411);
        long[] steps = [1, 2, 3, 192, 32767, 131068, long.MaxValue];
        foreach (long step in steps)
        foreach (long delta in new[] { long.MinValue, long.MinValue + 1, -193, -96, -1, 0, 1, 96, 193, long.MaxValue - 1, long.MaxValue }
            .Concat(Enumerable.Range(0, 1000).Select(_ => random.NextInt64(long.MinValue, long.MaxValue))))
        {
            Int128 magnitude = Int128.Abs((Int128)delta);
            Int128 lower = magnitude / step * step, remainder = magnitude - lower;
            Int128 snapped = remainder * 2 > step || remainder * 2 == step && delta > 0 ? lower + step : lower;
            Int128 expected = delta < 0 ? -snapped : snapped;
            if (expected < long.MinValue || expected > long.MaxValue)
                Assert.Throws<OverflowException>(() => ProjectTimelineGrid.SnapDelta(delta, 0, step, false, null));
            else Assert.Equal((long)expected, ProjectTimelineGrid.SnapDelta(delta, 0, step, false, null));
        }
    }

    [Fact]
    public void NextGridHasNoWrapAndDistinguishesEndFromInvalidArguments()
    {
        Assert.True(ProjectTimelineGrid.TryGetNextGridTick(long.MaxValue - 1, 1, false, null, out long end));
        Assert.Equal(long.MaxValue, end);
        Assert.False(ProjectTimelineGrid.TryGetNextGridTick(end, 1, false, null, out _));
        Assert.Throws<OverflowException>(() => ProjectTimelineGrid.GetNextGridTick(end, 1, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectTimelineGrid.TryGetNextGridTick(-1, 1, false, null, out _));
        Assert.Equal(long.MaxValue, ProjectTimelineGrid.GetGridTickAtOrAfter(long.MaxValue, 1, false, null));
    }
}
