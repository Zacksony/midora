using Midora.Compiler;
using Midora.Domain;
using Midora.Midi;
using System.Diagnostics;

namespace Midora.Application;

public readonly record struct CompiledOnionNote(MidoraId TrackId, long StartTick, long EndTick, int Key);
public readonly record struct CompiledOnionBuildProgress(string Stage, long Completed, long Total);
public enum CompiledOnionNoteScope { AllTracks, LogicalTracks }

/// <summary>Canonical-only, FIFO-paired, disk-backed read-only note projection.</summary>
public sealed class CompiledOnionNoteIndex : IDisposable
{
    private readonly BoundedImmutableValueSource<CompiledOnionNote> _notes;
    private readonly PageBounds[] _pages;
    private readonly Dictionary<MidoraId, int[]> _trackPages;
    private readonly Dictionary<MidoraId, (ulong Fingerprint, long EndTick)> _trackContent;
    private readonly object _lifetime = new();
    private bool _retired;
    private int _readers;
    private readonly record struct Endpoint(int Address, long Ordinal, bool IsOff, long Tick, MidoraId Track);
    private readonly record struct PageBounds(long Start, long End, int MinimumKey, int MaximumKey);
    private CompiledOnionNoteIndex(BoundedImmutableValueSource<CompiledOnionNote> notes,
        PageBounds[] pages, Dictionary<MidoraId, int[]> trackPages,
        Dictionary<MidoraId, (ulong Fingerprint, long EndTick)> trackContent, long fingerprint, long endTick,
        BoundedEditResources resources)
    {
        _notes = notes; _pages = pages; _trackPages = trackPages; _trackContent = trackContent; Fingerprint = fingerprint; EndTick = endTick;
        PeakResidentBytes = resources.PeakResidentBytes;
        PeakWorkingBytes = resources.PeakWorkingBytes;
        PeakSpillBytes = resources.PeakSpillBytes;
    }
    public long Fingerprint { get; }
    public long EndTick { get; }
    public int Count => _notes.Count;
    public long PeakResidentBytes { get; }
    public long PeakWorkingBytes { get; }
    public long PeakSpillBytes { get; }
    public IReadOnlyCollection<MidoraId> TrackIds => _trackPages.Keys;
    public ulong GetTrackFingerprint(MidoraId track) => _trackContent.TryGetValue(track, out var content) ? content.Fingerprint : 0;
    public long GetTrackEndTick(MidoraId track) => _trackContent.TryGetValue(track, out var content) ? content.EndTick : 0;

    public static CompiledOnionNoteIndex Build(CanonicalCompiledResult canonical,
        CancellationToken cancellationToken = default, string? temporaryRoot = null,
        Action<CompiledOnionBuildProgress>? progress = null,
        CompiledOnionNoteScope scope = CompiledOnionNoteScope.AllTracks)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!canonical.IsConsumable || canonical.IsPartial || !canonical.Context.IsFullProject)
            throw new ArgumentException("All Tracks requires a successful full Project compilation.", nameof(canonical));
        var resources = new BoundedEditResources(new PagedEditResourceBudget(
            maximumResidentBytes: 16L * 1024 * 1024,
            maximumWorkingBytes: 32L * 1024 * 1024), temporaryRoot);
        var progressClock = Stopwatch.StartNew();
        long lastReport = -100;
        void Report(string stage, long completed, long total, bool force = false)
        {
            if (progress is null || !force && progressClock.ElapsedMilliseconds - lastReport < 100) return;
            lastReport = progressClock.ElapsedMilliseconds;
            progress(new(stage, completed, total));
        }
        var endpointComparer = Comparer<Endpoint>.Create(static (a, b) =>
        {
            int c = a.Address.CompareTo(b.Address);
            if (c == 0) c = a.Ordinal.CompareTo(b.Ordinal);
            return c != 0 ? c : a.IsOff.CompareTo(b.IsOff);
        });
        using var endpoints = BoundedEditSort.Sort(ReadEndpoints(), endpointComparer, resources, cancellationToken);
        Report("Pairing canonical notes", 0, endpoints.Count, true);
        var noteComparer = Comparer<CompiledOnionNote>.Create(static (a, b) =>
        {
            int c = a.TrackId.CompareTo(b.TrackId);
            if (c == 0) c = a.Key.CompareTo(b.Key);
            if (c == 0) c = a.StartTick.CompareTo(b.StartTick);
            return c != 0 ? c : a.EndTick.CompareTo(b.EndTick);
        });
        var sorted = BoundedEditSort.Sort(Pair(), noteComparer, resources, cancellationToken);
        BoundedImmutableValueSource<CompiledOnionNote>? source = null;
        try
        {
            Report("Indexing note pages", 0, sorted.Count, true);
            sorted.SpillResidentPages(cancellationToken);
            source = new(sorted);
            List<PageBounds> pages = [];
            Dictionary<MidoraId, List<int>> tracks = [];
            Dictionary<MidoraId, (ulong Fingerprint, long EndTick)> trackContent = [];
            int index = 0;
            long start = long.MaxValue, end = 0; int min = 127, max = 0;
            HashSet<MidoraId> pageTracks = [];
            foreach (var note in sorted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                start = Math.Min(start, note.StartTick); end = Math.Max(end, QueryEnd(note));
                min = Math.Min(min, note.Key); max = Math.Max(max, note.Key); pageTracks.Add(note.TrackId);
                if (!trackContent.TryGetValue(note.TrackId, out var content)) content = (14695981039346656037UL, 0);
                ulong hash = Mix(content.Fingerprint, unchecked((ulong)note.StartTick));
                hash = Mix(hash, unchecked((ulong)note.EndTick));
                hash = Mix(hash, (ulong)note.Key);
                trackContent[note.TrackId] = (hash, Math.Max(content.EndTick, QueryEnd(note)));
                if (++index % source.PageCapacity == 0)
                { Flush(); Report("Indexing note pages", index, sorted.Count); }
            }
            if (pageTracks.Count != 0) Flush();
            Report("Ready", sorted.Count, sorted.Count, true);
            return new(source, pages.ToArray(), tracks.ToDictionary(x => x.Key, x => x.Value.ToArray()),
                trackContent, canonical.Fingerprint, scope == CompiledOnionNoteScope.LogicalTracks
                    ? trackContent.Values.Select(c => c.EndTick).DefaultIfEmpty(0).Max() : canonical.EndTick, resources);
            void Flush()
            {
                foreach (var id in pageTracks)
                {
                    if (!tracks.TryGetValue(id, out var list)) tracks[id] = list = [];
                    list.Add(pages.Count);
                }
                pages.Add(new(start, end, min, max)); pageTracks.Clear();
                start = long.MaxValue; end = 0; min = 127; max = 0;
            }
        }
        catch { if (source is null) sorted.Dispose(); else source.Dispose(); throw; }

        IEnumerable<Endpoint> ReadEndpoints()
        {
            // Fixed MIDI address space. Counters replace unbounded queues of live NoteOn objects.
            long[] starts = new long[16 * 16 * 128], stops = new long[starts.Length];
            long processed = 0;
            Report("Reading canonical notes", 0, canonical.EndTick - canonical.StartTick, true);
            if (canonical.EndTick <= canonical.StartTick) yield break;
            foreach (var e in ReadCanonicalNotes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool on = e.Message.MessageType == MidiMessageType.NoteOn && e.Message.Byte2 != 0;
                bool off = e.Message.MessageType == MidiMessageType.NoteOff
                    || e.Message.MessageType == MidiMessageType.NoteOn && e.Message.Byte2 == 0;
                if (!on && !off) continue;
                if ((++processed & 4095) == 0)
                    Report("Reading canonical notes", e.Tick - canonical.StartTick, canonical.EndTick - canonical.StartTick);
                int address = (e.ZeroBasedPort * 16 + e.Message.ChannelNumber) * 128 + e.Message.Byte1;
                if (off && stops[address] >= starts[address]) continue; // Redundant canonical cleanup.
                yield return new(address, on ? starts[address]++ : stops[address]++, off, e.Tick, e.TrackId);
            }
            Report("Sorting canonical endpoints", 0, 0, true);
        }
        IEnumerable<CanonicalMidiRenderEvent> ReadCanonicalNotes()
        {
            // Merge the frozen Logical/resident cursor and bounded Pure MIDI projection.
            // No Project recompile or interpretation, audio filtering or monitoring inference.
            using var left = ReadMemory().GetEnumerator();
            using var right = ReadPaged().GetEnumerator();
            bool a = left.MoveNext(), b = right.MoveNext();
            while (a || b)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool takeLeft = !b || a && Compare(left.Current, right.Current) <= 0;
                yield return takeLeft ? left.Current : right.Current;
                if (takeLeft) a = left.MoveNext(); else b = right.MoveNext();
            }
        }
        IEnumerable<CanonicalMidiRenderEvent> ReadMemory()
        {
            foreach (CanonicalMidiEvent e in canonical.EnumerateResidentAndLogicalEvents(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scope == CompiledOnionNoteScope.LogicalTracks
                    && (e.Source.PureMidiTrackId != default || e.Source.MidiChannelRootId != default)) continue;
                if (!IsNote(e.Message)) continue;
                MidoraId track = e.Source.TrackId != default ? e.Source.TrackId : e.Source.PureMidiTrackId;
                yield return new(e.Tick, e.ZeroBasedPort, e.Message, track, track, e.Role,
                    e.StableOrder, e.SmfTrackOrder, e.SmfEventOrder, e.Source.DirectMidiObjectId);
            }
        }
        IEnumerable<CanonicalMidiRenderEvent> ReadPaged()
        {
            // Logical pages are read above; the logical-only index must never request Pure MIDI.
            if (scope == CompiledOnionNoteScope.LogicalTracks) yield break;
            // Windowing also avoids multi-gigabyte full-song sort runs. A single dense tick
            // still goes through the bounded sorter; the bound is not a density assumption.
            long windowTicks = Math.Max(1, canonical.TicksPerQuarterNote * 16L);
            for (long start = canonical.StartTick; start < canonical.EndTick;)
            {
                long end = start + Math.Min(windowTicks, canonical.EndTick - start);
                foreach (var page in canonical.QueryPureMidiRenderEventPages(start, end,
                    includeStateAtStart: false, cancellationToken))
                foreach (var e in page.Items)
                    if (IsNote(e.Message)) yield return e;
                start = end;
            }
        }
        IEnumerable<CompiledOnionNote> Pair()
        {
            Endpoint? pending = null;
            long processed = 0;
            foreach (var endpoint in endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((++processed & 4095) == 0) Report("Pairing canonical notes", processed, endpoints.Count);
                if (!endpoint.IsOff)
                {
                    if (pending is { } previous && previous.Track != default && canonical.EndTick > previous.Tick)
                        yield return new(previous.Track, previous.Tick, canonical.EndTick, previous.Address % 128);
                    pending = endpoint;
                }
                else if (pending is { } on && on.Address == endpoint.Address && on.Ordinal == endpoint.Ordinal)
                {
                    // Raw imported MIDI may pair NoteOn/Off at the same tick. Keep the
                    // exact zero-duration pair; the view gives it a one-pixel footprint.
                    if (on.Track != default && endpoint.Tick >= on.Tick)
                        yield return new(on.Track, on.Tick, endpoint.Tick, on.Address % 128);
                    pending = null;
                }
            }
            if (pending is { } final && final.Track != default && canonical.EndTick > final.Tick)
                yield return new(final.Track, final.Tick, canonical.EndTick, final.Address % 128);
            Report("Sorting paired notes", 0, 0, true);
        }
    }

    private static bool IsNote(MidiMessage message) => message.MessageType is MidiMessageType.NoteOn or MidiMessageType.NoteOff;
    private static ulong Mix(ulong hash, ulong value) => unchecked((hash ^ value) * 1099511628211UL);
    private static long QueryEnd(CompiledOnionNote note) => note.EndTick == note.StartTick && note.EndTick < long.MaxValue
        ? note.EndTick + 1 : note.EndTick;
    private static int Compare(CanonicalMidiRenderEvent a, CanonicalMidiRenderEvent b)
    {
        int c = a.Tick.CompareTo(b.Tick); if (c != 0) return c;
        c = a.Role.CompareTo(b.Role); if (c != 0) return c;
        c = a.ZeroBasedPort.CompareTo(b.ZeroBasedPort); if (c != 0) return c;
        c = a.Message.ChannelNumber.CompareTo(b.Message.ChannelNumber); if (c != 0) return c;
        c = a.SmfTrackOrder.CompareTo(b.SmfTrackOrder); if (c != 0) return c;
        if (a.Role == CanonicalEventRole.DirectMidi)
        { c = a.SmfEventOrder.CompareTo(b.SmfEventOrder); if (c != 0) return c; }
        c = a.StableOrder.CompareTo(b.StableOrder); if (c != 0) return c;
        c = a.TrackId.CompareTo(b.TrackId); if (c != 0) return c;
        c = a.StableObjectId.CompareTo(b.StableObjectId); if (c != 0) return c;
        return a.Message.PackedValue.CompareTo(b.Message.PackedValue);
    }

    public void Visit(MidoraId track, long start, long end, int minimumKey, int maximumKey,
        Action<CompiledOnionNote> visitor, CancellationToken cancellationToken = default)
    {
        lock (_lifetime)
        {
            if (_retired) throw new OperationCanceledException("The compiled overview was retired.");
            _readers++;
        }
        try
        {
            if (!_trackPages.TryGetValue(track, out var pages)) return;
            foreach (int pageIndex in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bounds = _pages[pageIndex];
                if (bounds.Start >= end || bounds.End <= start
                    || bounds.MaximumKey < minimumKey || bounds.MinimumKey > maximumKey) continue;
                foreach (var note in _notes.ReadPage(pageIndex).Span)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (note.TrackId == track && note.StartTick < end && QueryEnd(note) > start
                        && note.Key >= minimumKey && note.Key <= maximumKey) visitor(note);
                }
            }
        }
        finally { lock (_lifetime) { if (--_readers == 0 && _retired) _notes.Dispose(); } }
    }
    public void Dispose()
    {
        lock (_lifetime) { if (_retired) return; _retired = true; if (_readers == 0) _notes.Dispose(); }
    }
}
