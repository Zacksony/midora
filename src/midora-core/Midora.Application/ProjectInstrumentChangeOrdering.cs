using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    // Insert three explicit MIDI messages before the old same-tick stream.
    // Order is a source value, never an inference from Stable ID allocation.
    // All patches use bounded stores; even a dense tick does not materialize
    // an array of notes, messages or payloads.
    private static IProjectEditCommand ReserveInstrumentChangeOrder(MidoraId segmentId, long tick) =>
        ReserveInstrumentChangeOrders(segmentId, [tick]);

    private static IProjectEditCommand ReserveInstrumentChangeOrders(MidoraId segmentId, IEnumerable<long> tickValues) =>
        Command("Reserve instrument change order", original =>
        {
        using var orderScope = BulkEditPreparationContext.Enter(project: original);
        using var ticks = BoundedEditSort.Sort(tickValues, Comparer<long>.Default, orderScope.Resources, orderScope.Token);
        IEnumerable<long> Ticks()
        {
            long previous = -1;
            foreach (long tick in ticks)
            {
                orderScope.Token.ThrowIfCancellationRequested();
                if (tick < 0 || tick == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(tickValues));
                if (tick != previous) yield return tick;
                previous = tick;
            }
        }
        bool Contains(long tick)
        {
            int lo = 0, hi = ticks.Count;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (ticks[mid] < tick) lo = mid + 1; else hi = mid; }
            return lo < ticks.Count && ticks[lo] == tick;
        }
        var originalOwner = FindMidiSegment(original, segmentId).Segment;
        // Only the exceptional Int64 order boundary needs an order-rank map.
        // Ranking equal orders equally also preserves every existing tie-break.
        bool rebase = Orders().Any(value => value > long.MaxValue - 3);
        using var sorted = rebase ? BoundedEditSort.Sort(Orders(), Comparer<long>.Default, orderScope.Resources, orderScope.Token) : null;
        using var ranks = sorted is null ? null : new BoundedImmutableValueSource<long>(sorted);
        long Shift(long value)
        {
            if (ranks is null) return checked(value + 3);
            int lo = 0, hi = ranks.Count;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; if (ranks[mid] < value) lo = mid + 1; else hi = mid; }
            return lo + 3L;
        }
        IEnumerable<long> Orders()
        {
            var notes = originalOwner.Notes.CreateQuerySnapshot();
            foreach (var value in Ticks().SelectMany(tick => notes.QueryStartValues(tick, tick + 1))) { orderScope.Token.ThrowIfCancellationRequested(); yield return value.NoteOnOrder; }
            foreach (var value in Ticks().SelectMany(tick => notes.QueryEndValues(tick, tick + 1))) { orderScope.Token.ThrowIfCancellationRequested(); yield return value.NoteOffOrder; }
            foreach (var value in Ticks().SelectMany(tick => originalOwner.ChannelEvents.CreateQuerySnapshot().QueryValues(tick, tick + 1)))
            { orderScope.Token.ThrowIfCancellationRequested(); yield return value.Order; }
            foreach (var value in Ticks().SelectMany(tick => originalOwner.OpaqueEvents.CreateQuerySnapshot().QueryValues(tick, tick + 1)))
            { orderScope.Token.ThrowIfCancellationRequested(); yield return value.Order; }
        }
        return new SequentialProjectEditCommand("Reserve instrument change order",
        [
            _ => Command("Rebase note order", project =>
            {
                using var scope = BulkEditPreparationContext.Enter(project: project);
                var location = FindMidiSegment(project, segmentId);
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
                var source = BoundedDirectMidiNoteSource.Capture(location.Segment.Notes);
                using var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiNoteSource.OrdinalComparer,
                    scope.Resources, scope.Token);
                if (store.Count == 0) return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
                using var plan = new BoundedImmutableValueSource<BoundedDirectNoteDelta>(store);
                return PrepareBoundedDirectMidiNotes(project, location, source, plan, scope,
                    resolveCollisions: false, expectedSourceStamp: stamp);
                IEnumerable<BoundedDirectNoteDelta> Read()
                {
                    using var ordered = BoundedEditSort.Sort(Ticks().SelectMany(tick => source.QueryNoteStarts(tick, tick + 1)
                        .Concat(source.QueryNoteEnds(tick, tick + 1))),
                        Comparer<DirectMidiNoteValue>.Create(static (a, b) => a.Id.CompareTo(b.Id)), scope.Resources, scope.Token);
                    IEnumerable<DirectMidiNoteValue> Unique()
                    {
                        MidoraId previous = default;
                        foreach (var value in ordered) { if (value.Id != previous) yield return value; previous = value.Id; }
                    }
                    foreach (var item in source.ResolveValues(Unique(), scope.Token))
                    {
                        scope.Token.ThrowIfCancellationRequested();
                        var value = item.Value;
                        yield return item with { Value = value with
                        {
                            NoteOnOrder = Contains(value.StartTick) ? Shift(value.NoteOnOrder) : value.NoteOnOrder,
                            NoteOffOrder = Contains(checked(value.StartTick + value.LengthTicks))
                                ? Shift(value.NoteOffOrder) : value.NoteOffOrder
                        }};
                    }
                }
            }),
            _ => Command("Rebase channel event order", project =>
            {
                using var scope = BulkEditPreparationContext.Enter(project: project);
                var location = FindMidiSegment(project, segmentId);
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
                var source = BoundedDirectMidiEventSource.Capture(location.Segment.ChannelEvents);
                using var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiEventSource.OrdinalComparer,
                    scope.Resources, scope.Token);
                if (store.Count == 0) return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
                using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
                return PublishBoundedDirectMidiEvents(project, location, source, plan, scope,
                    BoundedEventCollisionMode.None, expectedSourceStamp: stamp);
                IEnumerable<BoundedDirectEventDelta> Read()
                {
                    foreach (var item in source.ResolveValues(Ticks().SelectMany(tick => source.QueryChannelEvents(tick, checked(tick + 1))), scope.Token))
                        yield return item with { Value = item.Value with { Order = Shift(item.Value.Order) } };
                }
            }),
            _ => Command("Rebase preserved event order", project =>
            {
                using var scope = BulkEditPreparationContext.Enter(project: project);
                var location = FindMidiSegment(project, segmentId);
                if (location.Segment.OpaqueEvents.Count == 0 || !Ticks().Any(tick => location.Segment.OpaqueEvents.CreateQuerySnapshot().QueryValues(tick, tick + 1).Any()))
                    return Prepared(false, PureMidiTrackChange(location.Track.Id), _ => { }, _ => { });
                var stamp = ProjectTimelineOwnerSourceStamp.Capture(location.Segment);
                var source = BoundedOpaqueMidiSource.Capture(project, location.Segment.OpaqueEvents);
                using var store = BoundedEditSort.Sort(Read(), BoundedDirectMidiEventSource.OrdinalComparer,
                    scope.Resources, scope.Token);
                using var plan = new BoundedImmutableValueSource<BoundedDirectEventDelta>(store);
                return PublishBoundedOpaqueRoot(project, location, source, plan, scope, null, source.Metadata.FormalExtent,
                    null, stamp);
                IEnumerable<BoundedDirectEventDelta> Read()
                {
                    foreach (var item in source.Metadata.ResolveValues(
                        Ticks().SelectMany(tick => source.Metadata.QueryChannelEvents(tick, checked(tick + 1))), scope.Token))
                        yield return item with { Value = item.Value with { Order = Shift(item.Value.Order) } };
                }
            })
        ]).Prepare(original);
        });
}
