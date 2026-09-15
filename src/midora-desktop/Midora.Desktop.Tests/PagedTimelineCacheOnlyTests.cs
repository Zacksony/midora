using System.Collections;
using Midora.Desktop.Presentation.Rendering;
using Midora.Domain;
using Xunit;

namespace Midora.Desktop.Tests;

public sealed class PagedTimelineCacheOnlyTests
{
    [Theory]
    [InlineData("logical")]
    [InlineData("logical-velocity")]
    [InlineData("template")]
    [InlineData("template-velocity")]
    [InlineData("parameter")]
    [InlineData("event")]
    public void ColdPagesArePendingWithoutReadingAndBecomeReadyAfterPrefetch(string kind)
    {
        using MidoraProject project = new(480);
        ITimelineRenderItemSource source;
        Action clearCache;
        Func<int> reads;
        var ids = Enumerable.Range(0, 1_024).Select(i => new MidoraId(1_000 + i)).ToArray();
        if (kind.StartsWith("logical", StringComparison.Ordinal))
        {
            var values = new ColdSource<LogicalNoteSnapshotValue>(ids.Select((id, i) =>
                new LogicalNoteSnapshotValue(id, i * 8L, 3, 60, 80)).ToArray(), v => v.Id);
            Segment segment = new(project) { LengthTicks = 10_000 };
            segment.Notes.AdoptSource(project, values);
            source = new PagedLogicalNoteTimelineItemSource(segment,
                kind == "logical" ? LogicalNoteTimelineProjection.Notes : LogicalNoteTimelineProjection.Velocities);
            clearCache = values.ClearCache; reads = () => values.Reads;
        }
        else if (kind == "parameter")
        {
            var values = new ColdSource<CurvePointSnapshotValue>(ids.Select((id, i) =>
                new CurvePointSnapshotValue(id, i * 8L, 80, CurveInterpolation.Step)).ToArray(), v => v.Id);
            LogicalParameterLane lane = new(project);
            lane.Points.AdoptSource(project, values);
            source = new PagedLogicalParameterTimelineItemSource(lane, lane.Points.CreateQuerySnapshot(),
                0, 127, false, 0, 10_000);
            clearCache = values.ClearCache; reads = () => values.Reads;
        }
        else
        {
            bool events = kind == "event";
            var values = new ColdSource<TemplateEventSnapshotValue>(ids.Select((id, i) =>
                new TemplateEventSnapshotValue(id, events ? TemplateEventKind.ControlChange : TemplateEventKind.Note,
                    i * 8L, events ? 0 : 3, events ? 11 : 60, 80, 0, false, false, false)).ToArray(), v => v.Id);
            SubVoice voice = new(project);
            voice.Events.AdoptSource(project, values);
            source = events
                ? new PagedTemplateEventLaneTimelineItemSource(voice, voice.Events.CreateQuerySnapshot(), MidiValueTarget.ControlChange(11))
                : new PagedTemplateNoteTimelineItemSource(voice,
                    kind == "template" ? TemplateNoteTimelineProjection.Notes : TemplateNoteTimelineProjection.Velocities);
            clearCache = values.ClearCache; reads = () => values.Reads;
        }

        IPreparedTimelineRenderItemSource prepared = Assert.IsAssignableFrom<IPreparedTimelineRenderItemSource>(source);
        clearCache();
        int before = reads();
        Assert.Empty(TimelinePresentationPaging.Adapt(source).Items);
        Assert.Equal(before, reads());
        List<TimelineRenderItem> result = [default];
        Assert.False(prepared.TryQueryIntoCached(16, 48, 0, 128, result));
        Assert.Single(result);
        Assert.Equal(before, reads());
        prepared.PrefetchRange(16, 48, 0, 128, default);
        before = reads();
        Assert.True(prepared.TryQueryIntoCached(16, 48, 0, 128, result));
        Assert.Equal(ids.Skip(2).Take(4), result.Skip(1).Select(v => v.Id));
        Assert.Equal(before, reads());

        // A partially satisfied multi-page/ID query must not publish a partial
        // selection as though the missing cold page were empty.
        result.Clear();
        result.Add(default);
        Assert.False(prepared.TryQueryByIdsCached(new HashSet<MidoraId> { ids[3], ids[700] }, result));
        Assert.Single(result);
        Assert.Equal(before, reads());

        clearCache();
        result.Clear();
        var selected = new HashSet<MidoraId> { ids[700], ids[701] };
        before = reads();
        Assert.False(prepared.TryQueryByIdsCached(selected, result));
        Assert.Empty(result);
        Assert.Equal(before, reads());
        prepared.PrefetchIds(selected, default);
        before = reads();
        Assert.True(prepared.TryQueryByIdsCached(selected, result));
        Assert.Equal(selected.Order(), result.Select(v => v.Id).Order());
        Assert.Equal(before, reads());
        if (source is INonBlockingTimelineFingerprintSource metadata)
            Assert.False(metadata.CanComputeRangeFingerprintWithoutBlocking);
    }

    private sealed class ColdSource<T>(T[] values, Func<T, MidoraId> getId) : IIndexedImmutableTimelineValueSource<T>
    {
        private readonly HashSet<int> _cached = [];
        public int Count => values.Length;
        public int PageCapacity => 128;
        public int Reads { get; private set; }
        public void ClearCache() => _cached.Clear();
        public T this[int index] => ReadPage(index / PageCapacity).Span[index % PageCapacity];
        public ReadOnlyMemory<T> ReadPage(int pageIndex)
        {
            if (!_cached.Contains(pageIndex))
            {
                Assert.False(TimelineValueReadScope.IsCacheOnly);
                Reads++;
                _cached.Add(pageIndex);
            }
            return Page(pageIndex);
        }
        public bool TryReadCachedPage(int pageIndex, out ReadOnlyMemory<T> page)
        {
            page = _cached.Contains(pageIndex) ? Page(pageIndex) : default;
            return !page.IsEmpty;
        }
        public bool TryFindOrdinalById(MidoraId id, out int ordinal)
        {
            ordinal = Array.FindIndex(values, value => getId(value) == id);
            return ordinal >= 0;
        }
        private ReadOnlyMemory<T> Page(int pageIndex) => values.AsMemory(pageIndex * PageCapacity,
            Math.Min(PageCapacity, Count - pageIndex * PageCapacity));
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
