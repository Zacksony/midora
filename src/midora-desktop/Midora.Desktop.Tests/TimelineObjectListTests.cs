using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Midora.Desktop.Presentation.Interaction;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Midora.Desktop.Tests;

[Collection(DesktopSharedPresentationStateCollection.Name)]
public sealed class TimelineObjectListTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LogicalRowsIncludeEveryLaneAndContentOutsideCropInDeterministicOrder()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project) { LengthTicks = 48 };
        LogicalNote late = new(project) { StartTick = 9000, LengthTicks = 10, Note = 1, Velocity = 90 };
        LogicalNote early = new(project) { StartTick = 7, LengthTicks = 4, Note = 70, Velocity = 110 };
        segment.Notes.Add(late); segment.Notes.Add(early);
        LogicalParameterLane lane = new(project) { ParameterId = new(90000) };
        CurvePoint point = new(project, 7, 20, CurveInterpolation.Step);
        lane.Points.Add(point); segment.ParameterLanes.Add(lane);
        LogicalParameterLane other = new(project) { ParameterId = new(90001) };
        other.Points.Add(new(project, 0, 99, CurveInterpolation.Step)); segment.ParameterLanes.Add(other);
        using var source = TimelineObjectListSource.CreateLogical(project, segment);
        Assert.False(source.IsActivated);
        var rows = await source.ReadRowsAsync(0, 4);
        Assert.Equal(new long[] { 0, 7, 7, 9000 }, rows.Select(value => value.Tick));
        Assert.Equal(early.Id, rows[1].Id);
        Assert.Equal(point.Id, rows[2].Id);
        Assert.Equal(lane.Id, rows[2].LaneId);
        Assert.Equal(lane.ParameterId, rows[2].ParameterId);
        Assert.Equal(2, rows.Count(value => value.IsNote));
        segment.Notes.Clear(); lane.Points.Clear();
        Assert.Equal(rows, await source.ReadRowsAsync(0, 4));
    }

    [Fact]
    public async Task MidiNotesAreSingleRowsAndOpaquePayloadIsOnlySummarized()
    {
        using MidoraProject project = new(192);
        MidiSegment segment = new(project) { LengthTicks = 100 };
        DirectMidiNote note = new(project) { StartTick = 10, LengthTicks = 900, Key = 60, NoteOnVelocity = 100, NoteOffVelocity = 27 };
        segment.Notes.Add(note);
        segment.ChannelEvents.Add(new(project) { Tick = 10, Kind = DirectMidiChannelEventKind.PitchBend, Data1 = 3, Data2 = 65, Order = 2 });
        segment.ChannelEvents.Add(new(project) { Tick = 2, Kind = DirectMidiChannelEventKind.ProgramChange, Data1 = 4 });
        segment.OpaqueEvents.Add(new(project) { Tick = 10, Kind = OpaqueMidiEventKind.SystemExclusive, Payload = [0x41, 0x10, 0x42] });
        using var source = TimelineObjectListSource.CreateMidi(project, segment);
        var rows = await source.ReadRowsAsync(0, 4);
        Assert.Equal(4, source.Count);
        Assert.Single(rows, row => row.IsNote);
        Assert.Equal(note.Id, rows[1].Id);
        Assert.Equal(8323, rows[2].Value);
        Assert.Equal(new DirectMidiEventLaneTarget(DirectMidiChannelEventKind.PitchBend, 0), rows[2].DirectMidiTarget);
        Assert.True(rows[3].IsOpaque);
        Assert.Equal(3, rows[3].PayloadLength);
        Assert.Equal("Program 4", TimelineObjectListSource.GetValueLabel(rows[0]));
        Assert.DoesNotContain("Id", TimelineObjectListSource.GetValueLabel(rows[3]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubVoiceKeepsBankPresenceAndUsesExistingLsbForNavigation()
    {
        using MidoraProject project = new(192);
        SubVoice voice = new(project);
        voice.Events.Add(new(project) { Kind = TemplateEventKind.Note, Tick = 50, LengthTicks = 17, Number = 62, Value = 90 });
        voice.Events.Add(TemplateEvent.Bank(project, 1, null, 12));
        voice.Events.Add(new(project) { Kind = TemplateEventKind.PitchBendRange, Tick = 2, Value = 12, SecondaryValue = 50 });
        using var source = TimelineObjectListSource.CreateSubVoice(project, voice, new(123));
        var rows = await source.ReadRowsAsync(0, 3);
        Assert.Equal(TimelineObjectListOwnerKind.SubVoice, source.OwnerKind);
        Assert.Equal(new MidoraId(123), source.InstrumentId);
        Assert.False(rows[0].HasBankMsb); Assert.True(rows[0].HasBankLsb);
        Assert.Equal(TemplateEventMappingParameter.SecondaryValue, rows[0].MidiTarget!.Value.Parameter);
        Assert.Contains("LSB 12", TimelineObjectListSource.GetValueLabel(rows[0]), StringComparison.Ordinal);
        Assert.Equal("12 semitones · 50 cents", TimelineObjectListSource.GetValueLabel(rows[1]));
        Assert.Equal(TimelineItemKind.TemplateNote, rows[2].Kind);
    }

    [Fact]
    public async Task FirstPageDoesNotWaitForFullExternalSortAndMatchesItExactly()
    {
        using MidoraProject project = new(192);
        using ManualResetEventSlim sortStarted = new(), release = new();
        const int count = 9000;
        SyntheticSource values = new(count, i => (i * 117L) % 7819);
        values.OnPage = first =>
        {
            if (first == 0 && values.Passes > 1) { sortStarted.Set(); release.Wait(TimeSpan.FromSeconds(20)); }
        };
        using var source = new TimelineObjectListSource(project, values);
        var rows = await source.ReadRowsAsync(0, 80).WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(80, rows.Length);
            Assert.True(await Task.Run(() => sortStarted.Wait(TimeSpan.FromSeconds(10))));
            Assert.False(source.DirectoryReady);
        }
        finally { release.Set(); }
        await source.PrepareAsync();
        Assert.Equal(rows, await source.ReadRowsAsync(0, 80));
        Assert.Equal(0, source.RetainedScalarBytes);
        Assert.True(source.DirectorySpillBytes > 0);
    }

    [Fact]
    public async Task PrefixFirstPageUsesOnlyNearbyCandidatesBeforeBackgroundDirectoryStarts()
    {
        using MidoraProject project = new(192);
        using ManualResetEventSlim directoryStarted = new(), release = new();
        SyntheticSource values = new(1_000_000, static i => i);
        values.OnPage = _ => { directoryStarted.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
        using var source = new TimelineObjectListSource(project, values, useRangeCandidates: true);
        try
        {
            var rows = await source.ReadRowsAsync(0, 40).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, rows[0].Tick); Assert.Equal(39, rows[^1].Tick);
            Assert.InRange(values.RangeReadCount, 256, 300);
            Assert.Equal(0, values.ReadCount);
            Assert.True(await Task.Run(() => directoryStarted.Wait(TimeSpan.FromSeconds(5))));
        }
        finally { source.Cancel(); release.Set(); }
    }

    [Fact]
    public async Task SortedPageBoundariesAndMillionRangeUseOnlyScalarDiskDirectory()
    {
        using MidoraProject project = new(192);
        const int count = 1_000_000;
        SyntheticSource values = new(count, i => count - i);
        using var source = new TimelineObjectListSource(project, values);
        Stopwatch timer = Stopwatch.StartNew();
        await source.PrepareAsync();
        double prepared = timer.Elapsed.TotalMilliseconds;
        int before = values.ReadCount;
        timer.Restart();
        var page = await source.ReadRowsAsync(899_999, 40);
        Assert.Equal(900_000, page[0].Tick);
        Assert.Equal(before, values.ReadCount);
        Assert.Equal(0, source.RetainedScalarBytes);
        Assert.InRange(source.DirectorySpillBytes, count * 32L, count * 256L);
        var ids = await source.ReadSelectionAsync(0, count - 1);
        Assert.Equal(count, ids.Count);
        Assert.Contains(new MidoraId(1), ids);
        Assert.Contains(new MidoraId(count), ids);
        output.WriteLine($"objects_million directory_ms={prepared:F2}; distant_page_and_selection_ms={timer.Elapsed.TotalMilliseconds:F2}; retained_scalars={source.RetainedScalarBytes}; spill={source.DirectorySpillBytes}; source_reads={values.ReadCount}");
    }

    [Fact]
    public async Task CancelledBeforeActivationDoesNotReadOrBuildAndCanBeRetried()
    {
        using MidoraProject project = new(192);
        SyntheticSource values = new(2000, static i => i);
        using var source = new TimelineObjectListSource(project, values);
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadRowsAsync(0, 10, cancel.Token));
        Assert.Equal(0, values.ReadCount); Assert.False(source.IsActivated);
        Assert.Equal(10, (await source.ReadRowsAsync(0, 10)).Length);
    }

    [Fact]
    public async Task DisposeCancelsInFlightFirstPageAndPreventsAnyLaterPublication()
    {
        using MidoraProject project = new(192);
        using ManualResetEventSlim started = new(), release = new();
        SyntheticSource values = new(20_000, static i => i);
        values.OnPage = first => { if (first == 0) { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); } };
        using var source = new TimelineObjectListSource(project, values);
        Task read = source.ReadRowsAsync(0, 10);
        Assert.True(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))));
        source.Dispose(); release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(source.IsDisposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadSelectionAsync(0, 10));
    }

    [Fact]
    public void ContentIdentityIgnoresSelectionButDetectsNoteAndLaneChangesWithoutEnumeratingRows()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project);
        LogicalNote note = new(project) { StartTick = 1, LengthTicks = 10 };
        segment.Notes.Add(note);
        using var first = TimelineObjectListSource.CreateLogical(project, segment);
        using var same = TimelineObjectListSource.CreateLogical(project, segment);
        Assert.True(first.IsSameContent(same));
        note.Velocity++;
        using var changed = TimelineObjectListSource.CreateLogical(project, segment);
        Assert.False(first.IsSameContent(changed));
        Assert.False(first.IsActivated); Assert.False(same.IsActivated); Assert.False(changed.IsActivated);
    }

    [Fact]
    public void SelectedSubsetStreamIncludesAllKindsWithoutActivatingListDirectory()
    {
        using MidoraProject project = new(192);
        Segment segment = new(project);
        LogicalNote note = new(project) { StartTick = 5, LengthTicks = 10 };
        segment.Notes.Add(note);
        LogicalParameterLane lane = new(project) { ParameterId = new(88) };
        CurvePoint point = new(project, 2, 7, CurveInterpolation.Step); lane.Points.Add(point); segment.ParameterLanes.Add(lane);
        using var source = TimelineObjectListSource.CreateLogical(project, segment);
        var rows = source.EnumerateSelectedRows(CompressedMidoraIdSet.Create([note.Id, point.Id])).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.IsNote);
        Assert.Single(rows, row => row.Kind == TimelineItemKind.LogicalParameterPoint);
        Assert.False(source.IsActivated);
    }

    [Fact]
    public void InactivePaneDoesNotStartAnyReadsAndUsesItemBasedScrolling()
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            SyntheticSource values = new(1000, static i => i);
            using var source = new TimelineObjectListSource(project, values);
            TimelineObjectListPane pane = new() { Source = source };
            pane.Measure(new Size(420, 300)); pane.Arrange(new Rect(0, 0, 420, 300)); pane.UpdateLayout();
            Assert.False(source.IsActivated); Assert.Equal(0, values.ReadCount);
            Assert.False(pane.IsActive);
            Assert.Equal(2, pane.Children.Count);
            var scroll = Assert.IsType<System.Windows.Controls.Primitives.ScrollBar>(pane.Children[1]);
            Assert.Equal(1, scroll.SmallChange);
            Assert.False(InputMethod.GetIsInputMethodEnabled(pane));
            Assert.Equal(990, scroll.Maximum); // Overscan must not hide the final actual row.
            pane.FirstRow = 40;
            pane.Source = null;
            Assert.Equal(40, pane.FirstRow); // Hide/rebind preserves workspace scroll state.
        });
    }

    [Fact]
    public void EmptyListTailIsDistinctFromColdUnloadedRowsAndScrollbar()
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            using var small = new TimelineObjectListSource(project, new SyntheticSource(2, static i => i));
            using var large = new TimelineObjectListSource(project, new SyntheticSource(1000, static i => i));
            TimelineObjectListPane pane = new() { Source = small };
            pane.Measure(new(420, 300)); pane.Arrange(new(0, 0, 420, 300)); pane.UpdateLayout();
            Assert.False(pane.IsTrueEmptySpace(new Point(20, 10))); // An existing, still cold row.
            Assert.True(pane.IsTrueEmptySpace(new Point(20, 80))); // Below the last actual row.
            Assert.False(pane.IsTrueEmptySpace(new Point(415, 80))); // Scrollbar column.
            Assert.False(pane.IsTrueEmptySpace(new Point(20, -1)));
            pane.Source = large;
            Assert.False(pane.IsTrueEmptySpace(new Point(20, 80))); // Cold is not empty.
            Assert.False(small.IsActivated); Assert.False(large.IsActivated);
        });
    }

    [Fact]
    public void ColdRowContextPropertiesAndSelectionResolveTheExactOrdinalWithoutLoadingControls()
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            using var source = new TimelineObjectListSource(project, new SyntheticSource(20, static i => 20 - i));
            TimelineObjectListPane pane = new() { Source = source, IsActive = true };
            pane.Measure(new(420, 300)); pane.Arrange(new(0, 0, 420, 300)); pane.UpdateLayout();
            TimelineObjectListContextEventArgs? context = null;
            TimelineObjectListSelectionEventArgs? selected = null;
            TimelineObjectListRow? property = null;
            pane.ContextRequested += (_, value) => context = value;
            pane.SelectionRequested += (_, value) => selected = value;
            pane.PropertiesRequested += (_, value) => property = value;
            Assert.False(source.IsActivated);
            Pump(pane.RequestContextAsync(new(20, 80)));
            Assert.NotNull(context);
            Assert.Equal(2, context.Ordinal);
            Assert.Equal(3, context.Row!.Value.Tick);
            Pump(pane.RequestPropertiesAsync(5));
            Assert.Equal(6, property!.Value.Tick);
            Pump(pane.RequestSelectionAsync(1, 8, ModifierKeys.Control));
            Assert.NotNull(selected);
            Assert.Equal(1, selected.First); Assert.Equal(8, selected.Last);
            Assert.Equal(9, selected.Primary!.Value.Tick);
            Assert.Equal(ModifierKeys.Control, selected.Modifiers);
            Assert.Equal(2, pane.Children.Count);
        });
    }

    [Theory]
    [InlineData(0)] // Owner source changed.
    [InlineData(1)] // A newer input superseded the pending context request.
    [InlineData(2)] // Selection changed elsewhere.
    [InlineData(3)] // List hidden/deactivated.
    public void ColdRowInteractionNeverPublishesAfterItsContextChanges(int change)
    {
        RunOnSta(() =>
        {
            using MidoraProject project = new(192);
            using ManualResetEventSlim started = new(), release = new();
            SyntheticSource values = new(20, static i => i);
            values.OnPage = _ => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
            using var source = new TimelineObjectListSource(project, values);
            using var replacement = new TimelineObjectListSource(project, new SyntheticSource(5, static i => i));
            TimelineObjectListPane pane = new() { Source = source, IsActive = true };
            pane.Measure(new(420, 300)); pane.Arrange(new(0, 0, 420, 300)); pane.UpdateLayout();
            int contexts = 0, selections = 0;
            pane.ContextRequested += (_, _) => contexts++;
            pane.SelectionRequested += (_, _) => selections++;
            Task pending = pane.RequestContextAsync(new(20, 10));
            Task replacementInput = Task.CompletedTask;
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                if (change == 0) pane.Source = replacement;
                else if (change == 1) replacementInput = pane.RequestSelectionAsync(1, 1, ModifierKeys.None);
                else if (change == 2) pane.Selection = new(9, [new MidoraId(3)], new MidoraId(3));
                else pane.IsActive = false;
            }
            finally { release.Set(); }
            Pump(Task.WhenAll(pending, replacementInput));
            Assert.Equal(0, contexts);
            Assert.Equal(change == 1 ? 1 : 0, selections);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundedPrefixCandidatesMatchFullDirectoryAfterValueAndPositionEdits(bool subVoice)
    {
        using MidoraProject project = new(192);
        Segment segment = new(project);
        SubVoice voice = new(project);
        for (int i = 999; i >= 0; i--)
            if (subVoice) voice.Events.Add(new(project) { Tick = i % 97, Kind = i % 2 == 0 ? TemplateEventKind.Note : TemplateEventKind.ControlChange,
                LengthTicks = 10, Number = i % 2 == 0 ? i % 128 : 11, Value = i % 128 });
            else segment.Notes.Add(new(project) { StartTick = i % 97, LengthTicks = 10, Note = i % 128, Velocity = i % 127 + 1 });
        if (subVoice) { voice.Events[0].Tick = 100000; voice.Events[1].Value = 50; }
        else { segment.Notes[0].StartTick = 100000; segment.Notes[1].Velocity = 50; }
        using var source = subVoice ? TimelineObjectListSource.CreateSubVoice(project, voice) : TimelineObjectListSource.CreateLogical(project, segment);
        var first = await source.ReadRowsAsync(0, 256);
        await source.PrepareAsync();
        Assert.Equal(first, await source.ReadRowsAsync(0, 256));
        Assert.Equal(256, first.Length);
        Assert.True(first.Zip(first.Skip(1), (left, right) => TimelineObjectListSource.RowComparer.Instance.Compare(left, right) <= 0).All(value => value));
    }

    private sealed class SyntheticSource(int count, Func<int, long> tick) : ITimelineObjectSource<LogicalNoteSnapshotValue>
    {
        private int _reads, _passes, _rangeReads;
        public int Count => count;
        public long SourceRevision => 1;
        public int PageCapacity => 256;
        public int ReadCount => Volatile.Read(ref _reads);
        public int Passes => Volatile.Read(ref _passes);
        public int RangeReadCount => Volatile.Read(ref _rangeReads);
        public Action<int>? OnPage { get; set; }
        public bool TryGetPageByOrdinal(int first, int length, out TimelineObjectPage<LogicalNoteSnapshotValue> page)
        {
            if (first == 0) Interlocked.Increment(ref _passes);
            OnPage?.Invoke(first);
            int actual = Math.Min(length, Count - first);
            if (actual <= 0) { page = default; return false; }
            var values = new LogicalNoteSnapshotValue[actual];
            for (int i = 0; i < actual; i++) values[i] = new(new(first + i + 1), tick(first + i), 10, (first + i) % 128, 100);
            Interlocked.Add(ref _reads, actual);
            page = new(SourceRevision, first, values); return true;
        }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal) { ordinal = checked((int)id.Value - 1); return ordinal >= 0 && ordinal < Count; }
        public int FindOrdinalAtOrAfterTick(long value) => throw new NotSupportedException();
        public IEnumerable<LogicalNoteSnapshotValue> QueryTickRange(TimelineObjectRangeQuery query)
        {
            // This optional test path represents an indexed, monotonic source.
            for (int i = checked((int)query.StartTick); i < Math.Min(query.EndTick, Count); i++)
            {
                Interlocked.Increment(ref _rangeReads);
                yield return new(new(i + 1), tick(i), 10, i % 128, 100);
            }
        }
        public void Prefetch(TimelineObjectRangeQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
            }
            catch (Exception e) { failure = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Object list STA test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            DispatcherFrame frame = new();
            Stopwatch timer = Stopwatch.StartNew();
            DispatcherTimer poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
            poll.Tick += (_, _) => { if (task.IsCompleted || timer.Elapsed > TimeSpan.FromSeconds(10)) frame.Continue = false; };
            poll.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { poll.Stop(); }
        }
        Assert.True(task.IsCompleted, "The asynchronous row interaction did not finish.");
        task.GetAwaiter().GetResult();
    }
}
