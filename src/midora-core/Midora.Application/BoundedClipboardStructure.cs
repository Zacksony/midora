using Midora.Domain;

namespace Midora.Application;

public static partial class ProjectDomainEditCommands
{
    private static void ValidateClipboardSegmentRanges(IEnumerable<TickRange> additions, IEnumerable<TickRange> existing)
    {
        TickRange[] ordered = additions.OrderBy(static value => value.StartTick).ToArray();
        TickRange[] occupied = existing.OrderBy(static value => value.StartTick).ToArray();
        int current = 0;
        for (int index = 0; index < ordered.Length; index++)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            TickRange range = ordered[index];
            if (index != 0 && ordered[index - 1].EndTick > range.StartTick)
                throw new InvalidOperationException("Pasted Segments would overlap each other.");
            while (current < occupied.Length && occupied[current].EndTick <= range.StartTick) current++;
            if (current < occupied.Length && range.Intersects(occupied[current]))
                throw new InvalidOperationException("Pasted Segments would overlap an existing Segment.");
        }
    }

    private static void MergeClipboardSegments<T>(List<T> target, IEnumerable<T> additions,
        Func<T, long> tick, Func<T, MidoraId> id)
    {
        // A sorted batch merged with the existing formal sequence is equivalent
        // to repeatedly inserting before its first greater StartTick/ID, without
        // the quadratic scans and shifts of List.Insert for a whole Track paste.
        T[] ordered = additions.OrderBy(tick).ThenBy(id).ToArray();
        List<T> merged = new(checked(target.Count + ordered.Length));
        int next = 0;
        foreach (T value in target)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            while (next < ordered.Length && (tick(ordered[next]) < tick(value)
                || tick(ordered[next]) == tick(value) && id(ordered[next]).CompareTo(id(value)) < 0))
                merged.Add(ordered[next++]);
            merged.Add(value);
        }
        while (next < ordered.Length)
        {
            BulkEditPreparationContext.Current!.Token.ThrowIfCancellationRequested();
            merged.Add(ordered[next++]);
        }
        target.Clear();
        target.AddRange(merged);
    }

    private static EventInstrument CopyBoundedEventInstrument(MidoraProject project, EventInstrument source,
        string? name = null)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        return EventInstrumentLibrary.CopyInto(project, source, name, CopyBoundedSubVoiceTimeline, scope.Token);
    }

    private static void CopyBoundedSubVoiceTimeline(MidoraProject project, SubVoice source, SubVoice target)
    {
        using var scope = BulkEditPreparationContext.Enter(BulkEditPreparationContext.Current?.Token ?? default, project: project);
        long firstEventId = project.NextStableId;
        var events = FreezeClipboardSource(FirstTemplateClipboardValues(source.Events.CreateQuerySnapshot().EnumerateAll()
            .Select(value => value with { Id = project.AllocateStableId() })), static value => value.Id);
        target.Events.AdoptSource(project, events, scope.Token);
        target.InstrumentChanges = InstrumentChangeCopies.Restore(project,
            InstrumentChangeCopies.Capture(source.InstrumentChanges, source.Events.CreateQuerySnapshot()), firstEventId, false,
            group => InstrumentChangeResolver.TryRead(target, group, out _));
        foreach (ValueCurve value in source.Curves)
        {
            scope.Token.ThrowIfCancellationRequested();
            ValueCurve curve = new(project) { Target = value.Target };
            curve.TargetSettings.Rounding = value.TargetSettings.Rounding;
            curve.TargetSettings.Overflow = value.TargetSettings.Overflow;
            var points = FreezeClipboardSource(FirstClipboardValues(value.Points.CreateQuerySnapshot().EnumerateAll()
                .Select(point => point with { Id = project.AllocateStableId() }),
                static point => point.Tick, static _ => 0), static point => point.Id);
            curve.Points.AdoptSource(project, points, scope.Token);
            target.Curves.Add(curve);
        }
    }

    private static BoundedIndexedTimelineValueSource<T> FreezeClipboardSource<T>(
        IEnumerable<T> values, Func<T, MidoraId> id) where T : unmanaged
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        var records = new BoundedEditRecordStore<T>(scope.Resources);
        BoundedImmutableValueSource<T>? source = null;
        try
        {
            records.AddRange(values, scope.Token);
            records.Seal();
            records.SpillResidentPages(scope.Token);
            source = new BoundedImmutableValueSource<T>(records);
            return new(source, id, scope.Resources, scope.Token);
        }
        catch { if (source is null) records.Dispose(); else source.Dispose(); throw; }
    }

    private readonly record struct ClipboardOrderedValue<T>(int Ordinal, T Value) where T : unmanaged;

    // Copying a complete subtree has historically retained the first formal
    // object at each exact key. Preserve that rule without an N-entry HashSet.
    private static IEnumerable<T> FirstClipboardValues<T>(IEnumerable<T> input,
        Func<T, long> tick, Func<T, int> key) where T : unmanaged
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        using var ordered = BoundedEditSort.Sort(input.Select((value, ordinal) => new ClipboardOrderedValue<T>(ordinal, value)),
            Comparer<ClipboardOrderedValue<T>>.Create((a, b) =>
            {
                int c = tick(a.Value).CompareTo(tick(b.Value));
                if (c == 0) c = key(a.Value).CompareTo(key(b.Value));
                return c == 0 ? a.Ordinal.CompareTo(b.Ordinal) : c;
            }), scope.Resources, scope.Token);
        using var formal = BoundedEditSort.Sort(Winners(),
            Comparer<ClipboardOrderedValue<T>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), scope.Resources, scope.Token);
        foreach (var row in formal) { scope.Token.ThrowIfCancellationRequested(); yield return row.Value; }
        IEnumerable<ClipboardOrderedValue<T>> Winners()
        {
            bool hasPrevious = false; long previousTick = 0; int previousKey = 0;
            foreach (var row in ordered)
            {
                scope.Token.ThrowIfCancellationRequested();
                long t = tick(row.Value); int k = key(row.Value);
                if (hasPrevious && previousTick == t && previousKey == k) continue;
                hasPrevious = true; previousTick = t; previousKey = k;
                yield return row;
            }
        }
    }

    private static IEnumerable<TemplateEventSnapshotValue> FirstTemplateClipboardValues(
        IEnumerable<TemplateEventSnapshotValue> input)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        using var ordered = BoundedEditSort.Sort(input.Select((value, ordinal) => new ClipboardOrderedValue<TemplateEventSnapshotValue>(ordinal, value)),
            Comparer<ClipboardOrderedValue<TemplateEventSnapshotValue>>.Create((a, b) =>
            {
                int c = a.Value.Tick.CompareTo(b.Value.Tick);
                return c == 0 ? a.Ordinal.CompareTo(b.Ordinal) : c;
            }), scope.Resources, scope.Token);
        using var formal = BoundedEditSort.Sort(Winners(),
            Comparer<ClipboardOrderedValue<TemplateEventSnapshotValue>>.Create((a, b) => a.Ordinal.CompareTo(b.Ordinal)), scope.Resources, scope.Token);
        foreach (var row in formal) { scope.Token.ThrowIfCancellationRequested(); yield return row.Value; }
        IEnumerable<ClipboardOrderedValue<TemplateEventSnapshotValue>> Winners()
        {
            // The domain has a fixed, validated target vocabulary. Epoch stamps
            // avoid clearing the table at every tick while keeping memory fixed.
            using IDisposable working = scope.Resources.ReserveWorking(65_536L * sizeof(int));
            int[] occupied = new int[65_536];
            int epoch = 0; long previousTick = -1;
            foreach (var row in ordered)
            {
                scope.Token.ThrowIfCancellationRequested();
                if (row.Value.Tick != previousTick) { previousTick = row.Value.Tick; epoch = checked(epoch + 1); }
                var details = row.Value.Kind == TemplateEventKind.Note
                    ? (First: 65_000 + row.Value.Number, Second: 0, Count: 1)
                    : TemplateEventExactCollision.Details(row.Value);
                if ((details.Count > 0 && occupied[details.First] == epoch)
                    || (details.Count > 1 && occupied[details.Second] == epoch)) continue;
                if (details.Count > 0) occupied[details.First] = epoch;
                if (details.Count > 1) occupied[details.Second] = epoch;
                yield return row;
            }
        }
    }
}
