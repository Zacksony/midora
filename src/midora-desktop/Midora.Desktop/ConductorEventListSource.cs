using System.Globalization;
using Midora.Domain;

namespace Midora.Desktop;

/// <summary>
/// Revision-bound, owner-data list. The merge directory stores five cursors per
/// 256 rows, never a row, string or ID dictionary for every source event.
/// All directory/page preparation is called off the dispatcher.
/// </summary>
public sealed class ConductorEventListSource : IDisposable
{
    private const int PageSize = 256;
    private readonly Func<int, ConductorEventRow>[] _read;
    private readonly Func<int, ListKey>[] _key;
    private readonly int[] _counts;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Lazy<Task<int[][]>> _directory;
    private static readonly SemaphoreSlim PreparationGate = new(1, 1);

    public ConductorEventListSource(ConductorTrack conductor)
    {
        var tempos = conductor.Tempos.CaptureQuerySnapshot();
        var signatures = conductor.TimeSignatures.CaptureQuerySnapshot();
        var keys = conductor.KeySignatures.CaptureQuerySnapshot();
        var markers = conductor.Markers.CaptureQuerySnapshot();
        ConductorEventRow? end = conductor.EndMarker is { } value
            ? new(value.Id, value.Tick, "Project End", "Hard end boundary") : null;
        _counts = [tempos.Count, signatures.Count, keys.Count, markers.Count, end is null ? 0 : 1];
        Count = checked(_counts.Sum());
        _read =
        [
            i => { var v = tempos.GetByOrdinal(i); return new(v.Id, v.Tick, "Tempo", v.BeatsPerMinute.ToString("0.######", CultureInfo.InvariantCulture) + " BPM"); },
            i => { var v = signatures.GetByOrdinal(i); return new(v.Id, v.Tick, "Time Signature", $"{v.Numerator}/{v.Denominator}"); },
            i => { var v = keys.GetByOrdinal(i); return new(v.Id, v.Tick, "Key Signature", $"{v.SharpsFlats:+0;-0;0} · {(v.IsMinor ? "Minor" : "Major")}"); },
            i => { var v = markers.GetByOrdinal(i); return new(v.Id, v.Tick, "Marker", string.IsNullOrEmpty(v.Name) ? "(unnamed)" : v.Name); },
            _ => new(end!.Id, end.Tick, "Project End", "Hard end boundary")
        ];
        _key =
        [
            i => { var v = tempos.GetByOrdinal(i); return new(v.Id, v.Tick); },
            i => { var v = signatures.GetByOrdinal(i); return new(v.Id, v.Tick); },
            i => { var v = keys.GetByOrdinal(i); return new(v.Id, v.Tick); },
            i => { var v = markers.GetByOrdinal(i); return new(v.Id, v.Tick); },
            _ => new(end!.Id, end.Tick)
        ];
        _directory = new(() => Task.Run(BuildDirectoryAsync));
    }

    // Separate test seam also permits a future typed owner-data list to reuse
    // the same sparse merge without depending on Project/UI object identity.
    internal ConductorEventListSource(int[] counts, Func<int, ConductorEventRow>[] readers)
    {
        if (counts.Length != readers.Length || counts.Any(c => c < 0))
            throw new ArgumentException("Invalid Conductor list sources.");
        _counts = (int[])counts.Clone();
        _read = (Func<int, ConductorEventRow>[])readers.Clone();
        _key = readers.Select(reader => new Func<int, ListKey>(i =>
        { var row = reader(i); return new(row.Id, row.Tick); })).ToArray();
        Count = checked(_counts.Sum());
        _directory = new(() => Task.Run(BuildDirectoryAsync));
    }

    public int Count { get; }
    internal int DirectoryEntryCount => checked((int)((Count + (long)PageSize - 1) / PageSize));
    public Task PrepareAsync() => _directory.Value;

    public async Task<ConductorEventRow[]> ReadRowsAsync(int first, int count, CancellationToken token)
    {
        if (first < 0 || first > Count || count < 0) throw new ArgumentOutOfRangeException(nameof(first));
        count = Math.Min(count, Count - first);
        if (first < PageSize && count <= PageSize - first)
        {
            // The first screen needs no merge directory. Let directory warming
            // continue independently instead of delaying every edit's first rows
            // until all million events have been walked.
            _ = _directory.Value.ContinueWith(static task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return await Task.Run(() => ReadRows([new int[_counts.Length]], first, count, token), token).ConfigureAwait(false);
        }
        int[][] directory = await _directory.Value.WaitAsync(token).ConfigureAwait(false);
        return await Task.Run(() => ReadRows(directory, first, count, token), token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<MidoraId>> ReadSelectionAsync(int first, int last, CancellationToken token)
    {
        int[][] directory = await _directory.Value.WaitAsync(token).ConfigureAwait(false);
        if (first < 0 || last < first || last >= Count) throw new ArgumentOutOfRangeException(nameof(first));
        return await Task.Run<IReadOnlyCollection<MidoraId>>(() =>
            Midora.Desktop.Presentation.Interaction.CompressedMidoraIdSet.Create(EnumerateIds()), token).ConfigureAwait(false);

        IEnumerable<MidoraId> EnumerateIds()
        {
            int page = first / PageSize;
            int[] cursors = (int[])directory[page].Clone();
            ListKey?[] heads = ReadHeads(cursors);
            for (int ordinal = page * PageSize; ordinal <= last; ordinal++)
            {
                if ((ordinal & 255) == 0) { token.ThrowIfCancellationRequested(); _cancellation.Token.ThrowIfCancellationRequested(); }
                int type = FirstType(heads);
                ListKey row = heads[type]!.Value;
                if (ordinal >= first) yield return row.Id;
                Advance(type, cursors, heads);
            }
        }
    }

    private async Task<int[][]> BuildDirectoryAsync()
    {
        CancellationToken token = _cancellation.Token;
        await PreparationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int[][] directory = new int[DirectoryEntryCount][];
            int[] cursors = new int[_counts.Length];
            ListKey?[] heads = ReadHeads(cursors);
            for (int ordinal = 0; ordinal < Count; ordinal++)
            {
                if ((ordinal % PageSize) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    directory[ordinal / PageSize] = (int[])cursors.Clone();
                }
                Advance(FirstType(heads), cursors, heads);
            }
            return directory;
        }
        finally { PreparationGate.Release(); }
    }

    private ConductorEventRow[] ReadRows(int[][] directory, int first, int count, CancellationToken token)
    {
        if (count == 0) return [];
        int page = first / PageSize;
        int[] cursors = (int[])directory[page].Clone();
        ListKey?[] heads = ReadHeads(cursors);
        ConductorEventRow[] rows = new ConductorEventRow[count];
        for (int ordinal = page * PageSize; ordinal < first + count; ordinal++)
        {
            if ((ordinal & 255) == 0) { token.ThrowIfCancellationRequested(); _cancellation.Token.ThrowIfCancellationRequested(); }
            int type = FirstType(heads);
            if (ordinal >= first) rows[ordinal - first] = _read[type](cursors[type]);
            Advance(type, cursors, heads);
        }
        return rows;
    }

    private ListKey?[] ReadHeads(int[] cursors) => cursors.Select((cursor, type) =>
        cursor < _counts[type] ? (ListKey?)_key[type](cursor) : null).ToArray();

    private void Advance(int type, int[] cursors, ListKey?[] heads)
    {
        int next = ++cursors[type];
        heads[type] = next < _counts[type] ? _key[type](next) : null;
    }

    private static int FirstType(ListKey?[] heads)
    {
        int best = -1;
        for (int type = 0; type < heads.Length; type++)
            if (heads[type] is { } row && (best < 0 || row.Tick < heads[best]!.Value.Tick)) best = type;
        return best;
    }

    public void Dispose() => _cancellation.Cancel();
    private readonly record struct ListKey(MidoraId Id, long Tick);
}
