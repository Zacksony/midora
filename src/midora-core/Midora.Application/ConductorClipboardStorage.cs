using System.Collections;

namespace Midora.Application;

/// <summary>Scalar conductor records plus a bounded UTF-16 text stream.</summary>
internal sealed class ConductorClipboardList : IReadOnlyList<ConductorEventClipboardSnapshot>, IDisposable
{
    private readonly BoundedEditRecordStore<Record> _records;
    private readonly BoundedEditRecordStore<char> _text;
    private readonly long _firstTick;
    private ConductorClipboardList(BoundedEditRecordStore<Record> records,
        BoundedEditRecordStore<char> text, long firstTick)
        => (_records, _text, _firstTick) = (records, text, firstTick);

    public static ConductorClipboardList Capture(IEnumerable<ConductorEventClipboardSnapshot> values)
    {
        BulkEditPreparationContext scope = BulkEditPreparationContext.Current!;
        var text = new BoundedEditRecordStore<char>(scope.Resources);
        BoundedEditRecordStore<Record>? ordered = null;
        try
        {
            ordered = BoundedEditSort.Sort(Records(), Comparer<Record>.Create((a, b) =>
            {
                int order = a.Tick.CompareTo(b.Tick);
                if (order == 0) order = a.Kind.CompareTo(b.Kind);
                return order == 0 ? a.Ordinal.CompareTo(b.Ordinal) : order;
            }), scope.Resources, scope.Token);
            text.Seal();
            ordered.SpillResidentPages(scope.Token);
            text.SpillResidentPages(scope.Token);
            return ClipboardCaptureScope.Own(new ConductorClipboardList(ordered, text,
                ordered.Count == 0 ? 0 : ordered[0].Tick));
        }
        catch { ordered?.Dispose(); text.Dispose(); throw; }
        IEnumerable<Record> Records()
        {
            int ordinal = 0;
            foreach (ConductorEventClipboardSnapshot value in values)
            {
                scope.Token.ThrowIfCancellationRequested();
                int offset = text.Count;
                if (value.Text is { } content)
                    foreach (char character in content) text.Add(character, scope.Token);
                scope.Checkpoint(ordinal, 0, TimelineEditPreparationPhase.BuildingResult);
                yield return new(value.Kind, value.TickOffset, value.BeatsPerMinute, value.Primary,
                    value.Secondary, value.Flag, offset, value.Text?.Length ?? -1, ordinal++);
            }
        }
    }

    public int Count => _records.Count;
    public ConductorEventClipboardSnapshot this[int index]
    {
        get
        {
            Record row = _records[index];
            string? text = row.TextLength < 0 ? null : string.Create(row.TextLength, (row, _text),
                static (destination, state) =>
                {
                    Span<char> page = state._text.PageCapacity <= 4096
                        ? stackalloc char[state._text.PageCapacity]
                        : new char[state._text.PageCapacity];
                    int copied = 0;
                    while (copied < destination.Length)
                    {
                        BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
                        int position = state.row.TextOffset + copied;
                        int local = position % state._text.PageCapacity;
                        int take = Math.Min(destination.Length - copied, state._text.PageCapacity - local);
                        state._text.CopyPage(position / state._text.PageCapacity, page);
                        page.Slice(local, take).CopyTo(destination[copied..]);
                        copied += take;
                    }
                });
            return new(row.Kind, checked(row.Tick - _firstTick), row.Bpm, row.Primary, row.Secondary, row.Flag, text);
        }
    }
    public IEnumerator<ConductorEventClipboardSnapshot> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            BulkEditPreparationContext.Current?.Token.ThrowIfCancellationRequested();
            yield return this[index];
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() { _records.Dispose(); _text.Dispose(); }
    private readonly record struct Record(ConductorClipboardEventKind Kind, long Tick, decimal Bpm,
        int Primary, int Secondary, bool Flag, int TextOffset, int TextLength, int Ordinal);
}
