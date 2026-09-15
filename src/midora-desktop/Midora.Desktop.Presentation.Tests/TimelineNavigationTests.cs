using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Controls;

namespace Midora.Desktop.Presentation.Tests;

public sealed class TimelineNavigationTests
{
    [Theory]
    [InlineData(0L, 600L, 300L)]
    [InlineData(1000L, 600L, 1300L)]
    [InlineData(long.MaxValue - 600, 600L, long.MaxValue - 300)]
    [InlineData(long.MaxValue - 400, 600L, long.MaxValue - 200)]
    public void ReadOnlyNavigationUsesAbsoluteTicksWithoutSnapOrSourceQueries(long start, long span, long expected)
    {
        Sta(() =>
        {
            TimelineSurface surface = new()
            {
                SurfaceMode = TimelineSurfaceMode.PianoRoll, TickSpan = span,
                LaneHeight = 15, CanEdit = false, IsTimeRangeSelectionEnabled = false
            };
            // No source snapshot at all: coordinates must not depend on rendered/hit-testable notes.
            surface.Measure(new(852, 500)); surface.Arrange(new Rect(0, 0, 852, 500));
            // Test coordinate conversion at int64 boundaries independently from
            // the existing ruler/grid renderer's own supported arithmetic range.
            surface.StartTick = start;
            Assert.True(surface.TryGetNavigationTick(new(452, 8), out long ruler));
            Assert.True(surface.TryGetNavigationTick(new(452, 90), out long content));
            Assert.Equal(expected, ruler); Assert.Equal(ruler, content);
            Assert.False(surface.TryGetNavigationTick(new(51, 90), out _));
            Assert.False(surface.TryGetNavigationTick(new(852, 90), out _));
            Assert.False(surface.TryGetNavigationTick(new(452, 500), out _));
            Assert.False(surface.TryGetNavigationTick(new(double.PositiveInfinity, 90), out _));
        });
    }

    private static void Sta(Action action)
    {
        Exception? error = null;
        Thread thread = new(() =>
        {
            try { action(); } catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
