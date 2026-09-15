using Midora.Domain;

namespace Midora.Application.Tests;

public sealed class BoundedOrdinalLookupTests
{
    [Theory]
    [InlineData("logical", false)]
    [InlineData("logical", true)]
    [InlineData("template", false)]
    [InlineData("template", true)]
    [InlineData("curve", false)]
    [InlineData("curve", true)]
    public void ShuffledFormalIdsUseOneIndependentBoundedIndexAndSurviveCanceledEdit(string kind, bool warm)
    {
        using MidoraProject project = new(480);
        using var context = BulkEditPreparationContext.Enter(project: project);
        const int count = 8_192;
        // Interleave the highest and lowest IDs in every formal page. A tree
        // indexed by formal-page ID min/max cannot prune this input safely.
        MidoraId Id(int ordinal) => new(1_000 + (ordinal % 2 == 0 ? ordinal / 2 : count - 1 - ordinal / 2));
        var builder = new CountingBuilder();
        if (kind == "logical")
        {
            Segment segment = new(project);
            segment.Notes.AddRange(Enumerable.Range(0, count).Select(i => new LogicalNote(project, Id(i))
                { StartTick = i * 8L, LengthTicks = 3, Note = i % 128 }));
            Verify(segment.Notes.CreateQuerySnapshot());
        }
        else if (kind == "template")
        {
            SubVoice voice = new(project);
            voice.Events.AddRange(Enumerable.Range(0, count).Select(i => new TemplateEvent(project, Id(i))
                { Kind = TemplateEventKind.Note, Tick = i * 8L, LengthTicks = 3, Number = i % 128, Value = 100 }));
            Verify(voice.Events.CreateQuerySnapshot());
        }
        else
        {
            LogicalParameterLane lane = new(project);
            lane.Points.AddRange(Enumerable.Range(0, count).Select(i => new CurvePoint(project, Id(i), i * 8L, 80, CurveInterpolation.Step)));
            Verify(lane.Points.CreateQuerySnapshot());
        }

        void Verify<T>(ITimelineObjectSource<T> source)
        {
            if (warm) source.PrepareOrdinalLookup();
            using (var failedEditLease = context.Resources.BeginResourceLease())
            {
                source.PrepareOrdinalLookup(builder);
                source.PrepareOrdinalLookup(builder);
                Assert.Equal(1, builder.BuildCount);
                Assert.Equal(count, builder.RecordCount);
                // No Complete/MarkPublished: simulate failure after the reusable
                // source cache was built but before any edit root was published.
            }
            for (int i = count - 1; i >= 0; i--)
            {
                Assert.True(source.TryFindOrdinalById(Id(i), out int actual));
                Assert.Equal(i, actual);
            }
            Assert.Equal(count, builder.FindCount);
            Assert.Equal(0, context.Resources.ResidentBytes);
        }
    }

    [Fact]
    public void CancellationWhileBuildingTheAddressIndexLeavesTheSourceUsable()
    {
        using MidoraProject project = new(480);
        using var context = BulkEditPreparationContext.Enter(project: project);
        Segment segment = new(project);
        segment.Notes.AddRange(Enumerable.Range(0, 8_192).Select(i => new LogicalNote(project)
            { StartTick = i * 8L, LengthTicks = 3 }));
        var source = segment.Notes.CreateQuerySnapshot();
        MidoraId first = source.GetByOrdinal(0).Id;
        using var cancellation = new CancellationTokenSource();
        using (var failed = context.Resources.BeginResourceLease())
            Assert.Throws<OperationCanceledException>(() => source.PrepareOrdinalLookup(new CancelingBuilder(cancellation), cancellation.Token));
        Assert.Equal(0, context.Resources.ResidentBytes);
        Assert.Equal(0, context.Resources.SpillBytes);
        Assert.True(source.TryFindOrdinalById(first, out int ordinal));
        Assert.Equal(0, ordinal);
    }

    private sealed class CountingBuilder : IImmutableTimelineOrdinalIndexBuilder
    {
        public int BuildCount, RecordCount, FindCount;
        public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token)
        {
            BuildCount++;
            var index = BoundedTimelineOrdinalIndexBuilder.Instance.CreateOrdinalIndex(Count(), token);
            return new Index(this, index);
            IEnumerable<TimelineIdOrdinal> Count()
            { foreach (var value in values) { RecordCount++; yield return value; } }
        }
        private sealed class Index(CountingBuilder owner, IImmutableTimelineIdIndex index) : IImmutableTimelineIdIndex
        {
            public bool TryFindOrdinalById(MidoraId id, out int ordinal)
            { owner.FindCount++; return index.TryFindOrdinalById(id, out ordinal); }
            public void RetainForSourceLifetime() => index.RetainForSourceLifetime();
        }
    }

    private sealed class CancelingBuilder(CancellationTokenSource cancellation) : IImmutableTimelineOrdinalIndexBuilder
    {
        public IImmutableTimelineIdIndex CreateOrdinalIndex(IEnumerable<TimelineIdOrdinal> values, CancellationToken token) =>
            BoundedTimelineOrdinalIndexBuilder.Instance.CreateOrdinalIndex(CancelMidway(values), token);
        private IEnumerable<TimelineIdOrdinal> CancelMidway(IEnumerable<TimelineIdOrdinal> values)
        {
            int count = 0;
            foreach (var value in values)
            { if (++count == 1_000) cancellation.Cancel(); yield return value; }
        }
    }
}
